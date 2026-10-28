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
/// Deezer's public API (no key). ISRC lookup first (<c>/track/isrc:{isrc}</c>), else a strict
/// <c>artist:"…" track:"…"</c> search scored by <see cref="EnrichmentMatch"/>; then
/// <c>/track/{id}</c> for the ISRC and <c>/album/{id}</c> (cached per album) for label, genres
/// and release date. Cover art is the album's <c>cover_xl</c>.
/// Deezer documents ~50 requests / 5 s; this stays at ≤ 40 / 5 s and one request every ~120 ms
/// (≈ 7.5/s) with jitter. Its quota error arrives as HTTP 200 + <c>error.code 4</c> and is
/// treated as a rate limit.
/// </summary>
public sealed class DeezerProvider : IMetadataProvider
{
    public const string ProviderName = "deezer";
    public const string ApiBase = "https://api.deezer.com";
    public static readonly RatePolicy Policy = new(40, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(110), TimeSpan.FromMilliseconds(30), 4);

    private readonly ProviderHttp _http;
    private readonly ILogger _logger;

    public DeezerProvider(HttpClient http, IMetadataLookupCache cache, ILogger<DeezerProvider> logger, ProviderRuntime? runtime = null)
    {
        _logger = logger;
        Runtime = runtime ?? new ProviderRuntime(ProviderName, Policy);
        _http = new ProviderHttp(http, Runtime, cache, logger);
    }

    public string Name => ProviderName;
    public string DisplayName => "Deezer";
    public string Description => "Cover art, album, release date, label, genre and ISRC. Public API, no key; about 7 requests per second.";
    public MetadataField Supplies =>
        MetadataField.Artwork | MetadataField.Album | MetadataField.Label | MetadataField.ReleaseDate | MetadataField.Isrc |
        MetadataField.Genre | MetadataField.TrackNumber | MetadataField.Duration | MetadataField.ExternalIds;
    public ProviderRuntime Runtime { get; }
    public ProviderAvailability GetAvailability() =>
        Runtime.Breaker.IsDisabledForSession ? ProviderAvailability.Blocked : ProviderAvailability.Ready;

    public async Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        JsonElement? trackJson = null;
        double confidence;
        using var docs = new DocumentBag();

        var isrc = MetadataMerger.NormalizeIsrc(ids.Isrc ?? track.Isrc);
        if (isrc != null)
        {
            var byIsrc = await GetAsync($"/track/isrc:{isrc}", $"isrc:{isrc}", ct).ConfigureAwait(false);
            if (byIsrc.Kind is not (FetchKind.Ok or FetchKind.NotFound)) return byIsrc.ToLookupFailure();
            if (byIsrc.Kind == FetchKind.Ok && docs.Parse(byIsrc.Body) is { } el && el.TryGetProperty("id", out _))
                trackJson = el;
        }

        if (trackJson is { } isrcHit)
        {
            // An ISRC is an exact recording id, but guard against a mistagged ISRC: unless the file has
            // no usable identity, the title must still roughly agree.
            if (track.HasWeakIdentity) confidence = 0.95;
            else
            {
                var score = EnrichmentMatch.Score(track.Artist, track.Title, track.DurationSeconds,
                    Str(isrcHit, "artist", "name"), Str(isrcHit, "title"), Num(isrcHit, "duration"));
                if (score >= 0.6) confidence = 0.98;
                else { trackJson = null; confidence = 0; }
            }
        }
        else confidence = 0;

        if (trackJson is null)
        {
            if (string.IsNullOrWhiteSpace(track.Title) || track.HasWeakIdentity) return ProviderLookup.NoMatch();
            var q = $"artist:\"{track.Artist}\" track:\"{track.Title}\"";
            var search = await GetAsync($"/search/track?q={Uri.EscapeDataString(q)}&limit=10", "search:" + Key(track), ct).ConfigureAwait(false);
            if (search.Kind == FetchKind.NotFound) return ProviderLookup.NoMatch();
            if (search.Kind != FetchKind.Ok) return search.ToLookupFailure();
            if (docs.Parse(search.Body) is not { } root || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return ProviderLookup.NoMatch();

            double best = 0;
            JsonElement? bestHit = null;
            foreach (var hit in data.EnumerateArray())
            {
                var title = Str(hit, "title") ?? Str(hit, "title_short");
                var score = EnrichmentMatch.Score(track.Artist, track.Title, track.DurationSeconds,
                    Str(hit, "artist", "name"), title, Num(hit, "duration"));
                if (score > best) { best = score; bestHit = hit; }
            }
            if (bestHit is null || !EnrichmentMatch.Accept(best)) return ProviderLookup.NoMatch();
            confidence = Math.Min(0.95, best);

            // Search hits carry no ISRC / release date: fetch the full track.
            var id = Long(bestHit.Value, "id");
            if (id > 0)
            {
                var full = await GetAsync($"/track/{id}", $"track:{id}", ct).ConfigureAwait(false);
                if (full.Kind == FetchKind.Ok && docs.Parse(full.Body) is { } f && f.TryGetProperty("id", out _)) trackJson = f;
                else if (full.Kind is FetchKind.RateLimited or FetchKind.CircuitOpen) return full.ToLookupFailure();
                else trackJson = bestHit;
            }
            else trackJson = bestHit;
        }

        var t = trackJson.Value;
        var c = new MetadataCandidate
        {
            Confidence = confidence,
            Isrc = Str(t, "isrc"),
            DeezerTrackId = Long(t, "id") is var tid and > 0 ? tid.ToString(CultureInfo.InvariantCulture) : null,
            DurationSeconds = Num(t, "duration"),
            TrackNumber = (int?)Num(t, "track_position"),
            ReleaseDate = Date(Str(t, "release_date")),
        };
        if (track.HasWeakIdentity)
        {
            c.Artist = Str(t, "artist", "name");
            c.Title = Str(t, "title");
        }

        long albumId = 0;
        if (t.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            c.Album = Str(album, "title");
            c.ArtworkUrl = Str(album, "cover_xl") ?? Str(album, "cover_big");
            albumId = Long(album, "id");
            c.ReleaseDate ??= Date(Str(album, "release_date"));
        }

        if (albumId > 0)
        {
            var albumFetch = await GetAsync($"/album/{albumId}", $"album:{albumId}", ct).ConfigureAwait(false);
            if (albumFetch.Kind == FetchKind.Ok && docs.Parse(albumFetch.Body) is { } a)
            {
                c.Label = Str(a, "label");
                c.ReleaseDate ??= Date(Str(a, "release_date"));
                c.ArtworkUrl ??= Str(a, "cover_xl");
                if (a.TryGetProperty("genres", out var genres) && genres.TryGetProperty("data", out var gd) && gd.ValueKind == JsonValueKind.Array)
                    foreach (var g in gd.EnumerateArray())
                        if (Str(g, "name") is { Length: > 0 } name) c.Genres.Add(name);
            }
            else if (albumFetch.Kind is not (FetchKind.Ok or FetchKind.NotFound))
            {
                _logger.LogDebug("[Enrichment] Deezer album {Id} unavailable ({Kind}); keeping the track data", albumId, albumFetch.Kind);
            }
        }

        if (!string.IsNullOrWhiteSpace(c.ArtworkUrl) && c.ArtworkUrl.Contains("/images/cover//", StringComparison.Ordinal))
            c.ArtworkUrl = null; // Deezer's "no cover" placeholder

        Runtime.CountMatch();
        return ProviderLookup.Match(c);
    }

    private Task<HttpFetch> GetAsync(string path, string cacheKey, CancellationToken ct) =>
        _http.GetAsync(ApiBase + path, cacheKey, ct, inspect: InspectDeezerError);

    /// <summary>Deezer answers errors as 200 + {"error":{code}}: 4 = quota, 800 = no data.</summary>
    internal static HttpFetch? InspectDeezerError(HttpResponseMessage _, string body)
    {
        if (body.Length == 0 || body.IndexOf("\"error\"", StringComparison.Ordinal) < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("error", out var err)) return null;
            var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            var message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
            return code switch
            {
                4 => new HttpFetch(FetchKind.RateLimited, RetryAfter: TimeSpan.FromSeconds(5), Error: "Deezer quota exceeded"),
                800 => new HttpFetch(FetchKind.NotFound, StatusCode: 404),
                _ => new HttpFetch(FetchKind.Failed, Error: $"Deezer error {code}: {message}"),
            };
        }
        catch (JsonException)
        {
            return new HttpFetch(FetchKind.Failed, Error: "Deezer returned malformed JSON");
        }
    }

    internal static string Key(EnrichmentTrack t) =>
        (Discovery.PlaylistDiscoveryService.NormalizeArtist(t.Artist) + "|" + Discovery.PlaylistDiscoveryService.NormalizeTitle(t.Title));

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static string? Str(JsonElement e, string obj, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(obj, out var o) ? Str(o, name) : null;

    internal static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    internal static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    internal static DateTime? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.StartsWith("0000", StringComparison.Ordinal)) return null;
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM", "yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        return null;
    }
}

/// <summary>Keeps parsed JSON documents alive for one lookup and disposes them together.</summary>
internal sealed class DocumentBag : IDisposable
{
    private readonly System.Collections.Generic.List<JsonDocument> _docs = new();

    public JsonElement? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var doc = JsonDocument.Parse(body);
            _docs.Add(doc);
            return doc.RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var d in _docs) d.Dispose();
    }
}
