using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Services.Discovery;

namespace SLSKDONET.Services.Enrichment.Providers;

/// <summary>
/// Beatport's public search pages (no public API; scraped via <see cref="BeatportCatalogClient"/>).
/// Experimental and OFF by default: page scraping is fragile and Beatport rate-limits it, so this
/// runs at one request every 3 s and only after every other provider. Gives label, genre,
/// release date and cover. Its BPM/key are hints only and are never stored.
/// </summary>
public sealed class BeatportProvider : IMetadataProvider
{
    public const string ProviderName = "beatport";
    public static readonly RatePolicy Policy = new(1, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500), 1);

    private readonly BeatportCatalogClient _client;

    public BeatportProvider(BeatportCatalogClient client)
    {
        _client = client;
        Runtime = new ProviderRuntime(ProviderName, Policy);
    }

    public string Name => ProviderName;
    public string DisplayName => "Beatport";
    public string Description => "Experimental: label, genre, release date and cover from Beatport's public pages. Slow (1 request / 3 s) and may be rate-limited.";
    public MetadataField Supplies => MetadataField.Artwork | MetadataField.Label | MetadataField.Genre | MetadataField.ReleaseDate | MetadataField.Album;
    public ProviderRuntime Runtime { get; }
    public ProviderAvailability GetAvailability() =>
        Runtime.Breaker.IsDisabledForSession ? ProviderAvailability.Blocked : ProviderAvailability.Ready;

    public async Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        if (track.HasWeakIdentity || string.IsNullOrWhiteSpace(track.Title)) return ProviderLookup.NoMatch();
        if (Runtime.Breaker.OpenUntil is { } until) return ProviderLookup.Limited(until - DateTime.UtcNow, "paused");

        await Runtime.Limiter.WaitAsync(ct).ConfigureAwait(false);
        Runtime.CountRequest();
        var hits = await _client.SearchTracksAsync($"{PlaylistDiscoveryService.PrimaryArtist(track.Artist)} {track.Title}", ct).ConfigureAwait(false);
        // The client returns nothing (and logs) when Beatport blocks or changes its pages.
        if (hits.Count == 0)
        {
            Runtime.Breaker.RecordFailure();
            return ProviderLookup.NoMatch();
        }
        Runtime.Breaker.RecordSuccess();

        var best = hits
            .Select(h => (h.Candidate, Score: EnrichmentMatch.Score(track.Artist, track.Title, track.DurationSeconds,
                h.Candidate.Artist, h.Candidate.SearchTitle, h.Candidate.DurationSeconds)))
            .OrderByDescending(x => x.Score)
            .First();
        if (!EnrichmentMatch.Accept(best.Score)) return ProviderLookup.NoMatch();

        var d = best.Candidate;
        var c = new MetadataCandidate
        {
            Confidence = Math.Min(0.85, best.Score),
            Album = d.Album,
            Label = d.Label,
            ReleaseDate = d.ReleaseDate,
            ArtworkUrl = d.ImageUrl,
            BpmHint = d.Bpm,
            KeyHint = d.CamelotKey,
        };
        if (!string.IsNullOrWhiteSpace(d.Genre)) c.Genres.Add(d.Genre);
        Runtime.CountMatch();
        return ProviderLookup.Match(c);
    }
}
