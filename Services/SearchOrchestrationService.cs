using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private static readonly HashSet<string> LosslessFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "wav", "aif", "aiff", "ape", "alac"
    };

    private static readonly string[] StrictLosslessNegativeTokens =
    {
        "-mp3", "-aac", "-m4a", "-ogg", "-opus", "-wma", "-youtube", "-yt"
    };

    private readonly ILogger<SearchOrchestrationService> _logger;
    private readonly ISoulseekAdapter _soulseek;
    private readonly SearchQueryNormalizer _searchQueryNormalizer;
    private readonly SearchNormalizationService _searchNormalization; // Phase 4.6: Replaces broken parenthesis stripping
    private readonly ISafetyFilterService _safetyFilter; // Week 2: Gatekeeper
    private readonly Network.ProtocolHardeningService _hardeningService;
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
        Network.ProtocolHardeningService hardeningService,
        ILibraryService libraryService)
    {
        _logger = logger;
        _soulseek = soulseek;
        _searchQueryNormalizer = searchQueryNormalizer;
        _searchNormalization = searchNormalization;
        _safetyFilter = safetyFilter;
        _hardeningService = hardeningService;
        _config = config;
        _libraryService = libraryService;
        
        // Initialize simple signaling semaphore
        // Golden Rule: Baseline max 5 search lanes (optionally doubled for supporter accounts).
        int maxSearches = Math.Clamp(_config.MaxConcurrentSearches, 1, 5);
        if (_config.IsSoulseekSupporter)
        {
            var multiplier = Math.Max(1, _config.SupporterSearchLaneMultiplier);
            maxSearches = Math.Clamp(maxSearches * multiplier, 1, 10);
        }
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
        bool fastClearance = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var variations = _searchNormalization.GenerateSearchVariations(query);
        var seenHashes = new HashSet<string>();
        var formatFilter = preferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                
                var hardenedVariation = _hardeningService.NormalizeSearchQuery(variation);
                if (hardenedVariation == null) continue; // Skip banned query
                
                await foreach (var track in StreamAndRankResultsAsync(
                    hardenedVariation, 
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

                        if (fastClearance && !isAlbumSearch && IsFastLaneWinner(track, formatFilter, minBitrate))
                        {
                            _logger.LogInformation(
                                "Fast lane triggered for '{Variation}'. Short-circuiting cascade on idle peer {User} ({Bitrate} kbps, queue {QueueLength}).",
                                variation,
                                track.Username ?? "Unknown",
                                track.Bitrate,
                                track.QueueLength);
                            yield break;
                        }
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
                    await Task.Delay(Math.Max(50, _config.SearchThrottleDelayMs), cancellationToken); // Stagger
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

    private static bool IsFastLaneWinner(Track track, string[] formatFilter, int minBitrate)
    {
        if (track.IsFlagged)
            return false;

        if (track.QueueLength > ScoringConstants.Availability.FastLaneMaxQueue)
            return false;

        if (!track.HasFreeUploadSlot && track.QueueLength != 0)
            return false;

        var ext = track.GetExtension().ToLowerInvariant();
        var format = (track.Format ?? string.Empty).ToLowerInvariant();
        if (formatFilter.Length > 0 &&
            !formatFilter.Contains(format, StringComparer.OrdinalIgnoreCase) &&
            !formatFilter.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var isLossless = ext is "flac" or "wav" or "aif" or "aiff" or "ape" or "alac" ||
                         format is "flac" or "wav" or "aif" or "aiff" or "ape" or "alac";

        var effectiveMinBitrate = Math.Max(minBitrate, ScoringConstants.Availability.FastLaneMinBitrate);
        return isLossless || track.Bitrate >= effectiveMinBitrate;
    }

    private async IAsyncEnumerable<Track> StreamAndRankResultsAsync(
        string normalizedQuery,
        string preferredFormats,
        int minBitrate,
        int maxBitrate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int brainBufferSeconds = 3;
        const int brainWinnerCount = 5;

        var formatFilter = preferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var networkQuery = BuildNetworkQuery(normalizedQuery, formatFilter, minBitrate);
        var bufferedTracks = new List<Track>();
        using var brainBufferCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        brainBufferCts.CancelAfter(TimeSpan.FromSeconds(brainBufferSeconds));

        try
        {
            await foreach (var track in _soulseek.StreamResultsAsync(
                networkQuery,
                formatFilter,
                (minBitrate, maxBitrate),
                DownloadMode.Normal,
                brainBufferCts.Token))
            {
                _safetyFilter.EvaluateSafety(track, normalizedQuery);
                bufferedTracks.Add(track);
            }
        }
        catch (OperationCanceledException) when (brainBufferCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Brain buffer window elapsed for query '{Query}'. Ranking {Count} buffered candidates.",
                normalizedQuery,
                bufferedTracks.Count);
        }

        if (bufferedTracks.Count == 0)
        {
            yield break;
        }

        var ranked = RankTrackResults(bufferedTracks, normalizedQuery, formatFilter, minBitrate, maxBitrate)
            .Take(brainWinnerCount)
            .ToList();

        var audit = BuildSelectionAudit(
            normalizedQuery,
            networkQuery,
            brainBufferSeconds,
            minBitrate,
            maxBitrate,
            formatFilter,
            bufferedTracks,
            ranked);
        LogSelectionAudit(audit);

        foreach (var track in ranked)
        {
            yield return track;
        }
    }

    private static string BuildNetworkQuery(string normalizedQuery, string[] formatFilter, int minBitrate)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return normalizedQuery;

        var hasStrictLosslessFormatFilter = formatFilter.Length > 0 && formatFilter.All(format =>
            LosslessFormats.Contains(format));
        var hasStrictLosslessBitrate = minBitrate >= 701;
        var shouldInjectNegatives = hasStrictLosslessFormatFilter || hasStrictLosslessBitrate;

        if (!shouldInjectNegatives)
            return normalizedQuery;

        var existingTokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingNegatives = StrictLosslessNegativeTokens
            .Where(token => !existingTokens.Contains(token))
            .ToArray();

        if (missingNegatives.Length == 0)
            return normalizedQuery;

        return $"{normalizedQuery} {string.Join(" ", missingNegatives)}";
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

    private SearchSelectionAudit BuildSelectionAudit(
        string normalizedQuery,
        string networkQuery,
        int bufferSeconds,
        int minBitrate,
        int maxBitrate,
        string[] formatFilter,
        List<Track> candidates,
        List<Track> winners)
    {
        return new SearchSelectionAudit
        {
            TimestampUtc = DateTime.UtcNow,
            Query = normalizedQuery,
            NetworkQuery = networkQuery,
            BufferSeconds = bufferSeconds,
            CandidateCount = candidates.Count,
            WinnerCount = winners.Count,
            MinBitrate = minBitrate > 0 ? minBitrate : null,
            MaxBitrate = maxBitrate > 0 ? maxBitrate : null,
            PreferredFormats = formatFilter,
            Candidates = candidates.Select(MapAuditCandidate).ToList(),
            Winners = winners.Select(MapAuditCandidate).ToList()
        };
    }

    private static SearchSelectionAuditCandidate MapAuditCandidate(Track track)
    {
        return new SearchSelectionAuditCandidate
        {
            Username = track.Username ?? string.Empty,
            Filename = track.Filename ?? string.Empty,
            Format = track.Format ?? track.GetExtension(),
            Bitrate = track.Bitrate,
            QueuePos = track.QueueLength,
            PeerSpeed = track.UploadSpeed,
            IsDedup = TryGetDedupSignal(track),
            IsFlagged = track.IsFlagged,
            Rank = track.CurrentRank,
            ScoreBreakdown = track.ScoreBreakdown ?? string.Empty
        };
    }

    private static bool TryGetDedupSignal(Track track)
    {
        if (track.Metadata == null ||
            !track.Metadata.TryGetValue("IsDedup", out var isDedupRaw) ||
            isDedupRaw is null)
        {
            return false;
        }

        return isDedupRaw switch
        {
            bool boolValue => boolValue,
            string stringValue when bool.TryParse(stringValue, out var parsed) => parsed,
            _ => false
        };
    }

    private void LogSelectionAudit(SearchSelectionAudit audit)
    {
        _logger.LogInformation(
            "[SEARCH_AUDIT] Query='{Query}' NetworkQuery='{NetworkQuery}' Candidates={CandidateCount} Winners={WinnerCount} Buffer={BufferSeconds}s",
            audit.Query,
            audit.NetworkQuery,
            audit.CandidateCount,
            audit.WinnerCount,
            audit.BufferSeconds);

        _logger.LogDebug("[SEARCH_AUDIT] Candidates {Payload}", JsonSerializer.Serialize(audit.Candidates));
        _logger.LogDebug("[SEARCH_AUDIT] Winners {Payload}", JsonSerializer.Serialize(audit.Winners));
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
