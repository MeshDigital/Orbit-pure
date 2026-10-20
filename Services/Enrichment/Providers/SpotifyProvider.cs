using System;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services.Enrichment.Providers;

/// <summary>
/// Spotify search, only while the user is connected: identity, ISRC, album and cover. No audio
/// features (Spotify blocked those for new apps in Nov 2024). Goes through
/// <see cref="SpotifyEnrichmentService.IdentifyTrackAsync"/>, which keeps its own 429/403
/// back-off; its "degraded" answers are mapped to rate limits here so the breaker pauses too.
/// The search hit is re-scored with <see cref="EnrichmentMatch"/> because Spotify always returns
/// its first result.
/// </summary>
public sealed class SpotifyProvider : IMetadataProvider
{
    public const string ProviderName = "spotify";
    public static readonly RatePolicy Policy = new(20, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100), 1);

    private readonly Func<string, string, Task<TrackEnrichmentResult>> _identify;
    private readonly Func<bool> _isConnected;

    public SpotifyProvider(SpotifyEnrichmentService spotify, SpotifyAuthService auth)
        : this((a, t) => spotify.IdentifyTrackAsync(a, t), () => auth.IsAuthenticated)
    {
    }

    internal SpotifyProvider(Func<string, string, Task<TrackEnrichmentResult>> identify, Func<bool> isConnected, ProviderRuntime? runtime = null)
    {
        _identify = identify;
        _isConnected = isConnected;
        Runtime = runtime ?? new ProviderRuntime(ProviderName, Policy);
    }

    public string Name => ProviderName;
    public string DisplayName => "Spotify";
    public string Description => "Identity, ISRC, album and cover from Spotify search while you are connected. No audio features.";
    public MetadataField Supplies => MetadataField.Artwork | MetadataField.Album | MetadataField.Isrc | MetadataField.ExternalIds;
    public ProviderRuntime Runtime { get; }

    public ProviderAvailability GetAvailability()
    {
        if (Runtime.Breaker.IsDisabledForSession) return ProviderAvailability.Blocked;
        return _isConnected() ? ProviderAvailability.Ready : ProviderAvailability.NotConnected;
    }

    public async Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        if (!_isConnected()) return ProviderLookup.Unavailable("connect Spotify in Settings");
        if (track.HasWeakIdentity || string.IsNullOrWhiteSpace(track.Title)) return ProviderLookup.NoMatch();
        if (Runtime.Breaker.OpenUntil is { } until) return ProviderLookup.Limited(until - DateTime.UtcNow, "paused after a rate limit");

        await Runtime.Limiter.WaitAsync(ct).ConfigureAwait(false);
        Runtime.CountRequest();
        var result = await _identify(track.Artist, track.Title).ConfigureAwait(false);

        if (!result.Success)
        {
            var error = result.Error ?? "no match";
            if (error.Contains("Rate Limit", StringComparison.OrdinalIgnoreCase) || error.Contains("Degraded", StringComparison.OrdinalIgnoreCase))
            {
                var resume = Runtime.Breaker.RecordRateLimited(error.Contains("403") ? TimeSpan.FromMinutes(30) : null);
                Runtime.LastError = error;
                return ProviderLookup.Limited(resume - DateTime.UtcNow, error);
            }
            if (error.Contains("not available", StringComparison.OrdinalIgnoreCase))
                return ProviderLookup.Unavailable(error);
            if (error.Contains("No match", StringComparison.OrdinalIgnoreCase))
            {
                Runtime.Breaker.RecordSuccess();
                return ProviderLookup.NoMatch();
            }
            Runtime.Breaker.RecordFailure();
            Runtime.LastError = error;
            return ProviderLookup.Fail(error);
        }

        Runtime.Breaker.RecordSuccess();
        var score = EnrichmentMatch.Score(track.Artist, track.Title, null, result.OfficialArtist, result.OfficialTitle, null);
        if (!EnrichmentMatch.Accept(score)) return ProviderLookup.NoMatch();

        var c = new MetadataCandidate
        {
            Confidence = Math.Min(0.9, score),
            Isrc = result.ISRC,
            SpotifyTrackId = string.IsNullOrWhiteSpace(result.SpotifyId) ? null : result.SpotifyId,
            ArtworkUrl = string.IsNullOrWhiteSpace(result.AlbumArtUrl) ? null : result.AlbumArtUrl,
            ReleaseDate = result.ReleaseDate,
            Label = result.Label,
        };
        if (result.Genres != null) c.Genres.AddRange(result.Genres);
        Runtime.CountMatch();
        return ProviderLookup.Match(c);
    }
}
