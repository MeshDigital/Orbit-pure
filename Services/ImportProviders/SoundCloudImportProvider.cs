using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// #425: Best-effort public SoundCloud playlist/set URL importer (<c>soundcloud.com/&lt;user&gt;/sets/&lt;slug&gt;</c>).
/// Reads SoundCloud's embedded <c>window.__sc_hydration</c> JSON without requiring a closed API key,
/// and falls back cleanly with an actionable message if a playlist is private or blocked.
/// </summary>
public sealed class SoundCloudImportProvider : IStreamingImportProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SoundCloudImportProvider> _logger;

    public string Name => "SoundCloud";
    public string IconGlyph => "music";

    public SoundCloudImportProvider(IHttpClientFactory httpClientFactory, ILogger<SoundCloudImportProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool CanHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) return false;

        var host = uri.Host;
        var isSc = host.Equals("soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("www.soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("m.soundcloud.com", StringComparison.OrdinalIgnoreCase);

        if (!isSc) return false;
        return uri.AbsolutePath.Contains("/sets/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ImportResult> ImportAsync(string input)
    {
        var tracks = new List<SearchQuery>();
        var title = string.Empty;
        try
        {
            await foreach (var batch in ImportStreamAsync(input))
            {
                if (string.IsNullOrEmpty(title)) title = batch.SourceTitle;
                tracks.AddRange(batch.Tracks);
            }

            if (tracks.Count == 0)
                return new ImportResult { Success = false, ErrorMessage = "No tracks found in this SoundCloud playlist." };

            return new ImportResult
            {
                Success = true,
                SourceTitle = title,
                SourceType = Name,
                SourceUrl = input.Trim(),
                Tracks = tracks,
            };
        }
        catch (ImportSourceException ex)
        {
            return new ImportResult { Success = false, ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SoundCloud import failed for {Url}", input);
            return new ImportResult { Success = false, ErrorMessage = $"SoundCloud import failed: {ex.Message}" };
        }
    }

    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        var url = input.Trim();
        if (!CanHandle(url))
            throw new ImportSourceException("Expected a public SoundCloud playlist URL (soundcloud.com/<user>/sets/<playlist>).");

        var client = _httpClientFactory.CreateClient("SoundCloudImport");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; ORBIT/1.0)");

        using var resp = await client.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ImportSourceException(
                $"SoundCloud returned HTTP {(int)resp.StatusCode}. Make sure the playlist is public, or export it via TuneMyMusic and drop the CSV/JSON file into ORBIT.");

        var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var json = ExtractHydrationJson(html);
        if (string.IsNullOrWhiteSpace(json))
            throw new ImportSourceException(
                "Could not read playlist metadata from SoundCloud (it may be private or rate-limited). You can export it via TuneMyMusic and drop the file here instead.");

        yield return ParseHydrationPlaylist(json, url);
    }

    internal static string? ExtractHydrationJson(string html)
    {
        const string marker = "window.__sc_hydration = ";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;

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
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                    return html[start..(i + 1)];
            }
        }
        return null;
    }

    internal static ImportBatchResult ParseHydrationPlaylist(string hydrationJson, string sourceUrl)
    {
        using var doc = JsonDocument.Parse(hydrationJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ImportSourceException("Unexpected SoundCloud hydration payload.");

        string playlistTitle = "SoundCloud Playlist";
        var tracks = new List<SearchQuery>();

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("hydratable", out var h) ||
                !string.Equals(h.GetString(), "playlist", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                continue;

            if (data.TryGetProperty("title", out var tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
                playlistTitle = tEl.GetString()!;

            if (data.TryGetProperty("tracks", out var tracksEl) && tracksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tr in tracksEl.EnumerateArray())
                {
                    if (tr.ValueKind != JsonValueKind.Object) continue;
                    var rawTitle = tr.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                    if (string.IsNullOrWhiteSpace(rawTitle)) continue;

                    string? uploader = null;
                    if (tr.TryGetProperty("user", out var u) && u.TryGetProperty("username", out var un))
                        uploader = un.GetString();

                    int? durationSec = null;
                    if (tr.TryGetProperty("duration", out var d) && d.TryGetInt64(out var ms) && ms > 0)
                        durationSec = (int)(ms / 1000);

                    var (artist, title) = YouTubeImportProvider.NormalizeVideoTitle(rawTitle, uploader);
                    tracks.Add(new SearchQuery
                    {
                        Artist = artist,
                        Title = title,
                        Album = playlistTitle,
                        Length = durationSec,
                    });
                }
            }
        }

        if (tracks.Count == 0)
            throw new ImportSourceException("No tracks found in this SoundCloud playlist (it may be private or empty).");

        return new ImportBatchResult
        {
            Tracks = tracks,
            SourceTitle = playlistTitle,
            SourceType = "SoundCloud",
            SourceUrl = sourceUrl,
            TotalEstimated = tracks.Count,
        };
    }
}
