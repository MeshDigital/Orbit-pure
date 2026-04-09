using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hnsw;
using Hnsw.RamStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Similarity;

/// <summary>
/// A track entry held in the in-memory similarity index.
/// </summary>
internal sealed record IndexEntry(string TrackHash, float[] Vector);

/// <summary>
/// Result item from a similarity query.
/// </summary>
public sealed record SimilarTrack(string TrackHash, double Score);

/// <summary>
/// In-memory embedding index over track embeddings stored in <see cref="AudioAnalysisEntity"/>.
///
/// Architecture:
///   - For libraries up to <see cref="HnswThreshold"/> tracks the index falls back to brute-force
///     cosine similarity (simpler, zero-overhead, and cache-friendly for small collections).
///   - Above the threshold an HNSW (Hierarchical Navigable Small Worlds) approximate nearest-
///     neighbour graph is built using <c>HnswLite</c> + <c>HnswLite.RamStorage</c>.
///   - The index is rebuilt lazily and cached with a configurable TTL.
///   - All public methods are thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class SimilarityIndex : IDisposable
{
    /// <summary>
    /// Library size above which the HNSW graph is used instead of brute-force search.
    /// Chosen so that brute-force stays well under 50 ms for typical embedding dimensions.
    /// </summary>
    public const int HnswThreshold = 5_000;

    private readonly ILogger<SimilarityIndex> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private List<IndexEntry>? _index;                         // flat list for brute-force + fallback
    private HnswIndex? _hnswIndex;                            // populated when _index.Count > HnswThreshold
    private Dictionary<Guid, string>? _guidToHash;            // maps HNSW insertion GUID → track hash
    private Dictionary<string, Guid>? _hashToGuid;            // reverse: hash → GUID for query lookup
    private DateTime _indexBuiltAt = DateTime.MinValue;

    /// <summary>How long the in-memory index is considered fresh before a lazy reload.</summary>
    public TimeSpan IndexTtl { get; set; } = TimeSpan.FromHours(1);

    public SimilarityIndex(ILogger<SimilarityIndex> logger)
    {
        _logger = logger;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the top-N most similar tracks to <paramref name="queryHash"/>.
    /// The query track itself is excluded from results.
    /// Optionally supply <paramref name="excludeHashes"/> to filter out tracks
    /// already present in the target playlist (duplicate exclusion).
    /// </summary>
    public async Task<IReadOnlyList<SimilarTrack>> GetSimilarTracksAsync(
        string queryHash,
        int topN = 10,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? excludeHashes = null)
    {
        var exclude = excludeHashes is null
            ? ImmutableHashSet<string>.Empty
            : ImmutableHashSet.CreateRange(excludeHashes);
        var index = await GetOrBuildIndexAsync(cancellationToken);

        var queryEntry = index.FirstOrDefault(e => e.TrackHash == queryHash);
        if (queryEntry is null)
        {
            _logger.LogWarning("[SimilarityIndex] No embedding found for {Hash}", queryHash);
            return Array.Empty<SimilarTrack>();
        }

        if (_hnswIndex is not null && _guidToHash is not null && _hashToGuid is not null)
        {
            // HNSW path — approximate nearest neighbours, O(log n)
            return await QueryHnswAsync(queryHash, queryEntry.Vector, topN, cancellationToken, exclude);
        }

        // Brute-force path — exact cosine similarity, O(n)
        var results = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return index
                .Where(e => e.TrackHash != queryHash && !exclude.Contains(e.TrackHash))
                .Select(e => new SimilarTrack(e.TrackHash, CosineSimilarity(queryEntry.Vector, e.Vector)))
                .OrderByDescending(r => r.Score)
                .Take(topN)
                .ToList();
        }, cancellationToken);

        return results;
    }

    /// <summary>
    /// Forces a full reload of the index from the database on the next query.
    /// Call this after bulk analysis runs add new embeddings.
    /// </summary>
    public void InvalidateIndex()
    {
        _indexBuiltAt = DateTime.MinValue;
        _hnswIndex = null;
        _guidToHash = null;
        _hashToGuid = null;
    }

    /// <summary>Returns current index size (number of tracks with embeddings).</summary>
    public int IndexSize => _index?.Count ?? 0;

    // ── HNSW query ─────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SimilarTrack>> QueryHnswAsync(
        string queryHash,
        float[] queryVector,
        int topN,
        CancellationToken ct,
        ImmutableHashSet<string>? exclude = null)
    {
        // Fetch topN+1 so we can drop the self-match
        var queryList = new List<float>(queryVector);
        var hits = await _hnswIndex!.GetTopKAsync(queryList, topN + 1, null, ct);

        return hits
            .Where(r => _guidToHash!.TryGetValue(r.GUID, out var h) && h != queryHash
                        && (exclude is null || !exclude.Contains(h!)))
            .Take(topN)
            .Select(r =>
            {
                _guidToHash!.TryGetValue(r.GUID, out var hash);
                // CosineDistance = 1 - cosineSimilarity → convert back
                return new SimilarTrack(hash!, 1.0 - r.Distance);
            })
            .ToList();
    }

    // ── Index management ───────────────────────────────────────────────────

    private async Task<List<IndexEntry>> GetOrBuildIndexAsync(CancellationToken ct)
    {
        // Fast path: valid cached index.
        if (_index is not null && DateTime.UtcNow - _indexBuiltAt < IndexTtl)
            return _index;

        await _loadLock.WaitAsync(ct);
        try
        {
            // Double-checked locking.
            if (_index is not null && DateTime.UtcNow - _indexBuiltAt < IndexTtl)
                return _index;

            _logger.LogInformation("[SimilarityIndex] Building in-memory embedding index…");

            List<IndexEntry> entries;
            using (var db = new AppDbContext())
            {
                var rows = await db.AudioAnalysis
                    .Where(a => a.VectorEmbeddingJson != null)
                    .Select(a => new { a.TrackUniqueHash, a.VectorEmbeddingJson })
                    .ToListAsync(ct);

                entries = rows
                    .Select(r =>
                    {
                        var vec = TryDeserialize(r.VectorEmbeddingJson!);
                        return vec is null ? null : new IndexEntry(r.TrackUniqueHash, vec);
                    })
                    .Where(e => e is not null)
                    .Select(e => e!)
                    .ToList();
            }

            _index = entries;
            _hnswIndex = null;
            _guidToHash = null;
            _hashToGuid = null;

            // Build HNSW graph when the library is large enough to benefit from ANN
            if (entries.Count > HnswThreshold)
            {
                _logger.LogInformation(
                    "[SimilarityIndex] {Count} tracks — building HNSW graph…", entries.Count);
                (_hnswIndex, _guidToHash, _hashToGuid) = await BuildHnswAsync(entries, ct);
            }

            _indexBuiltAt = DateTime.UtcNow;
            _logger.LogInformation(
                "[SimilarityIndex] Index ready — {Count} tracks, mode={Mode}.",
                entries.Count,
                _hnswIndex is not null ? "HNSW" : "BruteForce");
            return _index;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static async Task<(HnswIndex, Dictionary<Guid, string>, Dictionary<string, Guid>)>
        BuildHnswAsync(List<IndexEntry> entries, CancellationToken ct)
    {
        int dim = entries[0].Vector.Length;
        var storage      = new RamHnswStorage();
        var layerStorage = new RamHnswLayerStorage();

        var hnsw = new HnswIndex(dim, storage, layerStorage)
        {
            M = 16,
            EfConstruction = 200,
            DistanceFunction = new CosineDistance()
        };

        var guidToHash = new Dictionary<Guid, string>(entries.Count);
        var hashToGuid = new Dictionary<string, Guid>(entries.Count);

        await Task.Run(async () =>
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var guid = Guid.NewGuid();
                var vec  = new List<float>(entries[i].Vector);
                await hnsw.AddAsync(guid, vec, ct);
                guidToHash[guid] = entries[i].TrackHash;
                hashToGuid[entries[i].TrackHash] = guid;
            }
        }, ct);

        return (hnsw, guidToHash, hashToGuid);
    }

    private static float[]? TryDeserialize(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<float[]>(json);
        }
        catch
        {
            return null;
        }
    }

    // ── Math (brute-force path) ────────────────────────────────────────────

    /// <summary>
    /// Cosine similarity between two equal-length float vectors.
    /// Returns a value in [-1, 1]; 1 = identical direction.
    /// </summary>
    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-10 ? 0.0 : dot / denom;
    }

    public void Dispose()
    {
        _loadLock.Dispose();
    }
}
