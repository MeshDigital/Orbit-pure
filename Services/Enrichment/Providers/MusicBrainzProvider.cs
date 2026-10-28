using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Enrichment.Providers;

/// <summary>
/// MusicBrainz (no key): ISRC lookup (<c>/isrc/{isrc}</c>) or a recording search scored by
/// <see cref="EnrichmentMatch"/>, then the chosen release (<c>/release/{id}?inc=labels+genres</c>,
/// cached per release) for label, catalogue number and genres. Artwork comes from the Cover Art
/// Archive when the release says it has a front cover.
/// <para>
/// Strictly one request per second (MusicBrainz blocks clients that go faster) and the
/// application User-Agent from <see cref="MusicBrainzService.UserAgent"/>. The same identity
/// rules as MusicBrainzService apply; this class only adds limiter/breaker/cache plumbing.
/// </para>
/// </summary>
public sealed class MusicBrainzProvider : IMetadataProvider
{
    public const string ProviderName = "musicbrainz";
    public const string ApiBase = "https://musicbrainz.org/ws/2";
    public static readonly RatePolicy Policy = new(1, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1050), TimeSpan.FromMilliseconds(50), 1);

    private readonly ProviderHttp _http;
    private readonly ILogger _logger;

    public MusicBrainzProvider(HttpClient http, IMetadataLookupCache cache, ILogger<MusicBrainzProvider> logger, ProviderRuntime? runtime = null)
    {
        _logger = logger;
        Runtime = runtime ?? new ProviderRuntime(ProviderName, Policy);
        _http = new ProviderHttp(http, Runtime, cache, logger);
    }

    public string Name => ProviderName;
    public string DisplayName => "MusicBrainz";
    public string Description => "Label, catalogue number, release year, MusicBrainz IDs and Cover Art Archive covers. No key; strictly 1 request per second.";
    public MetadataField Supplies =>
        MetadataField.Artwork | MetadataField.Album | MetadataField.Label | MetadataField.ReleaseDate | MetadataField.Isrc |
        MetadataField.Genre | MetadataField.CatalogNumber | MetadataField.ExternalIds | MetadataField.Duration;
    public ProviderRuntime Runtime { get; }
    public ProviderAvailability GetAvailability() =>
        Runtime.Breaker.IsDisabledForSession ? ProviderAvailability.Blocked : ProviderAvailability.Ready;

    public async Task<ProviderLookup> LookupAsync(EnrichmentTrack track, KnownIds ids, CancellationToken ct)
    {
        using var docs = new DocumentBag();
        JsonElement? recording = null;
        double confidence = 0;

        var isrc = MetadataMerger.NormalizeIsrc(ids.Isrc ?? track.Isrc);
        if (isrc != null)
        {
            var fetch = await GetAsync($"/isrc/{isrc}?inc=releases+artist-credits&fmt=json", "isrc:" + isrc, ct).ConfigureAwait(false);
            if (fetch.Kind is not (FetchKind.Ok or FetchKind.NotFound)) return fetch.ToLookupFailure();
            if (fetch.Kind == FetchKind.Ok && docs.Parse(fetch.Body) is { } root &&
                root.TryGetProperty("recordings", out var recs) && recs.ValueKind == JsonValueKind.Array && recs.GetArrayLength() > 0)
            {
                recording = recs[0];
                confidence = 0.97;
            }
        }

        if (recording is null)
        {
            if (string.IsNullOrWhiteSpace(track.Title) || track.HasWeakIdentity) return ProviderLookup.NoMatch();
            var query = $"recording:\"{Escape(track.Title)}\" AND artist:\"{Escape(track.Artist)}\"";
            var fetch = await GetAsync($"/recording?query={Uri.EscapeDataString(query)}&limit=5&fmt=json", "search:" + DeezerProvider.Key(track), ct).ConfigureAwait(false);
            if (fetch.Kind == FetchKind.NotFound) return ProviderLookup.NoMatch();
            if (fetch.Kind != FetchKind.Ok) return fetch.ToLookupFailure();
            if (docs.Parse(fetch.Body) is not { } root || !root.TryGetProperty("recordings", out var recs) || recs.ValueKind != JsonValueKind.Array)
                return ProviderLookup.NoMatch();

            double best = 0;
            foreach (var r in recs.EnumerateArray())
            {
                var seconds = DeezerProvider.Num(r, "length") is { } ms ? ms / 1000.0 : (double?)null;
                var score = EnrichmentMatch.Score(track.Artist, track.Title, track.DurationSeconds, ArtistCredit(r), DeezerProvider.Str(r, "title"), seconds);
                if (score > best) { best = score; recording = r; }
            }
            if (recording is null || !EnrichmentMatch.Accept(best)) return ProviderLookup.NoMatch();
            confidence = Math.Min(0.9, best);
        }

        var rec = recording.Value;
        var c = new MetadataCandidate
        {
            Confidence = confidence,
            MusicBrainzRecordingId = DeezerProvider.Str(rec, "id"),
            DurationSeconds = DeezerProvider.Num(rec, "length") is { } len ? len / 1000.0 : null,
        };
        if (isrc != null) c.Isrc = isrc;
        else if (rec.TryGetProperty("isrcs", out var isrcs) && isrcs.ValueKind == JsonValueKind.Array && isrcs.GetArrayLength() > 0)
            c.Isrc = isrcs[0].GetString();
        if (track.HasWeakIdentity)
        {
            c.Artist = ArtistCredit(rec);
            c.Title = DeezerProvider.Str(rec, "title");
        }

        var release = PickRelease(rec);
        if (release is { } rel)
        {
            var releaseId = DeezerProvider.Str(rel, "id");
            c.MusicBrainzReleaseId = releaseId;
            c.Album = DeezerProvider.Str(rel, "title");
            c.ReleaseDate = DeezerProvider.Date(DeezerProvider.Str(rel, "date"));

            if (!string.IsNullOrWhiteSpace(releaseId))
            {
                var detail = await GetAsync($"/release/{releaseId}?inc=labels+genres&fmt=json", "release:" + releaseId, ct).ConfigureAwait(false);
                if (detail.Kind == FetchKind.Ok && docs.Parse(detail.Body) is { } d)
                {
                    if (d.TryGetProperty("label-info", out var li) && li.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var info in li.EnumerateArray())
                        {
                            c.Label ??= DeezerProvider.Str(info, "label", "name");
                            c.CatalogNumber ??= DeezerProvider.Str(info, "catalog-number");
                        }
                    }
                    if (d.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                        foreach (var g in genres.EnumerateArray().OrderByDescending(g => DeezerProvider.Num(g, "count") ?? 0).Take(3))
                            if (DeezerProvider.Str(g, "name") is { Length: > 0 } name) c.Genres.Add(name);
                    if (d.TryGetProperty("cover-art-archive", out var caa) && caa.TryGetProperty("front", out var front) && front.ValueKind == JsonValueKind.True)
                        c.ArtworkUrl = CoverArtUrl(releaseId);
                    c.ReleaseDate ??= DeezerProvider.Date(DeezerProvider.Str(d, "date"));
                }
                else if (detail.Kind is FetchKind.RateLimited or FetchKind.CircuitOpen)
                {
                    _logger.LogDebug("[Enrichment] MusicBrainz release {Id} deferred by the rate limit; using the recording data", releaseId);
                }
            }
        }

        Runtime.CountMatch();
        return ProviderLookup.Match(c);
    }

    /// <summary>Cover Art Archive front cover (500 px) for a release MBID.</summary>
    public static string CoverArtUrl(string releaseMbid) => $"https://coverartarchive.org/release/{releaseMbid}/front-500";

    private Task<HttpFetch> GetAsync(string path, string cacheKey, CancellationToken ct) =>
        _http.GetAsync(ApiBase + path, cacheKey, ct, req =>
        {
            req.Headers.UserAgent.ParseAdd(MusicBrainzService.UserAgent);
            req.Headers.Accept.ParseAdd("application/json");
        });

    /// <summary>Official releases first, then the earliest date.</summary>
    private static JsonElement? PickRelease(JsonElement recording)
    {
        if (!recording.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return null;
        var list = releases.EnumerateArray().ToList();
        if (list.Count == 0) return null;
        return list
            .OrderBy(r => string.Equals(DeezerProvider.Str(r, "status"), "Official", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => DeezerProvider.Str(r, "date") is { Length: > 0 } d ? d : "9999")
            .First();
    }

    private static string? ArtistCredit(JsonElement recording)
    {
        if (!recording.TryGetProperty("artist-credit", out var ac) || ac.ValueKind != JsonValueKind.Array) return null;
        var names = new List<string>();
        foreach (var credit in ac.EnumerateArray())
        {
            var name = DeezerProvider.Str(credit, "name") ?? DeezerProvider.Str(credit, "artist", "name");
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
