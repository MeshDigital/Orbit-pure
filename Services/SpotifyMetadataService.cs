using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Utils;
using SLSKDONET.Models;
using SpotifyAPI.Web;

namespace SLSKDONET.Services;

/// <summary>
/// "The Gravity Well" - A resilient service for fetching, caching, and matching Spotify metadata.
/// Anchors local files to canonical Spotify identities using smart fuzzy search and audio analysis.
/// Refactored to use SpotifyBatchClient for strict throttling and reliable batching.
/// </summary>
public class SpotifyMetadataService : ISpotifyMetadataService
{
    private readonly ILogger<SpotifyMetadataService> _logger;
    private readonly SpotifyAuthService _authService;
    private readonly SpotifyBatchClient _batchClient;
    private readonly Configuration.AppConfig _config;


    // Negative cache duration for "Not Found" results
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromDays(7);
    
    // Positive cache duration (metadata rarely changes)
    private static readonly TimeSpan PositiveCacheDuration = TimeSpan.FromDays(30);

    public SpotifyMetadataService(
        ILogger<SpotifyMetadataService> logger,
        SpotifyAuthService authService,
        SpotifyBatchClient batchClient,
        Configuration.AppConfig config)
    {
        _logger = logger;
        _authService = authService;
        _batchClient = batchClient;
        _config = config;
    }



    // Workaround until I update SpotifyAuthService:
    // I'll implement a token fetcher using valid configured client
    private async Task UpdateBatchClientTokenAsync()
    {
        try
        {
            var token = await _authService.GetAccessTokenAsync();
            _batchClient.SetAccessToken(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to update batch client token: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Smart search for a track with fuzzy matching and confidence scoring.
    /// </summary>
    public async Task<FullTrack?> FindTrackAsync(string artist, string title, int? durationMs = null, bool forceRefresh = false)
    {
        // Check if user is authenticated
        if (!await _authService.IsAuthenticatedAsync())
        {
            _logger.LogDebug("Skipping Spotify search: Not authenticated");
            return null;
        }

        // 1. Check Cache (ONLY if not forced)
        string searchQuery = $"{artist} - {title}";
        string cacheKey = $"search:{StringDistanceUtils.Normalize(searchQuery)}";
        
        if (!forceRefresh)
        {
            var cached = await GetFromCacheAsync<FullTrack>(cacheKey);
            if (cached != null) return cached;
        }

        // 2. Execute Search via API
        var track = await SearchSpotifyWithSmartLogicAsync(artist, title, durationMs);

        // 3. Cache Result (Overwrites existing if forceRefresh was true)
        if (track != null)
        {
            await SaveToCacheAsync(cacheKey, track, PositiveCacheDuration);
        }
        else
        {
            await SaveToCacheAsync<FullTrack>(cacheKey, null, NegativeCacheDuration);
        }

        return track;
    }

    public async Task<bool> EnrichTrackAsync(PlaylistTrack track)
    {
        // Check if Spotify API is enabled
        if (!_config.SpotifyUseApi)
        {
            _logger.LogDebug("Skipping enrichment for {Artist} - {Title}: Spotify API disabled in settings", track.Artist, track.Title);
            return false;
        }

        // Check if user is authenticated before attempting enrichment
        if (!await _authService.IsAuthenticatedAsync())
        {
            _logger.LogDebug("Skipping enrichment for {Artist} - {Title}: Not authenticated", track.Artist, track.Title);
            return false;
        }

        var metadata = await FindTrackAsync(track.Artist, track.Title, null);

        if (metadata == null) return false;

        track.SpotifyTrackId = metadata.Id;
        track.ISRC = metadata.ExternalIds != null && metadata.ExternalIds.ContainsKey("isrc") ? metadata.ExternalIds["isrc"] : null;
        track.SpotifyAlbumId = metadata.Album.Id;
        track.SpotifyArtistId = metadata.Artists.FirstOrDefault()?.Id;
        track.AlbumArtUrl = metadata.Album.Images.FirstOrDefault()?.Url;
        track.ArtistImageUrl = null;
        track.Genres = null;
        track.Popularity = metadata.Popularity;
        track.CanonicalDuration = metadata.DurationMs;
        
        if (DateTime.TryParse(metadata.Album.ReleaseDate, out var releaseDate))
        {
            track.ReleaseDate = releaseDate;
        }



        track.IsEnriched = true;
        return true;
    }

    public async Task<bool> EnrichQueryAsync(SearchQuery query)
    {
        if (string.IsNullOrEmpty(query.Artist) || string.IsNullOrEmpty(query.Title)) return false;

        // Check if user is authenticated before attempting enrichment
        if (!await _authService.IsAuthenticatedAsync())
        {
            _logger.LogDebug("Skipping query enrichment for {Artist} - {Title}: Not authenticated", query.Artist, query.Title);
            return false;
        }

        var metadata = await FindTrackAsync(query.Artist, query.Title, query.Length * 1000, query.ForceRefresh);

        if (metadata == null) return false;

        query.SpotifyTrackId = metadata.Id;
        query.SpotifyAlbumId = metadata.Album.Id;
        query.SpotifyArtistId = metadata.Artists.FirstOrDefault()?.Id;
        query.AlbumArtUrl = metadata.Album.Images.FirstOrDefault()?.Url;
        query.ArtistImageUrl = null;
        query.Genres = null;
        query.Popularity = metadata.Popularity;
        query.CanonicalDuration = metadata.DurationMs;
        
        if (DateTime.TryParse(metadata.Album.ReleaseDate, out var releaseDate))
        {
            query.ReleaseDate = releaseDate;
        }

        return true;
    }


    private async Task<FullTrack?> SearchSpotifyWithSmartLogicAsync(string artist, string title, int? durationMs)
    {
        try
        {
            // Get authenticated client (automatically refreshes token if needed)
            var client = await _authService.GetAuthenticatedClientAsync();
            
            // Build search query
            var query = $"{artist} {title}";
            
            // Use native library search method
            var searchRequest = new SearchRequest(SearchRequest.Types.Track, query)
            {
                Limit = 5  // Get top 5 results for fuzzy matching
            };
            
            var searchResult = await client.Search.Item(searchRequest);
            
            if (searchResult?.Tracks?.Items == null || !searchResult.Tracks.Items.Any())
                return null;

            var candidates = searchResult.Tracks.Items;
            FullTrack? bestMatch = null;
            double bestScore = 0;

            foreach (var candidate in candidates)
            {
                string normalizedTitle = FilenameNormalizer.Normalize(title);
                string normalizedCandidateName = FilenameNormalizer.Normalize(candidate.Name);
                string normalizedArtist = FilenameNormalizer.Normalize(artist);
                string normalizedCandidateArtist = FilenameNormalizer.Normalize(candidate.Artists.FirstOrDefault()?.Name ?? "");
                
                double titleScore = StringDistanceUtils.GetNormalizedMatchScore(normalizedTitle, normalizedCandidateName);
                double artistScore = StringDistanceUtils.GetNormalizedMatchScore(normalizedArtist, normalizedCandidateArtist);
                
                double matchScore = (titleScore * 0.6) + (artistScore * 0.4);

                if (durationMs.HasValue)
                {
                    double diffSeconds = Math.Abs(candidate.DurationMs - durationMs.Value) / 1000.0;
                    if (diffSeconds > 5.0)
                    {
                        matchScore *= 0.5;
                    }
                    else if (diffSeconds <= 3.0 && matchScore > 0.7)
                    {
                        double durationBonus = 0.4 * (1.0 - (diffSeconds / 3.0));
                        matchScore = Math.Min(1.0, matchScore + durationBonus);
                    }
                }

                if (matchScore > bestScore)
                {
                    bestScore = matchScore;
                    bestMatch = candidate;
                }
            }

            if (bestMatch != null && bestScore >= 0.70)
            {
                return bestMatch;
            }
            
            return null;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Circuit Breaker"))
        {
            // Circuit breaker is open - this is expected behavior, log at debug level only
            _logger.LogDebug("Spotify search skipped for {Artist} - {Title}: Circuit breaker is open", artist, title);
            return null;
        }
        catch (APIException apiEx)
        {
            // Log detailed API error information
            _logger.LogError(apiEx, 
                "Spotify API error searching for {Artist} - {Title}. Status: {Status}, Response: {Response}", 
                artist, title,
                apiEx.Response?.StatusCode, 
                apiEx.Response?.Body);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Spotify for {Artist} - {Title}", artist, title);
            return null;
        }
    }

    private async Task<T?> GetFromCacheAsync<T>(string key) where T : class
    {
        try
        {
            using var context = new AppDbContext();
            var entity = await context.SpotifyMetadataCache.FindAsync(key);
            
            if (entity == null) return null;

            if (DateTime.UtcNow > entity.ExpiresAt)
            {
                context.SpotifyMetadataCache.Remove(entity);
                await context.SaveChangesAsync();
                return null;
            }

            if (string.IsNullOrEmpty(entity.DataJson)) return null;

            return JsonSerializer.Deserialize<T>(entity.DataJson);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task SaveToCacheAsync<T>(string key, T? data, TimeSpan duration)
    {
        try
        {
            using var context = new AppDbContext();
            var entity = new SpotifyMetadataCacheEntity
            {
                SpotifyId = key,
                DataJson = data != null ? JsonSerializer.Serialize(data) : "",
                CachedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(duration)
            };

            var existing = await context.SpotifyMetadataCache.FindAsync(key);
            if (existing != null)
            {
                existing.DataJson = entity.DataJson;
                existing.CachedAt = entity.CachedAt;
                existing.ExpiresAt = entity.ExpiresAt;
            }
            else
            {
                await context.SpotifyMetadataCache.AddAsync(entity);
            }

            await context.SaveChangesAsync();
        }
        catch (Exception)
        {
            // Ignore cache write failures
        }
    }

    public async Task ClearCacheAsync()
    {
        using var context = new AppDbContext();
        context.SpotifyMetadataCache.RemoveRange(context.SpotifyMetadataCache);
        await context.SaveChangesAsync();
    }
}
