using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Discovery;

/// <summary>
/// Reads Beatport's public catalog pages. Beatport's official API needs partner credentials, but
/// every public page ships its data as JSON in a <c>__NEXT_DATA__</c> script tag (the approach
/// OneTagger and similar DJ tools use). Two track shapes occur:
/// <list type="bullet">
/// <item>search pages: flat fields (<c>track_id</c>, <c>track_name</c>, <c>key_name</c> "G Minor",
/// <c>artists[].artist_name</c>, <c>genre[].genre_name</c>);</item>
/// <item>catalog pages (charts, artist tracks): <c>id</c>, <c>name</c>, <c>key.camelot_number</c>
/// /<c>camelot_letter</c>, <c>artists[].name</c>, <c>genre.name</c>, <c>sample_url</c>.</item>
/// </list>
/// Pages are fetched with Windows' curl.exe (see <see cref="FetchHtmlAsync"/>). Read-only and throttled. If Beatport changes its page structure this returns nothing rather
/// than throwing, and the Discover panel says so.
/// </summary>
public sealed class BeatportCatalogClient
{
    private const string BaseUrl = "https://www.beatport.com";
    private static readonly Regex NextDataRegex = new(
        "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly ILogger<BeatportCatalogClient> _logger;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public BeatportCatalogClient(HttpClient http, ILogger<BeatportCatalogClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(20);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }

    /// <summary>Beatport track search. Ranked by Beatport's relevance score.</summary>
    public Task<List<(DiscoveryCandidate Candidate, BeatportTrackRefs Refs)>> SearchTracksAsync(string query, CancellationToken ct = default) =>
        FetchTracksAsync($"/search/tracks?q={Uri.EscapeDataString(query)}&per_page=25", ct);

    /// <summary>An artist's tracks, newest release first.</summary>
    public Task<List<(DiscoveryCandidate Candidate, BeatportTrackRefs Refs)>> ArtistTracksAsync(int artistId, CancellationToken ct = default) =>
        FetchTracksAsync($"/artist/-/{artistId}/tracks", ct);

    /// <summary>A genre's top 100, in chart order.</summary>
    public Task<List<(DiscoveryCandidate Candidate, BeatportTrackRefs Refs)>> GenreTop100Async(int genreId, string? genreSlug, CancellationToken ct = default) =>
        FetchTracksAsync($"/genre/{(string.IsNullOrWhiteSpace(genreSlug) ? "-" : genreSlug)}/{genreId}/top-100", ct);

    private async Task<List<(DiscoveryCandidate Candidate, BeatportTrackRefs Refs)>> FetchTracksAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (status, html) = await FetchHtmlAsync(BaseUrl + path, ct).ConfigureAwait(false);
            if (status != 200)
            {
                _logger.LogWarning("[Discover] Beatport {Path} returned {Status}", path, status);
                return new();
            }
            var tracks = ParseTracksWithRefs(html);
            if (tracks.Count == 0)
                _logger.LogInformation("[Discover] Beatport {Path}: no tracks found in page data", path);
            return tracks;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Discover] Beatport request failed: {Path}", path);
            return new();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Beatport sits behind Cloudflare's bot check, which rejects .NET's HttpClient (403 "Just a
    /// moment…", whatever the headers — verified 2026-09-28) but lets Windows' built-in curl.exe
    /// through with a browser user agent. So pages are fetched with curl.exe (in System32 since
    /// Windows 10 1803), falling back to HttpClient where it's missing.
    /// </summary>
    private async Task<(int Status, string Html)> FetchHtmlAsync(string url, CancellationToken ct)
    {
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (!File.Exists(curl))
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        var psi = new ProcessStartInfo(curl)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-s", "-L", "--compressed", "--max-time", "20", "-A", BrowserUserAgent,
                                    "-H", "Accept-Language: en-US,en;q=0.9", "-w", "\n" + StatusMarker + "%{http_code}", url })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("curl.exe did not start");
        using var reg = ct.Register(() => { try { process.Kill(); } catch { /* already exited */ } });
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var marker = output.LastIndexOf(StatusMarker, StringComparison.Ordinal);
        if (marker < 0) return (0, string.Empty);
        int.TryParse(output.AsSpan(marker + StatusMarker.Length).Trim(), out var status);
        return (status, output[..marker]);
    }

    private const string StatusMarker = "@@ORBIT_HTTP_STATUS@@";

    /// <summary>
    /// Extracts every track list from a Beatport page's <c>__NEXT_DATA__</c>. Public for tests.
    /// Each track also carries its Beatport artist ids and genre id/slug in
    /// <see cref="BeatportTrackRefs"/> so callers can follow up with artist or chart pages.
    /// </summary>
    public static List<DiscoveryCandidate> ParseTracks(string html) => ParseTracksWithRefs(html).Select(t => t.Candidate).ToList();

    public static List<(DiscoveryCandidate Candidate, BeatportTrackRefs Refs)> ParseTracksWithRefs(string html)
    {
        var results = new List<(DiscoveryCandidate Candidate, BeatportTrackRefs Refs)>();
        var match = NextDataRegex.Match(html ?? string.Empty);
        if (!match.Success) return results;

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            if (!doc.RootElement.TryGetProperty("props", out var props)
                || !props.TryGetProperty("pageProps", out var pageProps)
                || !pageProps.TryGetProperty("dehydratedState", out var state)
                || !state.TryGetProperty("queries", out var queries)
                || queries.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var query in queries.EnumerateArray())
            {
                if (!query.TryGetProperty("state", out var qs) || !qs.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var listName in new[] { "results", "data" })
                {
                    if (!data.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("bpm", out _)) continue;
                        var parsed = item.TryGetProperty("track_id", out _) ? ParseFlat(item) : ParseCatalog(item);
                        if (parsed is { } p && !string.IsNullOrWhiteSpace(p.Candidate.Title) && !string.IsNullOrWhiteSpace(p.Candidate.Artist))
                            results.Add(p);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Page structure changed — treated as "no results".
        }
        return results;
    }

    private static (DiscoveryCandidate Candidate, BeatportTrackRefs Refs)? ParseCatalog(JsonElement t)
    {
        var refs = new BeatportTrackRefs();
        var c = new DiscoveryCandidate
        {
            Title = Str(t, "name") ?? string.Empty,
            MixName = Str(t, "mix_name"),
            Bpm = Num(t, "bpm"),
            PreviewUrl = Str(t, "sample_url"),
            ReleaseDate = Date(Str(t, "new_release_date") ?? Str(t, "publish_date")),
        };
        if (t.TryGetProperty("length_ms", out var len) && len.ValueKind == JsonValueKind.Number)
            c.DurationSeconds = len.GetDouble() / 1000.0;

        var artists = new List<string>();
        if (t.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                if (Str(a, "name") is not { } name) continue;
                artists.Add(name);
                if (Num(a, "id") is { } id) refs.Artists.Add(((int)id, name));
            }
        c.Artist = string.Join(", ", artists);

        if (t.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object
            && Num(key, "camelot_number") is { } n && Str(key, "camelot_letter") is { } letter)
            c.CamelotKey = $"{(int)n}{letter.ToUpperInvariant()}";

        if (t.TryGetProperty("genre", out var genre) && genre.ValueKind == JsonValueKind.Object)
        {
            c.Genre = Str(genre, "name");
            refs.GenreId = Num(genre, "id") is { } gid ? (int)gid : null;
            refs.GenreSlug = Str(genre, "slug");
        }

        if (t.TryGetProperty("release", out var release) && release.ValueKind == JsonValueKind.Object)
        {
            c.Album = Str(release, "name");
            if (release.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
                c.ImageUrl = SizedImage(Str(img, "dynamic_uri")) ?? Str(img, "uri");
            if (release.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Object)
                c.Label = Str(label, "name");
        }

        if (t.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Object)
            c.Price = Str(price, "display");

        if (Num(t, "id") is { } trackId)
        {
            refs.TrackId = (int)trackId;
            c.BeatportUrl = $"{BaseUrl}/track/{Str(t, "slug") ?? "-"}/{(int)trackId}";
        }
        return (c, refs);
    }

    private static (DiscoveryCandidate Candidate, BeatportTrackRefs Refs)? ParseFlat(JsonElement t)
    {
        var refs = new BeatportTrackRefs();
        var mix = Str(t, "mix_name");
        var name = Str(t, "track_name") ?? string.Empty;
        // Search results put the mix in parentheses inside track_name ("DIRTY (Metrik Remix)").
        if (!string.IsNullOrWhiteSpace(mix) && name.EndsWith($"({mix})", StringComparison.OrdinalIgnoreCase))
            name = name[..^(mix.Length + 2)].TrimEnd();

        var c = new DiscoveryCandidate
        {
            Title = name,
            MixName = mix,
            Bpm = Num(t, "bpm"),
            CamelotKey = KeyNameToCamelot(Str(t, "key_name")),
            ReleaseDate = Date(Str(t, "release_date") ?? Str(t, "publish_date")),
            ImageUrl = SizedImage(Str(t, "track_image_dynamic_uri")),
        };
        if (Num(t, "length") is { } lenMs) c.DurationSeconds = lenMs / 1000.0;

        // Only "Artist" credits name the track; "Remixer" credits are already in the title.
        var artists = new List<string>();
        if (t.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                var type = Str(a, "artist_type_name");
                if (Str(a, "artist_name") is not { } an) continue;
                if (Num(a, "artist_id") is { } id) refs.Artists.Add(((int)id, an));
                if (type is null || type.Equals("Artist", StringComparison.OrdinalIgnoreCase))
                    artists.Add(an);
            }
        c.Artist = string.Join(", ", artists);

        if (t.TryGetProperty("genre", out var genres) && genres.ValueKind == JsonValueKind.Array)
            foreach (var g in genres.EnumerateArray())
            {
                c.Genre = Str(g, "genre_name");
                refs.GenreId = Num(g, "genre_id") is { } gid ? (int)gid : null;
                break;
            }

        if (t.TryGetProperty("release", out var release) && release.ValueKind == JsonValueKind.Object)
        {
            c.Album = Str(release, "release_name");
            c.ImageUrl ??= Str(release, "release_image_uri");
        }
        if (t.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Object)
            c.Label = Str(label, "label_name");
        if (t.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Object)
            c.Price = Str(price, "display");

        if (Num(t, "track_id") is { } trackId)
        {
            refs.TrackId = (int)trackId;
            c.BeatportUrl = $"{BaseUrl}/track/-/{(int)trackId}";
        }
        return (c, refs);
    }

    /// <summary>"G Minor" / "Ab Major" / "F♯ Minor" → Camelot. Null when unrecognised.</summary>
    public static string? KeyNameToCamelot(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return null;
        var parts = keyName.Replace('♯', '#').Replace('♭', 'b').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var standard = parts[0] + (parts[1].StartsWith("min", StringComparison.OrdinalIgnoreCase) ? "m" : string.Empty);
        var camelot = KeyConverter.ToCamelot(standard);
        return Regex.IsMatch(camelot, "^(1[0-2]|[1-9])[AB]$") ? camelot : null;
    }

    private static string? SizedImage(string? dynamicUri) =>
        string.IsNullOrWhiteSpace(dynamicUri) ? null : dynamicUri.Replace("{w}", "200").Replace("{h}", "200");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTime? Date(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}

/// <summary>Beatport ids a parsed track points at, for follow-up artist/chart requests.</summary>
public sealed class BeatportTrackRefs
{
    public int? TrackId { get; set; }
    /// <summary>Every credited artist (including remixers) with their Beatport id.</summary>
    public List<(int Id, string Name)> Artists { get; } = new();
    public int? GenreId { get; set; }
    public string? GenreSlug { get; set; }
}
