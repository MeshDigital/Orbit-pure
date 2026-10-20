using System;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.Enrichment;

/// <summary>
/// One metadata source (local tags, Deezer, MusicBrainz, Discogs, AcoustID, Spotify, Beatport).
/// Implementations never throw from <see cref="LookupAsync"/> except for cancellation: every
/// failure comes back as a <see cref="ProviderLookup"/> status the orchestrator logs and shows.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>Stable id used in settings, provenance and the cache ("deezer").</summary>
    string Name { get; }
    string DisplayName { get; }
    /// <summary>What it can fill. The orchestrator skips it when none of these are missing.</summary>
    MetadataField Supplies { get; }
    /// <summary>One line for Settings: what it gives and its limits.</summary>
    string Description { get; }
    /// <summary>Shared limiter, breaker and counters.</summary>
    ProviderRuntime Runtime { get; }
    /// <summary>Key/tool/connection state (enabled-in-settings is checked by the orchestrator).</summary>
    ProviderAvailability GetAvailability();
    Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct);
}

/// <summary>Per-provider limiter, circuit breaker and counters.</summary>
public sealed class ProviderRuntime
{
    private int _requests;
    private int _matched;
    private int _cacheHits;

    public ProviderRuntime(string name, RatePolicy policy, Func<DateTime>? clock = null)
    {
        Name = name;
        Limiter = new ProviderRateLimiter(policy, clock);
        Breaker = new ProviderCircuitBreaker(clock);
    }

    public string Name { get; }
    public ProviderRateLimiter Limiter { get; }
    public ProviderCircuitBreaker Breaker { get; }
    public int Requests => Volatile.Read(ref _requests);
    public int Matched => Volatile.Read(ref _matched);
    public int CacheHits => Volatile.Read(ref _cacheHits);
    public string? LastError { get; set; }

    public void CountRequest() => Interlocked.Increment(ref _requests);
    public void CountMatch() => Interlocked.Increment(ref _matched);
    public void CountCacheHit() => Interlocked.Increment(ref _cacheHits);

    public ProviderStatusSnapshot Snapshot(string displayName, ProviderAvailability availability) =>
        new(Name, displayName, availability, Breaker.IsOpen && !Breaker.IsDisabledForSession, Breaker.OpenUntil,
            Requests, Matched, Breaker.RateLimitPauses, LastError);
}
