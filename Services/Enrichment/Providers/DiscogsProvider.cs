using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Enrichment.Providers;

/// <summary>
/// Discogs database search (needs a free personal access token from discogs.com/settings/developers).
/// One request per track: <c>/database/search?type=release&amp;artist=…&amp;track=…</c> returns
/// genre + style (the most precise source for electronic subgenres), label, catalogue number,
/// year and a cover. 60 requests/minute authenticated; this keeps to ≤ 55/min and, when
/// <c>X-Discogs-Ratelimit-Remaining</c> reaches 2, pauses until the minute window resets.
/// </summary>
public sealed class DiscogsProvider : IMetadataProvider
{
    public const string ProviderName = "discogs";
    public const string ApiBase = "https://api.discogs.com";
    public static readonly RatePolicy Policy = new(55, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(100), 1);

    private readonly ProviderHttp _http;
    private readonly MetadataCredentialStore _keys;

    public DiscogsProvider(HttpClient http, IMetadataLookupCache cache, MetadataCredentialStore keys, ILogger<DiscogsProvider> logger, ProviderRuntime? runtime = null)
    {
        _keys = keys;
        Runtime = runtime ?? new ProviderRuntime(ProviderName, Policy);
        _http = new ProviderHttp(http, Runtime, cache, logger);
    }

    public string Name => ProviderName;
    public string DisplayName => "Discogs";
    public string Description => "Genre and style (best for electronic subgenres), label, catalogue number and year. Needs a free personal token; 60 requests per minute.";
    public MetadataField Supplies =>
        MetadataField.Genre | MetadataField.Style | MetadataField.Label | MetadataField.CatalogNumber | MetadataField.ReleaseDate | MetadataField.Artwork;
    public ProviderRuntime Runtime { get; }

    public ProviderAvailability GetAvailability()
    {
        if (Runtime.Breaker.IsDisabledForSession) return ProviderAvailability.Blocked;
        return _keys.Has(MetadataCredentialStore.DiscogsToken) ? ProviderAvailability.Ready : ProviderAvailability.NeedsKey;
    }

    public async Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        var token = _keys.Get(MetadataCredentialStore.DiscogsToken);
        if (token == null) return ProviderLookup.Unavailable("add a Discogs token in Settings");
        if (track.HasWeakIdentity || string.IsNullOrWhiteSpace(track.Title)) return ProviderLookup.NoMatch();

        var artist = Discovery.PlaylistDiscoveryService.PrimaryArtist(track.Artist);
        var url = $"{ApiBase}/database/search?type=release&per_page=5&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(track.Title)}";
        var fetch = await _http.GetAsync(url, "search:" + DeezerProvider.Key(track), ct, req =>
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Discogs token=" + token);
            req.Headers.UserAgent.ParseAdd(MusicBrainzService.UserAgent);
        }, InspectBudget).ConfigureAwait(false);
        if (fetch.Kind == FetchKind.NotFound) return ProviderLookup.NoMatch();
        if (fetch.Kind != FetchKind.Ok) return fetch.ToLookupFailure();

        try
        {
            return Parse(fetch.Body, track) is { } c ? Matched(c) : ProviderLookup.NoMatch();
        }
        catch (JsonException ex)
        {
            Runtime.LastError = "malformed search response";
            return ProviderLookup.Fail("Discogs returned malformed JSON: " + ex.Message);
        }
    }

    private ProviderLookup Matched(MetadataCandidate c)
    {
        Runtime.CountMatch();
        return ProviderLookup.Match(c);
    }

    /// <summary>Picks the best search result. Public for tests.</summary>
    public static MetadataCandidate? Parse(string? body, EnrichmentTrack track)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;

        foreach (var r in results.EnumerateArray())
        {
            // Results are releases titled "Artist - Release"; the track filter already matched the
            // track title server-side, so only the artist is checked here.
            var full = DeezerProvider.Str(r, "title") ?? string.Empty;
            var dash = full.IndexOf(" - ", StringComparison.Ordinal);
            var releaseArtist = dash > 0 ? full[..dash] : full;
            var artistScore = EnrichmentMatch.Score(track.Artist, "x", null, releaseArtist, "x", null);
            if (!EnrichmentMatch.Accept(artistScore)) continue;

            var c = new MetadataCandidate { Confidence = 0.8 };
            if (dash > 0) c.Album = full[(dash + 3)..].Trim();
            AddAll(r, "style", c.Styles);
            AddAll(r, "genre", c.Genres);
            if (r.TryGetProperty("label", out var labels) && labels.ValueKind == JsonValueKind.Array && labels.GetArrayLength() > 0)
                c.Label = labels[0].GetString();
            c.CatalogNumber = DeezerProvider.Str(r, "catno") is { } cat && !cat.Equals("none", StringComparison.OrdinalIgnoreCase) ? cat : null;
            var year = DeezerProvider.Str(r, "year");
            if (int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y > 1900)
                c.ReleaseDate = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cover = DeezerProvider.Str(r, "cover_image");
            if (!string.IsNullOrWhiteSpace(cover) && !cover.Contains("spacer.gif", StringComparison.OrdinalIgnoreCase)) c.ArtworkUrl = cover;
            return c;
        }
        return null;
    }

    /// <summary>Pauses before the minute budget runs out instead of waiting for a 429.</summary>
    internal static HttpFetch? InspectBudget(HttpResponseMessage response, string _)
    {
        if (response.Headers.TryGetValues("X-Discogs-Ratelimit-Remaining", out var values) &&
            int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining) && remaining <= 2)
        {
            // The body is still valid; ProviderHttp keeps it and opens the breaker for the window.
            return new HttpFetch(FetchKind.Ok, PauseAfter: TimeSpan.FromSeconds(60));
        }
        return null;
    }

    private static void AddAll(JsonElement e, string name, System.Collections.Generic.List<string> into)
    {
        if (e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) into.Add(s);
    }
}
