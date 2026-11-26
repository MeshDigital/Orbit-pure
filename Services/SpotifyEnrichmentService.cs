using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Service to enrich local tracks with deep metadata from Spotify (Audio Features).
/// Uses SpotifyAPI.Web and existing authentication.
/// </summary>
public class SpotifyEnrichmentService
{
    private readonly SpotifyAuthService _authService;
    private readonly ILogger<SpotifyEnrichmentService> _logger;
    private readonly DatabaseService _databaseService; // Phase 5: Cache-First

    // Phase 5: Circuit Breaker
    private static bool _isServiceDegraded = false;
    private static DateTime _retryAfter = DateTime.MinValue;

    public static bool IsServiceDegraded => _isServiceDegraded;

    public SpotifyEnrichmentService(
        SpotifyAuthService authService, 
        ILogger<SpotifyEnrichmentService> logger,
        DatabaseService databaseService)
    {
        _authService = authService;
        _logger = logger;
        _databaseService = databaseService;
    }

    /// <summary>
    /// Phase 5: Cache-First Proxy. Returns cached metadata if available. 
    /// Returns NULL if missing (does not hit API).
    /// </summary>
    public async Task<TrackEnrichmentResult?> GetCachedMetadataAsync(string artist, string title)
    {
        // Try exact match first
        // Note: Ideally we use a dedicated cache table or query PlaylistTracks/LibraryEntries
        // For now, checks LibraryEntries which acts as the 'Gravity Well' cache
        var cached = await _databaseService.FindEnrichedTrackAsync(artist, title);
        if (cached != null)
        {
             return new TrackEnrichmentResult
             {
                 Success = true,
                 SpotifyId = cached.SpotifyTrackId ?? string.Empty,
                 OfficialArtist = cached.Artist ?? string.Empty,
                 OfficialTitle = cached.Title ?? string.Empty,
                 Bpm = cached.SpotifyBPM ?? cached.BPM,
                 Energy = cached.Energy,
                 Valence = cached.Valence,
                 Danceability = cached.Danceability,
                 MusicalKey = cached.MusicalKey,
                 AlbumArtUrl = cached.AlbumArtUrl ?? string.Empty,
                 ISRC = cached.ISRC
             };
        }
        return null;
    }

    /// <summary>
    /// Fetches deep metadata (BPM, Energy, Valence) for a track.
    /// </summary>
    /// <summary>
    /// Stage 1: Identify a track (get Spotify ID) from metadata.
    /// </summary>
    public async Task<TrackEnrichmentResult> IdentifyTrackAsync(string artist, string trackName)
    {
        // Circuit Breaker Check
        if (_isServiceDegraded)
        {
            if (DateTime.UtcNow < _retryAfter)
            {
               _logger.LogWarning("Spotify API Circuit Breaker Active. Skipping request.");
               return new TrackEnrichmentResult { Success = false, Error = "Service Degraded (Rate Limit)" };
            }
            _isServiceDegraded = false; // Reset if cooldown passed
        }

        try 
        {
            // Get a usable Spotify client (OAuth if available, otherwise Client Credentials)
            SpotifyClient client;
            try
            {
                client = await _authService.GetClientAsync();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug("Cannot access Spotify API for track identification: {Message}", ex.Message);
                return new TrackEnrichmentResult { Success = false, Error = "Spotify API not available" };
            }

            string sanitizedArtist = artist;
            string sanitizedTitle = trackName;

            // FIX: Handle "Unknown Artist" and bad metadata
            if (string.Equals(artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase) || 
                string.IsNullOrWhiteSpace(artist))
            {
                // Try to parse "Artist - Title" from track name if it looks like a filename/combined string
                if (trackName.Contains(" - "))
                {
                    var parts = trackName.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        sanitizedArtist = parts[0].Trim();
                        sanitizedTitle = parts[1].Trim();
                        _logger.LogInformation("Parsed 'Unknown Artist' track as: Artist='{A}', Title='{T}'", sanitizedArtist, sanitizedTitle);
                    }
                    else
                    {
                         sanitizedArtist = ""; // Search just by title
                    }
                }
                else
                {
                    sanitizedArtist = ""; // Clean it so we don't send "Unknown Artist" to Spotify
                }
            }

            var query = !string.IsNullOrEmpty(sanitizedArtist) 
                ? $"track:{sanitizedTitle} artist:{sanitizedArtist}" 
                : $"track:{sanitizedTitle}"; // Fallback to just title search

            // Fallback for tricky titles: remove "feat." and parentheses
            // e.g. "Song (Original Mix)" -> "Song"
            // This happens if exact search fails
            
            var searchReq = new SearchRequest(SearchRequest.Types.Track, query) { Limit = 1 };
            var response = await client.Search.Item(searchReq);
            var track = response.Tracks.Items?.FirstOrDefault();

            if (track == null)
            {
                 // Fallback 1: Simpler query (concatenated)
                 var simplerQuery = !string.IsNullOrEmpty(sanitizedArtist) 
                    ? $"{sanitizedArtist} {sanitizedTitle}" 
                    : sanitizedTitle;
                    
                 searchReq = new SearchRequest(SearchRequest.Types.Track, simplerQuery) { Limit = 1 };
                 response = await client.Search.Item(searchReq);
                 track = response.Tracks.Items?.FirstOrDefault();
            }
            
            if (track == null && (sanitizedTitle.Contains("(") || sanitizedTitle.Contains("[")))
            {
                // Fallback 2: Remove brackets/parentheses (e.g. "Title (Remix)" -> "Title")
                var cleanTitle = System.Text.RegularExpressions.Regex.Replace(sanitizedTitle, @"\s*[\(\[].*?[\)\]]", "").Trim();
                if (!string.IsNullOrEmpty(cleanTitle) && cleanTitle != sanitizedTitle)
                {
                     var simpleQuery = !string.IsNullOrEmpty(sanitizedArtist) 
                        ? $"{sanitizedArtist} {cleanTitle}" 
                        : cleanTitle;
                        
                     searchReq = new SearchRequest(SearchRequest.Types.Track, simpleQuery) { Limit = 1 };
                     response = await client.Search.Item(searchReq);
                     track = response.Tracks.Items?.FirstOrDefault();
                }
            }

            if (track == null)
            {
                 _logger.LogWarning("No Spotify match found for: {Artist} - {TrackName}", artist, trackName);
                 return new TrackEnrichmentResult { Success = false, Error = "No match found" };
            }

            return new TrackEnrichmentResult
            {
                Success = true,
                SpotifyId = track.Id,
                SpotifyAlbumId = track.Album.Id,
                SpotifyArtistId = track.Artists.FirstOrDefault()?.Id ?? string.Empty,
                OfficialArtist = track.Artists.FirstOrDefault()?.Name ?? artist,
                OfficialTitle = track.Name,
                AlbumArtUrl = track.Album.Images.OrderByDescending(i => i.Width).FirstOrDefault()?.Url ?? "",
                ISRC = track.ExternalIds != null && track.ExternalIds.ContainsKey("isrc") ? track.ExternalIds["isrc"] : null,
                // Feature fields remain null here
            };
        }
        catch (APITooManyRequestsException ex)
        {
            _logger.LogError("Spotify 429 Rate Limit hit. Backing off for {Seconds}s.", ex.RetryAfter.TotalSeconds);
            _isServiceDegraded = true;
            _retryAfter = DateTime.UtcNow.Add(ex.RetryAfter).AddSeconds(1); // Buffer
            return new TrackEnrichmentResult { Success = false, Error = "Rate Limit Hit" };
        }
        catch (APIException apiEx)
        {
             if (apiEx.Response?.StatusCode == System.Net.HttpStatusCode.Forbidden)
             {
                 // Epic #508 / #510: A 403 on one endpoint must never degrade unrelated endpoints for 30 min.
                 _logger.LogWarning("Spotify API 403 Forbidden during Identification. Reason: {Body}", apiEx.Response?.Body ?? "Unknown");
                 return new TrackEnrichmentResult { Success = false, Error = "Forbidden (403)" };
             }
             _logger.LogError(apiEx, "Spotify Identification failed (API Error) for {Artist} - {Track}", artist, trackName);
             return new TrackEnrichmentResult { Success = false, Error = apiEx.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify Identification failed for {Artist} - {Track}", artist, trackName);
            return new TrackEnrichmentResult { Success = false, Error = ex.Message };
        }
    }


    /// <summary>
    /// Wrapper for single-track enrichment (legacy/convenience).
    /// </summary>
    public async Task<TrackEnrichmentResult> GetDeepMetadataAsync(string artist, string trackName)
    {
        // 1. Identify
        var identification = await IdentifyTrackAsync(artist, trackName);
        if (!identification.Success || identification.SpotifyId == null) return identification;

        // 2. Fetch Features (Stage 2) - REMOVED: Paywalled API
        _logger.LogInformation("Spotify: Identified '{Title}' (Skipping paywalled audio features)", identification.OfficialTitle);

        return identification;
    }

    // Epic #508 / #510: GET /recommendations was removed by Spotify for dev-mode apps (Nov 2024+).
    // Circuit breaker (_isServiceDegraded) only trips on 429 rate limits, never on 403/404.
    internal static void ResetCircuitBreaker()
    {
        _isServiceDegraded = false;
        _retryAfter = DateTime.MinValue;
    }

    internal static void RecordApiError(System.Net.HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    {
        if (statusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _isServiceDegraded = true;
            _retryAfter = DateTime.UtcNow.Add(retryAfter ?? TimeSpan.FromSeconds(30)).AddSeconds(1);
        }
    }

    /// <summary>
    /// Stage 3: Batch fetch Artist details to get Genres.
    /// Tracks don't contain genres, but Artists do.
    /// </summary>
    public async Task<Dictionary<string, System.Collections.Generic.List<string>>> GetArtistGenresBatchAsync(System.Collections.Generic.List<string> artistIds)
    {
        var result = new Dictionary<string, System.Collections.Generic.List<string>>();
        if (!artistIds.Any()) return result;

        try
        {
            var client = await _authService.GetAuthenticatedClientAsync();
            
            // API allows max 50 IDs per call for Artists
            var chunkedIds = artistIds.Chunk(50);
            
            foreach (var chunk in chunkedIds)
            {
                var req = new ArtistsRequest(chunk.ToList());
                var artistsResponse = await client.Artists.GetSeveral(req);
                
                if (artistsResponse?.Artists != null)
                {
                    foreach (var artist in artistsResponse.Artists)
                    {
                        if (artist != null && artist.Genres != null && artist.Genres.Any())
                        {
                            result[artist.Id] = artist.Genres;
                        }
                    }
                }
            }
        }
        catch (APITooManyRequestsException ex)
        {
            _logger.LogError("Spotify 429 Rate Limit hit (Genres Batch). Backing off for {Seconds}s.", ex.RetryAfter.TotalSeconds);
            _isServiceDegraded = true;
            _retryAfter = DateTime.UtcNow.Add(ex.RetryAfter).AddSeconds(1);
        }
        catch (APIException apiEx)
        {
            if (apiEx.Response?.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                 // Epic #508 / #510: Never trip the global circuit breaker on 403 from an unrelated endpoint.
                 _logger.LogWarning("Spotify API 403 Forbidden in GetArtistGenresBatchAsync. Reason: {Body}", apiEx.Response?.Body ?? "Unknown");
            }
            else
            {
                _logger.LogError(apiEx, "Spotify API error in GetArtistGenresBatchAsync");
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to batch fetch artist genres");
        }
        
        return result;
    }

    /// <summary>
    /// Fetches the current user's personal playlists.
    /// </summary>
    public async Task<System.Collections.Generic.IEnumerable<object>> GetCurrentUserPlaylistsAsync()
    {
        try
        {
            var client = await _authService.GetAuthenticatedClientAsync();
            if (client == null) return new System.Collections.Generic.List<object>();

            var request = new PlaylistCurrentUsersRequest { Limit = 50 };
            var firstPage = await client.Playlists.CurrentUsers(request);
            
            return (System.Collections.Generic.IEnumerable<object>?)firstPage.Items ?? Enumerable.Empty<object>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch user playlists");
            return new System.Collections.Generic.List<object>();
        }
    }

    /// <summary>
    /// Fetches the current user's saved (liked) tracks.
    /// </summary>
    public async Task<System.Collections.Generic.List<SavedTrack>> GetCurrentUserSavedTracksAsync(int limit = 50)
    {
        try
        {
            var client = await _authService.GetAuthenticatedClientAsync();
            if (client == null) return new System.Collections.Generic.List<SavedTrack>();

            var request = new LibraryTracksRequest { Limit = limit };
            var response = await client.Library.GetTracks(request);
            
            return response.Items?.ToList() ?? new System.Collections.Generic.List<SavedTrack>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch saved tracks");
            return new System.Collections.Generic.List<SavedTrack>();
        }
    }

    /// <summary>
    /// Fetches tracks from a specific playlist by ID.
    /// Used by Discovery Hub Workbench to load playlist contents.
    /// </summary>
    public async Task<System.Collections.Generic.List<object>> GetPlaylistTracksAsync(string playlistId)
    {
        try
        {
            var client = await _authService.GetAuthenticatedClientAsync();
            if (client == null) return new System.Collections.Generic.List<object>();

            var request = new PlaylistGetItemsRequest { Limit = 100 };
            var firstPage = await client.Playlists.GetItems(playlistId, request);
            
            // Paginate to get all tracks
            var allItems = await client.PaginateAll(firstPage);
            return allItems.Cast<object>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch playlist tracks for {PlaylistId}", playlistId);
            return new System.Collections.Generic.List<object>();
        }
    }
}

public class SpotifyTrackViewModel
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumName { get; set; }
    public string? ImageUrl { get; set; }
    public string? ISRC { get; set; }
    public bool InLibrary { get; set; }

    /// <summary>Lazily loads the track's art from its (remote) URL on first access. Assigned by the consumer since this is a plain data model with no DI access of its own.</summary>
    public SLSKDONET.Models.ArtworkProxy? Artwork { get; set; }
}
