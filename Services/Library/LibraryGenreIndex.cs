using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Services.Library;

/// <summary>
/// Stable key identifying a genre node in <see cref="LibraryGenreIndex"/> (Epic #561 / #562 GV1).
/// Club buckets (DrumAndBass, Dubstep, Techno, House) form level 1 with subgenres as level 2;
/// Other holds non-club tag names as level 2; Unclassified represents <see cref="GenreDecision.None"/>.
/// </summary>
public readonly record struct GenreKey(GenreBucket Bucket, string? Subgenre = null, bool IsUnclassified = false)
{
    public static readonly GenreKey Unclassified = new(GenreBucket.Other, null, IsUnclassified: true);

    public string DisplayName => IsUnclassified
        ? "Unclassified"
        : string.IsNullOrWhiteSpace(Subgenre)
            ? GenreTaxonomy.CanonicalDisplay(Bucket, null)
            : GenreTaxonomy.CanonicalDisplay(Bucket, Subgenre);

    public bool Matches(GenreKey trackKey)
    {
        if (IsUnclassified)
            return trackKey.IsUnclassified;
        if (trackKey.IsUnclassified)
            return false;
        if (Bucket != trackKey.Bucket)
            return false;
        if (string.IsNullOrWhiteSpace(Subgenre))
            return true; // Bucket-level key matches all subgenres in this bucket
        return string.Equals(Subgenre, trackKey.Subgenre, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record GenreTreeNode(
    GenreKey Key,
    string DisplayName,
    int TrackCount,
    IReadOnlyList<GenreTreeNode> Children);

/// <summary>
/// Singleton in-memory genre index built from <see cref="TrackGenreResolver"/> (Epic #561 / #562 GV1).
/// One resolved genre per library track, so every surface (Library GENRES tree, filter chip,
/// Genre Galaxy, smart crates) reads the same source of truth and counts sum to the library total.
/// </summary>
public sealed class LibraryGenreIndex : IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<LibraryGenreIndex> _logger;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _gate = new();

    private Dictionary<string, GenreKey> _trackToKey = new(StringComparer.Ordinal);
    private bool _disposed;

    public bool IsReady { get; private set; }
    public bool IsBuilding { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler? Changed;

    public LibraryGenreIndex(
        IDbContextFactory<AppDbContext> dbFactory,
        IEventBus? eventBus,
        ILogger<LibraryGenreIndex> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;

        // Constructor must never throw (would poison the DI singleton for the app's lifetime).
        try
        {
            if (eventBus != null)
            {
                _subscriptions.Add(eventBus.GetEvent<TrackAnalysisUpdatedEvent>()
                    .Subscribe(e => { if (!string.IsNullOrWhiteSpace(e.TrackGlobalId)) _ = RefreshSingleSafeAsync(e.TrackGlobalId); }));
                _subscriptions.Add(eventBus.GetEvent<TrackMetadataUpdatedEvent>()
                    .Subscribe(e => { if (!string.IsNullOrWhiteSpace(e.TrackGlobalId)) _ = RefreshSingleSafeAsync(e.TrackGlobalId); }));
                _subscriptions.Add(eventBus.GetEvent<LibraryMetadataEnrichedEvent>()
                    .Subscribe(e => { _ = BuildAsync(); }));
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "LibraryGenreIndex failed to subscribe to refresh events; degraded to manual rebuilds.");
        }
    }

    public async Task BuildAsync(CancellationToken ct = default)
    {
        if (IsBuilding) return;
        IsBuilding = true;
        LastError = null;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var hashes = await db.LibraryEntries.AsNoTracking()
                .Where(e => !string.IsNullOrEmpty(e.UniqueHash))
                .Select(e => e.UniqueHash)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var next = new Dictionary<string, GenreKey>(hashes.Count, StringComparer.Ordinal);
            const int batchSize = 500;

            for (int i = 0; i < hashes.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = hashes.GetRange(i, Math.Min(batchSize, hashes.Count - i));
                var contexts = await TrackGenreContextQuery.LoadManyAsync(db, chunk, ct).ConfigureAwait(false);
                var features = await db.AudioFeatures.AsNoTracking()
                    .Where(f => chunk.Contains(f.TrackUniqueHash))
                    .ToDictionaryAsync(f => f.TrackUniqueHash, StringComparer.Ordinal, ct)
                    .ConfigureAwait(false);

                foreach (var hash in chunk)
                {
                    contexts.TryGetValue(hash, out var ctx);
                    AudioGenreOpinion? opinion = features.TryGetValue(hash, out var feat)
                        ? AudioGenreOpinion.FromFeatures(feat)
                        : null;
                    next[hash] = ResolveKey(ctx, opinion);
                }
            }

            lock (_gate)
            {
                _trackToKey = next;
            }
            IsReady = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "LibraryGenreIndex.BuildAsync failed.");
        }
        finally
        {
            IsBuilding = false;
        }
    }

    public async Task RefreshTracksAsync(IReadOnlyCollection<string> trackHashes, CancellationToken ct = default)
    {
        if (trackHashes == null || trackHashes.Count == 0) return;

        try
        {
            var distinct = trackHashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count == 0) return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var existingSet = (await db.LibraryEntries.AsNoTracking()
                .Where(e => distinct.Contains(e.UniqueHash))
                .Select(e => e.UniqueHash)
                .ToListAsync(ct)
                .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

            var contexts = await TrackGenreContextQuery.LoadManyAsync(db, distinct, ct).ConfigureAwait(false);
            var features = await db.AudioFeatures.AsNoTracking()
                .Where(f => distinct.Contains(f.TrackUniqueHash))
                .ToDictionaryAsync(f => f.TrackUniqueHash, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

            bool anyChanged = false;
            lock (_gate)
            {
                foreach (var hash in distinct)
                {
                    if (!existingSet.Contains(hash))
                    {
                        if (_trackToKey.Remove(hash))
                            anyChanged = true;
                        continue;
                    }

                    contexts.TryGetValue(hash, out var ctx);
                    AudioGenreOpinion? opinion = features.TryGetValue(hash, out var feat)
                        ? AudioGenreOpinion.FromFeatures(feat)
                        : null;
                    var key = ResolveKey(ctx, opinion);
                    if (!_trackToKey.TryGetValue(hash, out var prev) || prev != key)
                    {
                        _trackToKey[hash] = key;
                        anyChanged = true;
                    }
                }
            }

            if (anyChanged)
                Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LibraryGenreIndex.RefreshTracksAsync failed for {Count} tracks", trackHashes.Count);
        }
    }

    public GenreKey? GetKey(string? trackHash)
    {
        if (string.IsNullOrWhiteSpace(trackHash)) return null;
        lock (_gate)
        {
            return _trackToKey.TryGetValue(trackHash, out var key) ? key : null;
        }
    }

    public IReadOnlySet<string> GetHashes(GenreKey filter)
    {
        lock (_gate)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (hash, key) in _trackToKey)
            {
                if (filter.Matches(key))
                    result.Add(hash);
            }
            return result;
        }
    }

    public IReadOnlyList<GenreTreeNode> GetTree()
    {
        List<KeyValuePair<string, GenreKey>> snapshot;
        lock (_gate)
        {
            snapshot = _trackToKey.ToList();
        }

        if (snapshot.Count == 0) return Array.Empty<GenreTreeNode>();

        var nodes = new List<GenreTreeNode>();

        // 1. Club buckets (DrumAndBass, Dubstep, Techno, House) as level 1 with subgenres as level 2
        foreach (var bucket in new[] { GenreBucket.DrumAndBass, GenreBucket.Dubstep, GenreBucket.Techno, GenreBucket.House })
        {
            var inBucket = snapshot.Where(kv => !kv.Value.IsUnclassified && kv.Value.Bucket == bucket).ToList();
            if (inBucket.Count == 0) continue;

            var children = inBucket
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Subgenre))
                .GroupBy(kv => kv.Value.Subgenre!, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var key = new GenreKey(bucket, g.First().Value.Subgenre);
                    return new GenreTreeNode(key, key.DisplayName, g.Count(), Array.Empty<GenreTreeNode>());
                })
                .OrderByDescending(c => c.TrackCount)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var bucketKey = new GenreKey(bucket);
            nodes.Add(new GenreTreeNode(bucketKey, bucketKey.DisplayName, inBucket.Count, children));
        }

        // 2. Other (non-club genres) with tag names as level 2
        var other = snapshot.Where(kv => !kv.Value.IsUnclassified && kv.Value.Bucket == GenreBucket.Other).ToList();
        if (other.Count > 0)
        {
            var otherChildren = other
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Subgenre))
                .GroupBy(kv => kv.Value.Subgenre!, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var key = new GenreKey(GenreBucket.Other, g.First().Value.Subgenre);
                    return new GenreTreeNode(key, key.DisplayName, g.Count(), Array.Empty<GenreTreeNode>());
                })
                .OrderByDescending(c => c.TrackCount)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var otherKey = new GenreKey(GenreBucket.Other);
            nodes.Add(new GenreTreeNode(otherKey, otherKey.DisplayName, other.Count, otherChildren));
        }

        // 3. Unclassified (GenreDecision.None)
        int unclassifiedCount = snapshot.Count(kv => kv.Value.IsUnclassified);
        if (unclassifiedCount > 0)
        {
            nodes.Add(new GenreTreeNode(GenreKey.Unclassified, "Unclassified", unclassifiedCount, Array.Empty<GenreTreeNode>()));
        }

        return nodes;
    }

    public static GenreKey ResolveKey(TrackGenreContext? context, AudioGenreOpinion? audio)
    {
        var resolved = TrackGenreResolver.Resolve(context, audio);
        if (resolved.Decision == GenreDecision.None)
            return GenreKey.Unclassified;
        return new GenreKey(resolved.Bucket, resolved.Subgenre);
    }

    private async Task RefreshSingleSafeAsync(string hash)
    {
        if (_disposed) return;
        await RefreshTracksAsync(new[] { hash }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }
}
