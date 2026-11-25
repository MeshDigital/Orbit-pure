using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// #424: Imports YouTube and YouTube Music playlists (and Liked Videos when OAuth is configured)
/// via <see cref="IStreamingImportProvider"/> so they flow through ORBIT's preview, fuzzy library
/// matching, commit, and re-sync pipeline.
/// </summary>
public sealed partial class YouTubeImportProvider : IStreamingImportProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppConfig _config;
    private readonly ILogger<YouTubeImportProvider> _logger;
    private readonly Func<string?>? _accessTokenProvider;

    public string Name => "YouTube";
    public string IconGlyph => "play";

    public YouTubeImportProvider(
        IHttpClientFactory httpClientFactory,
        AppConfig config,
        ILogger<YouTubeImportProvider> logger,
        Func<string?>? accessTokenProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _accessTokenProvider = accessTokenProvider;
    }

    [GeneratedRegex(@"(?:^|[\?&])list=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PlaylistIdQueryRegex();

    [GeneratedRegex(@"\s*[\(\[]\s*(official\s*(music\s*)?(video|audio|lyric\s*video|visualizer)|lyric\s*video|lyrics?|audio|visualizer|hd|4k|hq|mv|m/v|explicit|clean|remastered(\s+\d{4})?)\s*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NoiseSuffixRegex();

    [GeneratedRegex(@"\s*-\s*Topic$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TopicChannelRegex();

    [GeneratedRegex(@"\s*(VEVO|Official)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ChannelNoiseRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    public bool CanHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        if (trimmed.Equals("youtube:liked", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        var isYouTubeHost =
            host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);

        if (!isYouTubeHost)
            return false;

        return !string.IsNullOrEmpty(ExtractPlaylistId(trimmed));
    }

    public static string? ExtractPlaylistId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        if (trimmed.Equals("youtube:liked", StringComparison.OrdinalIgnoreCase))
            return "LL";

        var match = PlaylistIdQueryRegex().Match(trimmed);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Cleans a YouTube video title into (Artist, Title) suitable for Soulseek search and library matching.
    /// Strips "(Official Video)", "[Official Audio]", etc., and falls back to the channel name when no
    /// "Artist - Title" separator is present.
    /// </summary>
    public static (string Artist, string Title) NormalizeVideoTitle(string rawTitle, string? channelName)
    {
        var cleaned = NoiseSuffixRegex().Replace(rawTitle ?? string.Empty, string.Empty).Trim();
        cleaned = MultiSpaceRegex().Replace(cleaned, " ");

        // Try common "Artist - Title" separators (hyphen, en-dash, em-dash, pipe)
        string[] separators = { " - ", " – ", " — ", " | " };
        foreach (var sep in separators)
        {
            var idx = cleaned.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0 && idx + sep.Length < cleaned.Length)
            {
                var artist = cleaned[..idx].Trim();
                var title = cleaned[(idx + sep.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                    return (artist, title);
            }
        }

        // Fallback: use channel name (stripping " - Topic", "VEVO", "Official") as Artist
        var fallbackArtist = TopicChannelRegex().Replace(channelName ?? string.Empty, string.Empty).Trim();
        fallbackArtist = ChannelNoiseRegex().Replace(fallbackArtist, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fallbackArtist))
            fallbackArtist = "Unknown Artist";

        return (fallbackArtist, string.IsNullOrWhiteSpace(cleaned) ? "Unknown Track" : cleaned);
    }

    public async Task<ImportResult> ImportAsync(string input)
    {
        var tracks = new List<SearchQuery>();
        var title = string.Empty;
        string? sourceUrl = null;
        try
        {
            await foreach (var batch in ImportStreamAsync(input))
            {
                if (string.IsNullOrEmpty(title) && !string.IsNullOrWhiteSpace(batch.SourceTitle))
                    title = batch.SourceTitle;
                sourceUrl ??= batch.SourceUrl;
                tracks.AddRange(batch.Tracks);
            }

            if (tracks.Count == 0)
                return new ImportResult { Success = false, ErrorMessage = "No playable videos found in this YouTube playlist." };

            return new ImportResult
            {
                Success = true,
                SourceTitle = string.IsNullOrWhiteSpace(title) ? "YouTube Playlist" : title,
                SourceType = Name,
                SourceUrl = sourceUrl ?? input.Trim(),
                Tracks = tracks,
            };
        }
        catch (ImportSourceException ex)
        {
            return new ImportResult { Success = false, ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "YouTube import failed for {Input}", input);
            return new ImportResult { Success = false, ErrorMessage = $"YouTube import failed: {ex.Message}" };
        }
    }

    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        var playlistId = ExtractPlaylistId(input);
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ImportSourceException("That doesn't look like a YouTube playlist URL (expected ?list=...).");

        var canonicalUrl = playlistId.Equals("LL", StringComparison.OrdinalIgnoreCase)
            ? "youtube:liked"
            : $"https://www.youtube.com/playlist?list={playlistId}";

        var accessToken = _accessTokenProvider?.Invoke();
        var apiKey = _config.YouTubeApiKey;

        if (playlistId.Equals("LL", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ImportSourceException(
                "Importing your private YouTube Liked Videos requires YouTube sign-in (OAuth access token).",
                requiresSignIn: true);
        }

        if (!string.IsNullOrWhiteSpace(accessToken) || !string.IsNullOrWhiteSpace(apiKey))
        {
            await foreach (var batch in StreamViaDataApiAsync(playlistId, canonicalUrl, apiKey, accessToken))
                yield return batch;
            yield break;
        }

        // Zero-config public playlist path via ytInitialData
        var publicBatch = await FetchPublicPlaylistHtmlAsync(playlistId, canonicalUrl).ConfigureAwait(false);
        yield return publicBatch;
    }

    private async IAsyncEnumerable<ImportBatchResult> StreamViaDataApiAsync(
        string playlistId,
        string canonicalUrl,
        string? apiKey,
        string? accessToken)
    {
        var client = _httpClientFactory.CreateClient("YouTubeImport");
        var playlistTitle = playlistId.Equals("LL", StringComparison.OrdinalIgnoreCase)
            ? "YouTube Liked Videos"
            : await TryFetchApiPlaylistTitleAsync(client, playlistId, apiKey, accessToken).ConfigureAwait(false) ?? $"YouTube Playlist ({playlistId})";

        string? pageToken = null;
        var yieldedAny = false;

        do
        {
            var url = playlistId.Equals("LL", StringComparison.OrdinalIgnoreCase)
                ? $"https://www.googleapis.com/youtube/v3/videos?part=snippet&myRating=like&maxResults=50"
                : $"https://www.googleapis.com/youtube/v3/playlistItems?part=snippet&maxResults=50&playlistId={Uri.EscapeDataString(playlistId)}";

            if (!string.IsNullOrWhiteSpace(pageToken))
                url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
            if (!string.IsNullOrWhiteSpace(apiKey))
                url += $"&key={Uri.EscapeDataString(apiKey)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(accessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                if (status is 401 or 403)
                    throw new ImportSourceException("YouTube rejected the request (check your playlist visibility or YouTube API credentials).", requiresSignIn: status == 401);
                throw new ImportSourceException($"YouTube Data API returned HTTP {status}.");
            }

            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            var total = root.TryGetProperty("pageInfo", out var pi) && pi.TryGetProperty("totalResults", out var tr)
                ? tr.GetInt32()
                : 0;

            var batchTracks = new List<SearchQuery>();
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("snippet", out var snippet)) continue;
                    var rawTitle = snippet.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(rawTitle) ||
                        rawTitle.Equals("Deleted video", StringComparison.OrdinalIgnoreCase) ||
                        rawTitle.Equals("Private video", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var channel = snippet.TryGetProperty("videoOwnerChannelTitle", out var ownerEl)
                        ? ownerEl.GetString()
                        : (snippet.TryGetProperty("channelTitle", out var chEl) ? chEl.GetString() : null);

                    var (artist, title) = NormalizeVideoTitle(rawTitle, channel);
                    batchTracks.Add(new SearchQuery
                    {
                        Artist = artist,
                        Title = title,
                        Album = playlistTitle,
                    });
                }
            }

            if (batchTracks.Count > 0)
            {
                yieldedAny = true;
                yield return new ImportBatchResult
                {
                    Tracks = batchTracks,
                    SourceTitle = playlistTitle,
                    SourceType = Name,
                    SourceUrl = canonicalUrl,
                    TotalEstimated = total > 0 ? total : batchTracks.Count,
                };
            }

            pageToken = root.TryGetProperty("nextPageToken", out var nextEl) ? nextEl.GetString() : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        if (!yieldedAny)
            throw new ImportSourceException("No playable videos found in this YouTube playlist.");
    }

    private static async Task<string?> TryFetchApiPlaylistTitleAsync(
        HttpClient client,
        string playlistId,
        string? apiKey,
        string? accessToken)
    {
        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/playlists?part=snippet&id={Uri.EscapeDataString(playlistId)}";
            if (!string.IsNullOrWhiteSpace(apiKey))
                url += $"&key={Uri.EscapeDataString(apiKey)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(accessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var first = items[0];
                if (first.TryGetProperty("snippet", out var snippet) && snippet.TryGetProperty("title", out var t))
                    return t.GetString();
            }
        }
        catch
        {
            // Non-fatal: fall back to playlist ID title
        }
        return null;
    }

    private async Task<ImportBatchResult> FetchPublicPlaylistHtmlAsync(string playlistId, string canonicalUrl)
    {
        var client = _httpClientFactory.CreateClient("YouTubeImport");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(playlistId)}");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; ORBIT/1.0)");

        using var resp = await client.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ImportSourceException($"Could not load YouTube playlist (HTTP {(int)resp.StatusCode}). Make sure the playlist is Public or Unlisted.");

        var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var json = ExtractYtInitialDataJson(html);
        if (string.IsNullOrWhiteSpace(json))
            throw new ImportSourceException("Could not read playlist data from YouTube. Make sure the playlist is Public or Unlisted, or set a YouTube API key in config.");

        return ParseYtInitialData(json, playlistId, canonicalUrl);
    }

    internal static string? ExtractYtInitialDataJson(string html)
    {
        const string marker = "var ytInitialData = ";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            const string altMarker = "ytInitialData = ";
            start = html.IndexOf(altMarker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += altMarker.Length;
        }
        else
        {
            start += marker.Length;
        }

        // Balance braces to find the end of the JSON object
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return html[start..(i + 1)];
            }
        }
        return null;
    }

    internal static ImportBatchResult ParseYtInitialData(string json, string playlistId, string canonicalUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var playlistTitle = TryFindMetadataTitle(root) ?? $"YouTube Playlist ({playlistId})";
        var tracks = new List<SearchQuery>();
        CollectPlaylistVideos(root, playlistTitle, tracks);

        if (tracks.Count == 0)
            throw new ImportSourceException("No playable videos found in this YouTube playlist (it may be private or empty).");

        return new ImportBatchResult
        {
            Tracks = tracks,
            SourceTitle = playlistTitle,
            SourceType = "YouTube",
            SourceUrl = canonicalUrl,
            TotalEstimated = tracks.Count,
            Notice = tracks.Count >= 100
                ? "Public YouTube pages return the first 100 videos; configure a YouTube API key for longer playlists."
                : null,
        };
    }

    private static string? TryFindMetadataTitle(JsonElement root)
    {
        if (root.TryGetProperty("metadata", out var meta)
            && meta.TryGetProperty("playlistMetadataRenderer", out var pmr)
            && pmr.TryGetProperty("title", out var t))
        {
            return t.GetString();
        }
        return null;
    }

    private static void CollectPlaylistVideos(JsonElement node, string playlistTitle, List<SearchQuery> tracks)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("playlistVideoRenderer", out var renderer) && renderer.ValueKind == JsonValueKind.Object)
            {
                var rawTitle = ReadTextRuns(renderer, "title");
                if (!string.IsNullOrWhiteSpace(rawTitle)
                    && !rawTitle.Equals("[Deleted video]", StringComparison.OrdinalIgnoreCase)
                    && !rawTitle.Equals("[Private video]", StringComparison.OrdinalIgnoreCase))
                {
                    var channel = ReadTextRuns(renderer, "shortBylineText");
                    int? durationSeconds = null;
                    if (renderer.TryGetProperty("lengthSeconds", out var lenEl)
                        && int.TryParse(lenEl.GetString(), out var secs) && secs > 0)
                    {
                        durationSeconds = secs;
                    }

                    var (artist, title) = NormalizeVideoTitle(rawTitle, channel);
                    tracks.Add(new SearchQuery
                    {
                        Artist = artist,
                        Title = title,
                        Album = playlistTitle,
                        Length = durationSeconds,
                    });
                }
                return;
            }

            foreach (var prop in node.EnumerateObject())
                CollectPlaylistVideos(prop.Value, playlistTitle, tracks);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in node.EnumerateArray())
                CollectPlaylistVideos(elem, playlistTitle, tracks);
        }
    }

    private static string? ReadTextRuns(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var prop)) return null;
        if (prop.TryGetProperty("simpleText", out var simple)) return simple.GetString();
        if (prop.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(runs.EnumerateArray()
                .Where(r => r.TryGetProperty("text", out _))
                .Select(r => r.GetProperty("text").GetString()));
        }
        return null;
    }
}
