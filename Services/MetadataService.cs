using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SpotifyAPI.Web;

namespace SLSKDONET.Services;

public interface IMetadataService
{
    Task<string?> GetAlbumArtUrlAsync(string artist, string album);
}

public class MetadataService : IMetadataService
{
    private readonly ILogger<MetadataService> _logger;
    private readonly AppConfig _config;
    
    // Simple memory cache: key="artist|album", value=url
    private readonly ConcurrentDictionary<string, string?> _cache = new();
    
    // Cached Spotify client
    private SpotifyClient? _spotifyClient;
    private DateTime _tokenExpiry = DateTime.MinValue;
    
    // Rate limiting to prevent API throttling
    private static readonly SemaphoreSlim _rateLimitSemaphore = new SemaphoreSlim(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;
    private const int MinRequestIntervalMs = 100; // Minimum 100ms between requests

    // Shared HttpClient for MusicBrainz / Cover Art Archive requests
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "ORBIT-Music-Engine/1.0.0 ( https://github.com/MeshDigital/ORBIT )" } }
    };

    public MetadataService(ILogger<MetadataService> logger, AppConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<string?> GetAlbumArtUrlAsync(string artist, string album)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return null;

        var key = $"{artist.ToLowerInvariant()}|{album.ToLowerInvariant()}";
        
        if (_cache.TryGetValue(key, out var cachedUrl))
            return cachedUrl;

        // Try Spotify first if enabled and credentials are configured
        if (_config.SpotifyUseApi &&
            !string.IsNullOrWhiteSpace(_config.SpotifyClientId) &&
            !string.IsNullOrWhiteSpace(_config.SpotifyClientSecret))
        {
            var spotifyUrl = await GetAlbumArtFromSpotifyAsync(artist, album);
            if (spotifyUrl != null)
            {
                _cache[key] = spotifyUrl;
                return spotifyUrl;
            }
        }

        // Fall back to MusicBrainz Cover Art Archive (free, no auth required)
        var mbUrl = await GetAlbumArtFromMusicBrainzAsync(artist, album);
        _cache[key] = mbUrl;
        return mbUrl;
    }

    /// <summary>
    /// Fetches album art URL from Spotify using Client Credentials (no user login required).
    /// </summary>
    private async Task<string?> GetAlbumArtFromSpotifyAsync(string artist, string album)
    {
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Rate limiting to prevent hitting Spotify's API limits
                await _rateLimitSemaphore.WaitAsync();
                try
                {
                    // Enforce minimum delay between requests
                    var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
                    if (timeSinceLastRequest < TimeSpan.FromMilliseconds(MinRequestIntervalMs))
                    {
                        var delay = MinRequestIntervalMs - (int)timeSinceLastRequest.TotalMilliseconds;
                        await Task.Delay(delay);
                    }
                    _lastRequestTime = DateTime.UtcNow;
                }
                finally
                {
                    _rateLimitSemaphore.Release();
                }

                var client = await GetClientAsync();
                
                // Search for the album
                var request = new SearchRequest(SearchRequest.Types.Album, $"{artist} {album}");
                request.Limit = 1;
                
                var response = await client.Search.Item(request);
                if (response.Albums?.Items?.FirstOrDefault() is SimpleAlbum result)
                {
                    // Prefer Medium image (usually 300x300 or 640x640)
                    // Images are sorted by size descending usually. [0]=640, [1]=300, [2]=64
                    var image = result.Images?.FirstOrDefault();
                    if (image != null)
                    {
                        return image.Url;
                    }
                }
                
                return null;
            }
            catch (APITooManyRequestsException ex)
            {
                // Respect Retry-After header from Spotify
                var retryAfter = ex.RetryAfter.TotalSeconds > 0 ? ex.RetryAfter : TimeSpan.FromSeconds(5);
                
                if (attempt < maxRetries - 1)
                {
                    _logger.LogWarning(
                        "Spotify API rate limit hit for {Artist} - {Album}. Retrying after {Seconds}s (attempt {Attempt}/{Max})",
                        artist, album, retryAfter.TotalSeconds, attempt + 1, maxRetries);
                    await Task.Delay(retryAfter);
                    continue; // Retry
                }
                else
                {
                    _logger.LogWarning(ex, 
                        "Failed to fetch metadata for {Artist} - {Album} after {Max} attempts", 
                        artist, album, maxRetries);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch album art from Spotify for {Artist} - {Album}", artist, album);
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches album art URL via the MusicBrainz search API + Cover Art Archive.
    /// No authentication required. Used as a fallback when Spotify is unavailable.
    /// </summary>
    private async Task<string?> GetAlbumArtFromMusicBrainzAsync(string artist, string album)
    {
        try
        {
            // Step 1: Search MusicBrainz for a matching release
            var encodedArtist = Uri.EscapeDataString(artist);
            var encodedAlbum = Uri.EscapeDataString(album);
            var searchUrl = $"https://musicbrainz.org/ws/2/release/?query=artist:{encodedArtist}+release:{encodedAlbum}&limit=1&fmt=json";

            var searchResponse = await _httpClient.GetAsync(searchUrl);
            if (!searchResponse.IsSuccessStatusCode)
            {
                _logger.LogDebug("MusicBrainz search failed for {Artist} - {Album}: {Status}", artist, album, searchResponse.StatusCode);
                return null;
            }

            var searchJson = await searchResponse.Content.ReadAsStringAsync();
            using var searchDoc = JsonDocument.Parse(searchJson);

            if (!searchDoc.RootElement.TryGetProperty("releases", out var releases) || releases.GetArrayLength() == 0)
            {
                _logger.LogDebug("No MusicBrainz release found for {Artist} - {Album}", artist, album);
                return null;
            }

            var mbid = releases[0].GetProperty("id").GetString();
            if (string.IsNullOrEmpty(mbid))
                return null;

            // Step 2: Fetch cover art from Cover Art Archive
            var coverUrl = $"https://coverartarchive.org/release/{mbid}/front-250";
            var coverResponse = await _httpClient.GetAsync(coverUrl);

            // HttpClient follows redirects automatically; IsSuccessStatusCode means we got the image
            if (coverResponse.IsSuccessStatusCode)
            {
                // The final URL (after redirect) is the actual CDN image URL
                var imageUrl = coverResponse.RequestMessage?.RequestUri?.ToString() ?? coverUrl;
                _logger.LogDebug("MusicBrainz cover art found for {Artist} - {Album}: {Url}", artist, album, imageUrl);
                return imageUrl;
            }

            _logger.LogDebug("No cover art in Cover Art Archive for release {Mbid} ({Artist} - {Album})", mbid, artist, album);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MusicBrainz cover art lookup failed for {Artist} - {Album}", artist, album);
            return null;
        }
    }

    private async Task<SpotifyClient> GetClientAsync()
    {
        if (_spotifyClient != null && DateTime.UtcNow < _tokenExpiry)
            return _spotifyClient;

        var config = SpotifyClientConfig.CreateDefault();
        var request = new ClientCredentialsRequest(_config.SpotifyClientId!, _config.SpotifyClientSecret!);
        var response = await new OAuthClient(config).RequestToken(request);
        
        // Refresh 1 minute before actual expiry
        _tokenExpiry = DateTime.UtcNow.AddSeconds(response.ExpiresIn - 60);
        _spotifyClient = new SpotifyClient(config.WithToken(response.AccessToken));
        
        return _spotifyClient;
    }
}
