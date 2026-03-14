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
            var client = await _authService.GetAuthenticatedClientAsync();

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
                 _logger.LogWarning("Spotify API 403 Forbidden during Identification. Service degraded for 30 mins. Reason: {Body}", apiEx.Response?.Body ?? "Unknown");
                 _isServiceDegraded = true;
                 _retryAfter = DateTime.UtcNow.AddMinutes(30);
                 return new TrackEnrichmentResult { Success = false, Error = "Service Degraded (403)" };
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

    /// <summary>
    /// Fetches personalized recommendations based on the user's top tracks.
    /// </summary>
    /// <summary>
    /// Fetches personalized recommendations based on the user's top tracks.
    /// </summary>
    public async Task<System.Collections.Generic.List<SpotifyTrackViewModel>> GetRecommendationsAsync(int limit = 10)
    {
        return await GetRecommendationsForSeedsAsync(null, limit);
    }

    /// <summary>
    /// Fetches recommendations using a specific track as a seed.
    /// </summary>
    public async Task<System.Collections.Generic.List<SpotifyTrackViewModel>> GetRecommendationsForTrackAsync(string spotifyId, int limit = 20)
    {
        if (string.IsNullOrEmpty(spotifyId)) return new();
        return await GetRecommendationsForSeedsAsync(new System.Collections.Generic.List<string> { spotifyId }, limit);
    }

    private async Task<System.Collections.Generic.List<SpotifyTrackViewModel>> GetRecommendationsForSeedsAsync(System.Collections.Generic.List<string>? seedIds, int limit = 10)
    {
        // 1. Circuit Breaker Check
        if (_isServiceDegraded)
        {
            if (DateTime.UtcNow < _retryAfter)
            {
                _logger.LogWarning("Spotify API Circuit Breaker Active. Skipping recommendations.");
                return new System.Collections.Generic.List<SpotifyTrackViewModel>();
            }
            _isServiceDegraded = false; // Reset if cooldown passed
        }

        var result = new System.Collections.Generic.List<SpotifyTrackViewModel>();
        try
        {
            var client = await _authService.GetAuthenticatedClientAsync();
            
            // If no seeds provided, get user's top tracks
            System.Collections.Generic.List<string> effectiveSeeds = seedIds ?? new();
            if (!effectiveSeeds.Any())
            {
                try
                {
                    var topTracks = await client.Personalization.GetTopTracks(new PersonalizationTopRequest { Limit = 5 });
                    effectiveSeeds = topTracks.Items?.Select(t => t.Id).Take(5).ToList() ?? new System.Collections.Generic.List<string>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch top tracks for Recommendations seeds");
                    return result;
                }
            }
            
            if (!effectiveSeeds.Any())
            {
                _logger.LogWarning("No seeds available for Spotify Recommendations.");
                return result;
            }

            _logger.LogDebug("Using {Count} seeds for Spotify recommendations", effectiveSeeds.Count);

            var recommendationsReq = new RecommendationsRequest { Limit = limit };
            foreach (var trackId in effectiveSeeds)
            {
                recommendationsReq.SeedTracks.Add(trackId);
            }

            var recommendations = await client.Browse.GetRecommendations(recommendationsReq);
            
            if (recommendations.Tracks != null)
            {
                foreach (var track in recommendations.Tracks)
                {
                    result.Add(new SpotifyTrackViewModel
                    {
                        Id = track.Id,
                        Title = track.Name,
                        Artist = track.Artists.FirstOrDefault()?.Name,
                        AlbumName = track.Album.Name,
                        ImageUrl = track.Album.Images.OrderByDescending(i => i.Width).LastOrDefault()?.Url ?? "",
                        ISRC = track.ExternalIds != null && track.ExternalIds.ContainsKey("isrc") ? track.ExternalIds["isrc"] : null
                    });
                }
            }
        }
        catch (APIException apiEx)
        {
             if (apiEx.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) return result;

             if (apiEx.Response?.StatusCode == System.Net.HttpStatusCode.Forbidden)
             {
                 _logger.LogWarning("Spotify API 403 Forbidden in Recommendations. Disabling service.");
                 _isServiceDegraded = true;
                 _retryAfter = DateTime.UtcNow.AddMinutes(30);
                 return result;
             }

             _logger.LogError(apiEx, "Spotify API error in Recommendations");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Spotify recommendations");
        }
        
        return result;
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
                 _logger.LogWarning("Spotify API 403 Forbidden in GetArtistGenresBatchAsync. Disabling service temporarily.");
                 _isServiceDegraded = true;
                 _retryAfter = DateTime.UtcNow.AddMinutes(30);
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
}
