using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;

namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates search operations including Soulseek searches, result ranking, and album grouping.
/// Extracted from MainViewModel to separate business logic from UI coordination.
/// </summary>
public class SearchOrchestrationService
{
    private readonly ILogger<SearchOrchestrationService> _logger;
    private readonly ISoulseekAdapter _soulseek;
    private readonly SearchQueryNormalizer _searchQueryNormalizer;
    private readonly SearchNormalizationService _searchNormalization; // Phase 4.6: Replaces broken parenthesis stripping
    private readonly ISafetyFilterService _safetyFilter; // Week 2: Gatekeeper
    private readonly AppConfig _config;
    
    private readonly ILibraryService _libraryService;

    // Throttling: Prevent getting banned by issuing too many searches at once
    private readonly SemaphoreSlim _searchSemaphore;
    
    public SearchOrchestrationService(
        ILogger<SearchOrchestrationService> logger,
        ISoulseekAdapter soulseek,
        SearchQueryNormalizer searchQueryNormalizer,
        SearchNormalizationService searchNormalization,
        ISafetyFilterService safetyFilter,
        AppConfig config,
        ILibraryService libraryService)
    {
        _logger = logger;
        _soulseek = soulseek;
        _searchQueryNormalizer = searchQueryNormalizer;
        _searchNormalization = searchNormalization;
        _safetyFilter = safetyFilter;
        _config = config;
        _libraryService = libraryService;
        
        // Initialize simple signaling semaphore
        // Golden Rule: Max 4 concurrent searches to avoid bans
        int maxSearches = Math.Min(4, Math.Max(1, _config.MaxConcurrentSearches));
        _searchSemaphore = new SemaphoreSlim(maxSearches);
    }
    
    public bool IsConnected => _soulseek.IsConnected;
    private int _activeSearchCount = 0;
    public int GetActiveSearchCount() => _activeSearchCount;

    /// <summary>
    /// Execute a search with the given parameters and stream ranked results.
    /// </summary>
    public async IAsyncEnumerable<Track> SearchAsync(
        string query,
        string preferredFormats,
        int minBitrate,
        int maxBitrate,
        bool isAlbumSearch, 
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var variations = _searchNormalization.GenerateSearchVariations(query);
        var seenHashes = new HashSet<string>();
        int totalFound = 0;

        // Throttling Check (Acquire once for the entire cascade)
        bool acquired = false;
        try 
        {
            await _searchSemaphore.WaitAsync(cancellationToken);
            acquired = true;
            Interlocked.Increment(ref _activeSearchCount);

            foreach (var variation in variations)
            {
                _logger.LogInformation("Cascade Search: Attempting variation '{Variation}' (Attempting {Idx} of {Count})", 
                    variation, variations.IndexOf(variation) + 1, variations.Count);

                bool foundInThisVariation = false;
                
                await foreach (var track in StreamAndRankResultsAsync(
                    variation, 
                    preferredFormats, 
                    minBitrate, 
                    maxBitrate, 
                    cancellationToken))
                {
                    if (seenHashes.Add(track.UniqueHash))
                    {
                        yield return track;
                        totalFound++;
                        foundInThisVariation = true;
                    }
                }

                // Smart Stop: If we found hits with a better strategy, don't fallback to noisier ones
                // Unless it's an album search where we want as much coverage as possible.
                if (foundInThisVariation && !isAlbumSearch && totalFound >= 5)
                {
                    _logger.LogInformation("Cascade Search: Found {Count} results for '{Variation}'. Stopping cascade.", totalFound, variation);
                    break;
                }

                if (!foundInThisVariation && variations.IndexOf(variation) < variations.Count - 1)
                {
                    _logger.LogInformation("Cascade Search: No new results for '{Variation}'. Trying next variation...", variation);
                    await Task.Delay(500, cancellationToken); // Stagger
                }
            }
        }
        finally
        {
            if (acquired)
            {
                Interlocked.Decrement(ref _activeSearchCount);
                _searchSemaphore.Release();
            }
        }
    }

    private async IAsyncEnumerable<Track> StreamAndRankResultsAsync(
        string normalizedQuery,
        string preferredFormats,
        int minBitrate,
        int maxBitrate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var formatFilter = preferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var searchTrack = new Track { Title = normalizedQuery };
        var evaluator = new FileConditionEvaluator();
        
        if (formatFilter.Length > 0)
        {
            evaluator.AddRequired(new FormatCondition { AllowedFormats = formatFilter.ToList() });
        }
        if (minBitrate > 0 || maxBitrate > 0)
        {
            evaluator.AddPreferred(new BitrateCondition 
            { 
                MinBitrate = minBitrate > 0 ? minBitrate : null, 
                MaxBitrate = maxBitrate > 0 ? maxBitrate : null 
            });
        }

        await foreach (var track in _soulseek.StreamResultsAsync(
            normalizedQuery,
            formatFilter,
            (minBitrate, maxBitrate),
            DownloadMode.Normal,
            cancellationToken))
        {
            _safetyFilter.EvaluateSafety(track, normalizedQuery);
            ResultSorter.CalculateRank(track, searchTrack, evaluator);
            yield return track;
        }
    }
    
    private List<Track> RankTrackResults(
        List<Track> results, 
        string normalizedQuery, 
        string[] formatFilter, 
        int minBitrate, 
        int maxBitrate)
    {
        if (results.Count == 0)
            return results;
            
        _logger.LogInformation("Ranking {Count} search results", results.Count);
        
        // Create search track from query for ranking
        var searchTrack = new Track { Title = normalizedQuery };
        
        // Create evaluator based on current filter settings
        var evaluator = new FileConditionEvaluator();
        if (formatFilter.Length > 0)
        {
            evaluator.AddRequired(new FormatCondition { AllowedFormats = formatFilter.ToList() });
        }
        
        if (minBitrate > 0 || maxBitrate > 0)
        {
            evaluator.AddPreferred(new BitrateCondition 
            { 
                MinBitrate = minBitrate > 0 ? minBitrate : null, 
                MaxBitrate = maxBitrate > 0 ? maxBitrate : null 
            });
        }
        
        // Rank the results
        var rankedResults = ResultSorter.OrderResults(results, searchTrack, evaluator);
        
        _logger.LogInformation("Results ranked successfully");
        return rankedResults.ToList();
    }
    

    private List<AlbumSearchResult> GroupResultsByAlbum(List<Track> tracks)
    {
        _logger.LogInformation("Grouping {Count} tracks into albums", tracks.Count);
        
        // Group by Album + Artist
        var grouped = tracks
            .Where(t => !string.IsNullOrEmpty(t.Album))
            .GroupBy(t => new { t.Album, t.Artist })
            .Select(g => new AlbumSearchResult
            {
                Album = g.Key.Album ?? "Unknown Album",
                Artist = g.Key.Artist ?? "Unknown Artist",
                TrackCount = g.Count(),
                Tracks = g.ToList(),
                // Use the highest bitrate track's info for album metadata
                AverageBitrate = (int)g.Average(t => t.Bitrate),
                Format = g.OrderByDescending(t => t.Bitrate).First().Format
            })
            .OrderByDescending(a => a.TrackCount)
            .ThenByDescending(a => a.AverageBitrate)
            .ToList();
        
        _logger.LogInformation("Grouped into {Count} albums", grouped.Count);
        return grouped;
    }
}

/// <summary>
/// Result of a search operation.
/// </summary>
public class SearchResult
{
    public int TotalCount { get; set; }
    public List<Track> Tracks { get; set; } = new();
    public List<AlbumSearchResult> Albums { get; set; } = new();
    public bool IsAlbumSearch { get; set; }
}

/// <summary>
/// Represents an album in search results.
/// </summary>
public class AlbumSearchResult
{
    public string Album { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public int AverageBitrate { get; set; }
    public string? Format { get; set; }
    public List<Track> Tracks { get; set; } = new();
}
