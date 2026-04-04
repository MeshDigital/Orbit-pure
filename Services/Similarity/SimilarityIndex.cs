using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// In-memory cosine-similarity index over track embeddings stored in <see cref="AudioAnalysisEntity"/>.
///
/// Architecture notes:
///   - Vectors are loaded lazily on first query and cached for the lifetime of the service.
///   - Thread-safe via <see cref="SemaphoreSlim"/>; concurrent queries share the cached index.
///   - Similarity search is O(n) brute-force, which handles up to ~50k tracks in &lt;100ms on modern hardware.
///     For libraries &gt; 100k tracks replace with an HNSW index (e.g., HNSWlib via P/Invoke).
///   - Vectors are dimension-agnostic — works with both the current 128-dim and future 2048-dim embeddings.
/// </summary>
public sealed class SimilarityIndex
{
    private readonly ILogger<SimilarityIndex> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private List<IndexEntry>? _index;
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
    /// </summary>
    /// <param name="queryHash">TrackUniqueHash of the seed track.</param>
    /// <param name="topN">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Propagated to DB and CPU-bound work.</param>
    public async Task<IReadOnlyList<SimilarTrack>> GetSimilarTracksAsync(
        string queryHash,
        int topN = 10,
        CancellationToken cancellationToken = default)
    {
        var index = await GetOrBuildIndexAsync(cancellationToken);

        var queryEntry = index.FirstOrDefault(e => e.TrackHash == queryHash);
        if (queryEntry is null)
        {
            _logger.LogWarning("[SimilarityIndex] No embedding found for {Hash}", queryHash);
            return Array.Empty<SimilarTrack>();
        }

        // Cosine similarity is CPU-bound; offload from any caller that already holds a UI/async context.
        var results = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return index
                .Where(e => e.TrackHash != queryHash)
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
    public void InvalidateIndex() => _indexBuiltAt = DateTime.MinValue;

    /// <summary>Returns current index size (number of tracks with embeddings).</summary>
    public int IndexSize => _index?.Count ?? 0;

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
                // Load only rows that actually have an embedding blob.
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
            _indexBuiltAt = DateTime.UtcNow;

            _logger.LogInformation("[SimilarityIndex] Index ready — {Count} tracks indexed.", entries.Count);
            return _index;
        }
        finally
        {
            _loadLock.Release();
        }
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

    // ── Math ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the cosine similarity between two equal-length float vectors.
    /// Returns a value in [-1, 1]; 1 = identical direction.
    ///
    /// Uses a single-pass accumulation loop for performance — avoids allocation
    /// overhead that MathNet.Numerics DenseVector would introduce per candidate.
    /// </summary>
    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-10 ? 0.0 : dot / denom;
    }
}
