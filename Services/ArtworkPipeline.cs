using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Session 2 (Phase 2 Performance Overhaul): Artwork fetching and caching pipeline.
/// Supports parallel downloads with concurrency limiting (5 concurrent max).
/// Stores artwork in {AppData}/SLSKDONET/artwork/ directory.
/// </summary>
public class ArtworkPipeline
{
    private readonly ILogger<ArtworkPipeline> _logger;
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _downloadSemaphore = new(5); // Max 5 concurrent downloads
    private readonly HttpClient _httpClient = new();
    private readonly string _artworkCacheDir;
    
    public ArtworkPipeline(ILogger<ArtworkPipeline> logger, AppConfig config)
    {
        _logger = logger;
        _config = config;
        
        // Setup artwork cache directory
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _artworkCacheDir = Path.Combine(appData, "SLSKDONET", "artwork");
        Directory.CreateDirectory(_artworkCacheDir);
        
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }
    
    /// <summary>
    /// Fetches and caches artwork for a single track.
    /// Returns local file path if successful, null otherwise.
    /// </summary>
    public async Task<string?> GetArtworkAsync(string? albumArtUrl, string? spotifyAlbumId)
    {
        if (string.IsNullOrEmpty(albumArtUrl) || string.IsNullOrEmpty(spotifyAlbumId))
            return null;
        
        // Check cache first
        var cachedPath = GetCachedArtworkPath(spotifyAlbumId);
        if (File.Exists(cachedPath))
        {
            _logger.LogDebug("Artwork cache HIT: {AlbumId}", spotifyAlbumId);
            return cachedPath;
        }
        
        // Download with concurrency limit
        await _downloadSemaphore.WaitAsync();
        try
        {
            _logger.LogDebug("Downloading artwork: {Url}", albumArtUrl);
            return await DownloadAndCacheAsync(albumArtUrl, spotifyAlbumId);
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }
    
    /// <summary>
    /// Batch fetches artwork for multiple tracks in parallel.
    /// Much faster than sequential processing.
    /// </summary>
    public async Task<Dictionary<string, string>> GetArtworkBatchAsync(IEnumerable<PlaylistTrack> tracks)
    {
        var results = new ConcurrentDictionary<string, string>();
        
        var tracksWithArt = tracks
            .Where(t => !string.IsNullOrEmpty(t.AlbumArtUrl) && !string.IsNullOrEmpty(t.SpotifyAlbumId))
            .GroupBy(t => t.SpotifyAlbumId) // Deduplicate by album
            .Select(g => g.First())
            .ToList();
        
        _logger.LogInformation("Fetching artwork for {Count} unique albums", tracksWithArt.Count);
        
        await Parallel.ForEachAsync(tracksWithArt, 
            new ParallelOptions { MaxDegreeOfParallelism = 5 },
            async (track, ct) =>
            {
                try
                {
                    var artwork = await GetArtworkAsync(track.AlbumArtUrl, track.SpotifyAlbumId);
                    if (artwork != null && track.SpotifyAlbumId != null)
                    {
                        results[track.SpotifyAlbumId] = artwork;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch artwork for album {AlbumId}", track.SpotifyAlbumId);
                }
            });
        
        _logger.LogInformation("Artwork batch complete: {Success}/{Total} downloaded", 
            results.Count, tracksWithArt.Count);
        
        return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
    
    /// <summary>
    /// Downloads artwork from URL and caches it to disk.
    /// </summary>
    private async Task<string?> DownloadAndCacheAsync(string url, string albumId)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            var imageData = await response.Content.ReadAsByteArrayAsync();
            var cachePath = GetCachedArtworkPath(albumId);
            
            await File.WriteAllBytesAsync(cachePath, imageData);
            _logger.LogInformation("Cached artwork: {AlbumId} → {Path}", albumId, cachePath);
            
            return cachePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download artwork from {Url}", url);
            return null;
        }
    }
    
    /// <summary>
    /// Gets the cached artwork file path for an album ID.
    /// </summary>
    private string GetCachedArtworkPath(string albumId)
    {
        // Sanitize album ID for filename
        var sanitized = string.Join("_", albumId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_artworkCacheDir, $"{sanitized}.jpg");
    }
    
    /// <summary>
    /// Clears the artwork cache directory.
    /// </summary>
    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(_artworkCacheDir))
            {
                Directory.Delete(_artworkCacheDir, recursive: true);
                Directory.CreateDirectory(_artworkCacheDir);
                _logger.LogInformation("Artwork cache cleared");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear artwork cache");
        }
    }
    
    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public (int FileCount, long TotalSizeBytes) GetCacheStats()
    {
        try
        {
            if (!Directory.Exists(_artworkCacheDir))
                return (0, 0);
            
            var files = Directory.GetFiles(_artworkCacheDir);
            var totalSize = files.Sum(f => new FileInfo(f).Length);
            
            return (files.Length, totalSize);
        }
        catch
        {
            return (0, 0);
        }
    }
}
