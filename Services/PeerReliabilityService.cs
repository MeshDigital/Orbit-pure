using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

public sealed class PeerReliabilityService
{
    private readonly ConcurrentDictionary<string, PeerStats> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly DatabaseService _databaseService;

    public PeerReliabilityService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
        LoadFromDatabase();
    }

    public void RecordSearchCandidate(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var peer = GetOrCreate(username);
        Interlocked.Increment(ref peer.SearchCandidates);
        Interlocked.Exchange(ref peer.LastSeenTicks, DateTime.UtcNow.Ticks);
        // Fire-and-forget: SaveToDatabase does a synchronous EF Core upsert, which would
        // otherwise throttle this call's hot path (e.g. once per Soulseek search result,
        // 50-200 per search) on DB latency.
        _ = Task.Run(() => SaveToDatabase(username));
    }

    public void RecordDownloadStarted(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var peer = GetOrCreate(username);
        Interlocked.Increment(ref peer.DownloadStarts);
        Interlocked.Exchange(ref peer.LastSeenTicks, DateTime.UtcNow.Ticks);
        // Fire-and-forget: SaveToDatabase does a synchronous EF Core upsert, which would
        // otherwise throttle this call's hot path (e.g. once per Soulseek search result,
        // 50-200 per search) on DB latency.
        _ = Task.Run(() => SaveToDatabase(username));
    }

    public void RecordProgress(string? username, long bytesDelta)
    {
        if (string.IsNullOrWhiteSpace(username) || bytesDelta <= 0) return;
        var peer = GetOrCreate(username);
        Interlocked.Add(ref peer.BytesTransferred, bytesDelta);
        Interlocked.Exchange(ref peer.LastSeenTicks, DateTime.UtcNow.Ticks);
        // Save less frequently for progress
    }

    public void RecordDownloadCompleted(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var peer = GetOrCreate(username);
        Interlocked.Increment(ref peer.DownloadCompletions);
        Interlocked.Exchange(ref peer.LastSeenTicks, DateTime.UtcNow.Ticks);
        // Fire-and-forget: SaveToDatabase does a synchronous EF Core upsert, which would
        // otherwise throttle this call's hot path (e.g. once per Soulseek search result,
        // 50-200 per search) on DB latency.
        _ = Task.Run(() => SaveToDatabase(username));
    }

    public void RecordDownloadFailed(string? username, bool stalled)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var peer = GetOrCreate(username);
        Interlocked.Increment(ref peer.DownloadFailures);
        if (stalled)
        {
            Interlocked.Increment(ref peer.StallFailures);
        }
        Interlocked.Exchange(ref peer.LastSeenTicks, DateTime.UtcNow.Ticks);
        // Fire-and-forget: SaveToDatabase does a synchronous EF Core upsert, which would
        // otherwise throttle this call's hot path (e.g. once per Soulseek search result,
        // 50-200 per search) on DB latency.
        _ = Task.Run(() => SaveToDatabase(username));
    }

    public double GetReliabilityScore(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return 0.50;
        if (!_peers.TryGetValue(username, out var peer)) return 0.50;

        var starts = Math.Max(1, Interlocked.Read(ref peer.DownloadStarts));
        var completions = Interlocked.Read(ref peer.DownloadCompletions);
        var failures = Interlocked.Read(ref peer.DownloadFailures);
        var stalls = Interlocked.Read(ref peer.StallFailures);
        var bytes = Interlocked.Read(ref peer.BytesTransferred);

        var completionRate = Math.Clamp((double)completions / starts, 0.0, 1.0);
        var stallRate = failures > 0 ? Math.Clamp((double)stalls / failures, 0.0, 1.0) : 0.0;

        var completedCount = Math.Max(1, completions);
        var avgCompletionBytes = (double)bytes / completedCount;
        var throughputFactor = Math.Clamp(avgCompletionBytes / (10 * 1024 * 1024), 0.0, 1.0);

        var score = 0.20 + (completionRate * 0.55) + ((1.0 - stallRate) * 0.15) + (throughputFactor * 0.10);

        if (starts < 3)
        {
            score = Math.Max(score, 0.45);
        }

        // Phase 9: De-prioritize bad peers
        if (starts >= 5 && completions == 0)
        {
            score = 0.01; // Very low score for peers with 100% failure rate over 5+ attempts
        }

        return Math.Clamp(score, 0.05, 0.99);
    }

    public PeerGlobalSnapshot GetGlobalSnapshot()
    {
        var totalStarts = _peers.Values.Sum(p => Interlocked.Read(ref p.DownloadStarts));
        var totalCompletions = _peers.Values.Sum(p => Interlocked.Read(ref p.DownloadCompletions));
        var totalFailures = _peers.Values.Sum(p => Interlocked.Read(ref p.DownloadFailures));
        var totalStalls = _peers.Values.Sum(p => Interlocked.Read(ref p.StallFailures));

        var completionRatio = totalStarts > 0 ? (double)totalCompletions / totalStarts : 1.0;
        var stallRatio = totalFailures > 0 ? (double)totalStalls / totalFailures : 0.0;

        return new PeerGlobalSnapshot(
            PeersTracked: _peers.Count,
            TotalStarts: totalStarts,
            TotalCompletions: totalCompletions,
            TotalFailures: totalFailures,
            TotalStalls: totalStalls,
            CompletionRatio: Math.Clamp(completionRatio, 0.0, 1.0),
            StallRatio: Math.Clamp(stallRatio, 0.0, 1.0));
    }

    /// <summary>Per-peer aggregate stats, for the Users/Contacts page and per-user profile Overview tab.</summary>
    public PeerUserSnapshot? GetSnapshot(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || !_peers.TryGetValue(username, out var peer))
            return null;

        return new PeerUserSnapshot(
            username,
            Interlocked.Read(ref peer.SearchCandidates),
            Interlocked.Read(ref peer.DownloadStarts),
            Interlocked.Read(ref peer.DownloadCompletions),
            Interlocked.Read(ref peer.DownloadFailures),
            Interlocked.Read(ref peer.StallFailures),
            Interlocked.Read(ref peer.BytesTransferred),
            new DateTime(Interlocked.Read(ref peer.LastSeenTicks), DateTimeKind.Utc));
    }

    /// <summary>Every username this service has ever tracked stats for — the row source for the Users/Contacts page.</summary>
    public IReadOnlyList<string> GetKnownUsernames() => _peers.Keys.ToList();

    // Unbounded, this dictionary grows forever over a long session (a single search can surface
    // 50-200 distinct usernames). Cap it and evict the least-recently-seen peers in a batch once
    // the cap is exceeded, rather than growing forever.
    private const int MaxTrackedPeers = 2000;
    private const int EvictionBatchSize = 200;

    private PeerStats GetOrCreate(string username)
    {
        if (_peers.TryGetValue(username, out var existing)) return existing;

        var created = _peers.GetOrAdd(username, static _ => new PeerStats());

        if (_peers.Count > MaxTrackedPeers)
        {
            EvictLeastRecentlySeenPeers();
        }

        return created;
    }

    private void EvictLeastRecentlySeenPeers()
    {
        try
        {
            var oldest = _peers
                .OrderBy(kv => Interlocked.Read(ref kv.Value.LastSeenTicks))
                .Take(EvictionBatchSize)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldest)
            {
                _peers.TryRemove(key, out _);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup — never let eviction itself become a new failure mode.
        }
    }

    private void LoadFromDatabase()
    {
        try
        {
            var entities = _databaseService.GetPeerReliabilityStats();
            foreach (var entity in entities)
            {
                var stats = new PeerStats
                {
                    SearchCandidates = entity.SearchCandidates,
                    DownloadStarts = entity.DownloadStarts,
                    DownloadCompletions = entity.DownloadCompletions,
                    DownloadFailures = entity.DownloadFailures,
                    StallFailures = entity.StallFailures,
                    BytesTransferred = entity.BytesTransferred,
                    LastSeenTicks = entity.LastSeenTicks
                };
                _peers[entity.Username] = stats;
            }
        }
        catch (Exception)
        {
            // Log or handle
        }
    }

    private void SaveToDatabase(string username)
    {
        try
        {
            var stats = _peers[username];
            var entity = new PeerReliabilityEntity
            {
                Username = username,
                SearchCandidates = stats.SearchCandidates,
                DownloadStarts = stats.DownloadStarts,
                DownloadCompletions = stats.DownloadCompletions,
                DownloadFailures = stats.DownloadFailures,
                StallFailures = stats.StallFailures,
                BytesTransferred = stats.BytesTransferred,
                LastSeenTicks = stats.LastSeenTicks,
                LastUpdated = DateTime.UtcNow
            };
            _databaseService.UpsertPeerReliability(entity);
        }
        catch (Exception)
        {
            // Log or handle
        }
    }

    private sealed class PeerStats
    {
        public long SearchCandidates;
        public long DownloadStarts;
        public long DownloadCompletions;
        public long DownloadFailures;
        public long StallFailures;
        public long BytesTransferred;
        public long LastSeenTicks;
    }
}

public readonly record struct PeerGlobalSnapshot(
    int PeersTracked,
    long TotalStarts,
    long TotalCompletions,
    long TotalFailures,
    long TotalStalls,
    double CompletionRatio,
    double StallRatio);

public readonly record struct PeerUserSnapshot(
    string Username,
    long SearchCandidates,
    long DownloadStarts,
    long DownloadCompletions,
    long DownloadFailures,
    long StallFailures,
    long BytesTransferred,
    DateTime LastSeenUtc);
