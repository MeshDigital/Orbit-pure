using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using HtmlAgilityPack;
using SLSKDONET.Models;

namespace SLSKDONET.Services.InputParsers;

/// <summary>
/// Scrapes public Spotify playlists and albums without requiring API keys.
/// Uses multiple strategies: OEmbed (most reliable), HTML scraping, JSON-LD fallback.
/// Hardened against Spotify frontend changes with rate limiting.
/// </summary>
public class SpotifyScraperInputSource
{
    private readonly ILogger<SpotifyScraperInputSource> _logger;
    private readonly HttpClient _httpClient;

    public SpotifyScraperInputSource(ILogger<SpotifyScraperInputSource> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Extracts playlist ID from Spotify URL.
    /// </summary>
    public static string? ExtractPlaylistId(string url)
    {
        if (url.Contains("spotify.com") && url.Contains("/playlist/"))
        {
            var parts = url.Split('/');
            if (parts.Contains("playlist"))
            {
                var idx = Array.IndexOf(parts, "playlist");
                if (idx + 1 < parts.Length)
                {
                    var id = parts[idx + 1].Split('?')[0];
                    return string.IsNullOrEmpty(id) ? null : id;
                }
            }
        }

        if (url.StartsWith("spotify:playlist:"))
            return url.Replace("spotify:playlist:", "");

        return null;
    }

    /// <summary>
    /// Extracts album ID from Spotify URL.
    /// </summary>
    public static string? ExtractAlbumId(string url)
    {
        if (url.Contains("spotify.com") && url.Contains("/album/"))
        {
            var parts = url.Split('/');
            if (parts.Contains("album"))
            {
                var idx = Array.IndexOf(parts, "album");
                if (idx + 1 < parts.Length)
                {
                    var id = parts[idx + 1].Split('?')[0];
                    return string.IsNullOrEmpty(id) ? null : id;
                }
            }
        }

        if (url.StartsWith("spotify:album:"))
            return url.Replace("spotify:album:", "");

        return null;
    }

    /// <summary>
    /// Parses a Spotify URL using multiple extraction strategies.
    /// Tries OEmbed, then HTML scraping, then JSON-LD.
    /// </summary>
    public async Task<List<SearchQuery>> ParseAsync(string url)
    {
        try
        {
            _logger.LogInformation("Parsing Spotify content (public scraping): {Url}", url);

            // Normalize URL to avoid Spotify "si" tracking parameters that sometimes break scraping
            var canonicalUrl = NormalizeSpotifyUrl(url);
            if (!string.Equals(canonicalUrl, url, StringComparison.Ordinal))
                _logger.LogDebug("Using canonical Spotify URL: {CanonicalUrl}", canonicalUrl);

            // Strategy 1: Try OEmbed for metadata
            var oembedTitle = await TryOEmbedAsync(canonicalUrl);

            // Strategy 2: Try HTML scraping
            var tracks = await TryScrapeHtmlAsync(canonicalUrl);
            
            if (!tracks.Any())
            {
                _logger.LogWarning("Spotify scrape returned zero tracks for canonical URL: {Url}", canonicalUrl);
                throw new InvalidOperationException("Unable to extract tracks. The playlist may be private, deleted, or the URL may be invalid (or consent wall detected).");
            }

            // Enrich with OEmbed title if available
            if (!string.IsNullOrEmpty(oembedTitle) && tracks.Any())
            {
                foreach (var track in tracks)
                {
                    if (string.IsNullOrEmpty(track.SourceTitle))
                        track.SourceTitle = oembedTitle;
                }
            }

            _logger.LogInformation("Successfully extracted {Count} tracks from Spotify", tracks.Count);
            return tracks;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error accessing Spotify");
            throw new InvalidOperationException($"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Spotify URL");
            throw;
        }
    }

    /// <summary>
    /// Removes tracking query params and rebuilds a clean Spotify URL for more reliable scraping.
    /// </summary>
    private static string NormalizeSpotifyUrl(string url)
    {
        try
        {
            var uri = new Uri(url);

            // Keep only the path (playlist/album) without query fragments like ?si=...
            var cleanBuilder = new UriBuilder(uri.Scheme, uri.Host)
            {
                Path = uri.AbsolutePath,
                Query = string.Empty
            };

            return cleanBuilder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            // Fallback: strip common "?" tracking manually
            var qIndex = url.IndexOf('?', StringComparison.Ordinal);
            return qIndex >= 0 ? url[..qIndex] : url;
        }
    }

    /// <summary>
    /// Attempts to fetch metadata via Spotify's OEmbed endpoint.
    /// Returns the playlist/album title if successful.
    /// </summary>
    private async Task<string?> TryOEmbedAsync(string url)
    {
        try
        {
            _logger.LogDebug("Attempting OEmbed extraction");
            
            var oembedUrl = $"https://embed.spotify.com/oembed?url={Uri.EscapeDataString(url)}";
            var response = await _httpClient.GetAsync(oembedUrl);
            
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("title", out var titleElem))
            {
                var title = titleElem.GetString();
                _logger.LogDebug("OEmbed title: {Title}", title);
                return title;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OEmbed extraction failed");
        }

        return null;
    }

    /// <summary>
    /// Scrapes HTML with rate limiting and multiple fallback strategies.
    /// </summary>
    private async Task<List<SearchQuery>> TryScrapeHtmlAsync(string url)
    {
        try
        {
            // CRITICAL FIX: Convert to embed URL for reliable scraping
            // The embed version contains simpler JSON structures that are easier to parse
            string embedUrl = url;
            if (!url.Contains("/embed/"))
            {
                // Convert "https://open.spotify.com/playlist/XYZ" 
                // to "https://open.spotify.com/embed/playlist/XYZ"
                embedUrl = url.Replace("open.spotify.com/", "open.spotify.com/embed/");
                _logger.LogDebug("Converted to embed URL: {EmbedUrl}", embedUrl);
            }

            // Rate limiting to avoid IP bans
            await Task.Delay(Random.Shared.Next(500, 1500));

            var html = await _httpClient.GetStringAsync(embedUrl);

            if (string.IsNullOrWhiteSpace(html))
            {
                _logger.LogWarning("Spotify scrape: empty HTML for {Url}", embedUrl);
                return new List<SearchQuery>();
            }

            _logger.LogDebug("Spotify scrape: fetched HTML length {Length}", html.Length);

            // Strategy 1: Try embed-specific "resource" variable extraction (simplest and most reliable for embeds)
            var tracks = ExtractTracksFromEmbedResource(html, url);
            if (tracks.Any())
            {
                _logger.LogDebug("Successfully extracted tracks from embed resource variable");
                return tracks;
            }

            // Strategy 2: Try __NEXT_DATA__ extraction
            tracks = ExtractTracksFromHtml(html, url);
            if (tracks.Any())
                return tracks;

            _logger.LogDebug("Spotify scrape: __NEXT_DATA__ yielded 0 tracks");

            // Strategy 3: Try JSON-LD as fallback
            tracks = ExtractTracksFromJsonLd(html, url);
            if (tracks.Any())
                return tracks;

            _logger.LogDebug("Spotify scrape: JSON-LD yielded 0 tracks");

            return new List<SearchQuery>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HTML scraping failed, returning empty list");
            return new List<SearchQuery>();
        }
    }

    /// <summary>
    /// Extracts tracks from the Spotify embed's "resource" JavaScript variable.
    /// The embed version uses a simple: var resource = {...}; pattern
    /// </summary>
    private List<SearchQuery> ExtractTracksFromEmbedResource(string html, string sourceUrl)
    {
        var tracks = new List<SearchQuery>();
        try
        {
            // Look for the resource variable pattern in embed pages
            var resourcePattern = @"resource\s*=\s*(\{.*?\});";
            var match = System.Text.RegularExpressions.Regex.Match(html, resourcePattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            
            if (!match.Success)
            {
                _logger.LogDebug("Embed resource variable not found in HTML");
                return tracks;
            }

            var jsonContent = match.Groups[1].Value;
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            // Extract playlist title
            var playlistTitle = "Spotify Playlist";
            if (root.TryGetProperty("name", out var nameElem))
            {
                playlistTitle = nameElem.GetString() ?? playlistTitle;
            }

            // Extract tracks from the resource structure
            // Common patterns: resource.tracks.items or resource.trackList
            if (root.TryGetProperty("tracks", out var tracksObj))
            {
                if (tracksObj.TryGetProperty("items", out var itemsArray))
                {
                    ExtractTracksFromItemsArray(itemsArray, playlistTitle, tracks);
                }
            }
            else if (root.TryGetProperty("trackList", out var trackList))
            {
                ExtractTracksRecursive(trackList, tracks, playlistTitle);
            }
            else
            {
                // Fallback: recursively search for track objects
                ExtractTracksRecursive(root, tracks, playlistTitle);
            }

            if (tracks.Count > 0)
                _logger.LogDebug("Extracted {TrackCount} tracks from embed resource variable", tracks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embed resource extraction failed");
        }

        return tracks;
    }

    /// <summary>
    /// Extracts tracks from JSON-LD structured data (SEO data).
    /// </summary>
    private List<SearchQuery> ExtractTracksFromJsonLd(string html, string sourceUrl)
    {
        var tracks = new List<SearchQuery>();
        try
        {
            const string jsonLdPrefix = "\"@type\":\"MusicPlaylist\"";
            var jsonLdIndex = html.IndexOf(jsonLdPrefix, StringComparison.OrdinalIgnoreCase);
            
            if (jsonLdIndex == -1)
                return tracks;

            var braceIndex = html.LastIndexOf('{', jsonLdIndex);
            if (braceIndex == -1)
                return tracks;

            var closingBrace = FindMatchingBrace(html, braceIndex);
            if (closingBrace == -1)
                return tracks;

            var jsonContent = html.Substring(braceIndex, closingBrace - braceIndex + 1);
            
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("track", out var tracksElem))
            {
                var playlistTitle = root.TryGetProperty("name", out var nameElem) 
                    ? nameElem.GetString() ?? "Spotify Playlist"
                    : "Spotify Playlist";

                ExtractTracksRecursive(tracksElem, tracks, playlistTitle);
            }

            if (tracks.Count > 0)
                _logger.LogDebug("Extracted {TrackCount} tracks from JSON-LD", tracks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JSON-LD extraction failed, returning empty list");
        }

        return tracks;
    }

    /// <summary>
    /// Extracts tracks from __NEXT_DATA__ script content.
    /// </summary>
    private List<SearchQuery> ExtractTracksFromHtml(string html, string sourceUrl)
    {
        try
        {
            var jsonContent = GetNextDataJson(html);
            if (string.IsNullOrEmpty(jsonContent))
                return new List<SearchQuery>();

            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            var playlistTitle = ExtractPlaylistTitle(root) ?? "Spotify Playlist";
            var tracks = new List<SearchQuery>();

            // Prefer targeted extraction from entities/track lists; fall back to full recursion.
            if (!TryExtractTracksFromEntities(root, playlistTitle, tracks))
            {
                ExtractTracksRecursive(root, tracks, playlistTitle);
            }

            if (tracks.Count > 0)
                _logger.LogDebug("Extracted {TrackCount} tracks from __NEXT_DATA__", tracks.Count);
            return tracks;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HTML extraction from __NEXT_DATA__ failed, returning empty list");
            return new List<SearchQuery>();
        }
    }

    /// <summary>
    /// Retrieves the __NEXT_DATA__ JSON payload using HtmlAgilityPack first, then falls back to string search.
    /// </summary>
    private string? GetNextDataJson(string html)
    {
        try
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);
            var node = doc.DocumentNode.SelectSingleNode("//script[@id='__NEXT_DATA__']");
            if (node != null)
            {
                var inner = (node.InnerText ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(inner))
                {
                    _logger.LogDebug("Spotify scrape: found __NEXT_DATA__ via HtmlAgilityPack (length {Length})", inner.Length);
                    return inner;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HtmlAgilityPack extraction of __NEXT_DATA__ failed; will try fallback");
        }

        // Fallback: manual string search
        var scriptStart = html.IndexOf("id=\"__NEXT_DATA__\"", StringComparison.OrdinalIgnoreCase);
        if (scriptStart == -1)
        {
            _logger.LogDebug("Spotify scrape: __NEXT_DATA__ not found via fallback search");
            return null;
        }

        var contentStart = html.IndexOf(">", scriptStart);
        var contentEnd = html.IndexOf("</script>", contentStart);
        if (contentStart == -1 || contentEnd == -1)
        {
            _logger.LogDebug("Spotify scrape: __NEXT_DATA__ boundaries not found (start {Start}, end {End})", contentStart, contentEnd);
            return null;
        }

        var jsonContent = html.Substring(contentStart + 1, contentEnd - contentStart - 1).Trim();
        _logger.LogDebug("Spotify scrape: __NEXT_DATA__ fallback length {Length}", jsonContent.Length);
        return string.IsNullOrEmpty(jsonContent) ? null : jsonContent;
    }

    /// <summary>
    /// Attempts to navigate common __NEXT_DATA__ shapes to pull track items without deep recursion.
    /// </summary>
    private bool TryExtractTracksFromEntities(JsonElement root, string sourceTitle, List<SearchQuery> tracks)
    {
        try
        {
            if (root.TryGetProperty("props", out var props) &&
                props.TryGetProperty("pageProps", out var pageProps) &&
                pageProps.TryGetProperty("initialState", out var initialState) &&
                initialState.TryGetProperty("entities", out var entities))
            {
                foreach (var entityProp in entities.EnumerateObject())
                {
                    var entityVal = entityProp.Value;

                    // Common shape: { items: [ { track: {...} } ] }
                    if (entityVal.ValueKind == JsonValueKind.Object)
                    {
                        if (entityVal.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        {
                            ExtractTracksFromItemsArray(items, sourceTitle, tracks);
                        }
                        else if (entityVal.ValueKind == JsonValueKind.Array)
                        {
                            ExtractTracksFromItemsArray(entityVal, sourceTitle, tracks);
                        }
                    }
                    else if (entityVal.ValueKind == JsonValueKind.Array)
                    {
                        ExtractTracksFromItemsArray(entityVal, sourceTitle, tracks);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Targeted entity extraction failed; will fall back to recursion");
        }

        return tracks.Count > 0;
    }

    /// <summary>
    /// Extracts track information from a known items array shape.
    /// </summary>
    private void ExtractTracksFromItemsArray(JsonElement items, string sourceTitle, List<SearchQuery> tracks)
    {
        foreach (var item in items.EnumerateArray())
        {
            // Some payloads nest under "track"; others are the track object directly.
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("track", out var trackObj))
            {
                var track = ExtractTrackFromObject(trackObj, sourceTitle);
                if (track != null) tracks.Add(track);
                continue;
            }

            var direct = ExtractTrackFromObject(item, sourceTitle);
            if (direct != null) tracks.Add(direct);
        }
    }

    /// <summary>
    /// Finds matching brace position.
    /// </summary>
    private int FindMatchingBrace(string text, int startIndex)
    {
        if (startIndex < 0 || startIndex >= text.Length || text[startIndex] != '{')
            return -1;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = startIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Extracts playlist title from JSON.
    /// </summary>
    private string? ExtractPlaylistTitle(JsonElement root)
    {
        try
        {
            if (root.TryGetProperty("props", out var props) &&
                props.TryGetProperty("pageProps", out var pageProps) &&
                pageProps.TryGetProperty("initialState", out var state) &&
                state.TryGetProperty("headerData", out var header) &&
                header.TryGetProperty("title", out var titleElem))
            {
                return titleElem.GetString();
            }

            if (root.TryGetProperty("initialState", out var initialState) &&
                initialState.TryGetProperty("headerData", out var headerData) &&
                headerData.TryGetProperty("title", out var title))
            {
                return title.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Title extraction failed");
        }

        return null;
    }

    /// <summary>
    /// Recursively extracts all tracks from JSON.
    /// </summary>
    private void ExtractTracksRecursive(JsonElement element, List<SearchQuery> tracks, string sourceTitle)
    {
        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (IsTrackObject(element))
                    {
                        var track = ExtractTrackFromObject(element, sourceTitle);
                        // Validate ID: Must be alphanumeric and reasonable length (Spotify IDs are 22 chars)
                        if (track != null && !string.IsNullOrEmpty(track.SpotifyTrackId) && track.SpotifyTrackId.Length >= 20)
                        {
                             tracks.Add(track);
                        }
                    }

                    foreach (var prop in element.EnumerateObject())
                    {
                        ExtractTracksRecursive(prop.Value, tracks, sourceTitle);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        ExtractTracksRecursive(item, tracks, sourceTitle);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error in recursive extraction, continuing");
        }
    }

    /// <summary>
    /// Determines if JSON object is a track.
    /// </summary>
    private bool IsTrackObject(JsonElement obj)
    {
        try
        {
            var hasName = obj.TryGetProperty("name", out _);
            var hasArtist = obj.TryGetProperty("artists", out _) || obj.TryGetProperty("artist", out _);
            var hasId = obj.TryGetProperty("id", out _) || obj.TryGetProperty("uri", out _);

            return hasName && hasArtist && hasId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts track information from JSON object.
    /// </summary>
    private SearchQuery? ExtractTrackFromObject(JsonElement obj, string sourceTitle)
    {
        try
        {
            var title = obj.TryGetProperty("name", out var titleElem) ? titleElem.GetString() : null;
            if (string.IsNullOrEmpty(title))
                return null;

            string? artist = null;
            if (obj.TryGetProperty("artists", out var artistsElem))
            {
                if (artistsElem.ValueKind == JsonValueKind.Array)
                {
                    var firstArtist = artistsElem.EnumerateArray().FirstOrDefault();
                    artist = firstArtist.TryGetProperty("name", out var artistName) ? artistName.GetString() : null;
                }
                else
                {
                    artist = artistsElem.TryGetProperty("name", out var artistName) ? artistName.GetString() : null;
                }
            }
            else if (obj.TryGetProperty("artist", out var artistElem))
            {
                artist = artistElem.GetString();
            }

            var album = obj.TryGetProperty("album", out var albumElem) 
                ? (albumElem.ValueKind == JsonValueKind.Object 
                    ? (albumElem.TryGetProperty("name", out var albumName) ? albumName.GetString() : null)
                    : albumElem.GetString())
                : null;

            return new SearchQuery
            {
                Artist = artist ?? "Unknown",
                Title = title,
                Album = album ?? "Unknown",
                SourceTitle = sourceTitle,
                TrackHash = $"{artist}|{title}".ToLowerInvariant()
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error extracting track from JSON, skipping track");
            return null;
        }
    }
}
