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
        
    }
    
    public bool IsConnected => _soulseek.IsLoggedIn;
    public bool IsLoggedIn => _soulseek.IsLoggedIn;
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
        var searchPlan = _searchNormalization.BuildSearchPlan(query);
        await foreach (var track in SearchAsyncCore(
            query,
            searchPlan,
            preferredFormats,
            minBitrate,
            maxBitrate,
            isAlbumSearch,
            fastClearance,
            cancellationToken))
        {
            yield return track;
        }
    }

    public async IAsyncEnumerable<Track> SearchAsync(
        PlaylistTrack target,
        string query,
        string preferredFormats,
        int minBitrate,
        int maxBitrate,
        bool isAlbumSearch,
        bool fastClearance = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var searchPlan = _searchNormalization.BuildSearchPlan(target, query);
        await foreach (var track in SearchAsyncCore(
            query,
            searchPlan,
            preferredFormats,
            minBitrate,
            maxBitrate,
            isAlbumSearch,
            fastClearance,
            cancellationToken))
        {
            yield return track;
        }
    }

    private async IAsyncEnumerable<Track> SearchAsyncCore(
        string query,
        SearchPlan searchPlan,
        string preferredFormats,
        int minBitrate,
        int maxBitrate,
        bool isAlbumSearch,
        bool fastClearance,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var activeNow = Math.Max(1, Volatile.Read(ref _activeSearchCount) + 1);
        var executionProfile = SearchLoadSheddingPolicy.Compute(_config, activeNow);

        var generatedLanes = searchPlan.EnumerateLanes().ToList();

        if (generatedLanes.Count == 0)
        {
            generatedLanes = _searchNormalization.GenerateSearchVariations(query)
                .Select((variation, index) => new PlannedSearchLane(index switch
                {
                    0 => SearchQueryLane.Strict,
                    1 => SearchQueryLane.Standard,
                    _ => SearchQueryLane.Desperate
                }, variation))
                .ToList();
        }

        var variationCap = Math.Max(1, executionProfile.EffectiveVariationCap);
        var variations = generatedLanes.Take(variationCap).ToList();
        if (generatedLanes.Count > variations.Count)
        {
            _logger.LogInformation(
                "Cascade variation cap applied for query '{Query}': using {Used}/{Generated} variations.",
                query,
                variations.Count,
                generatedLanes.Count);
        }

            if (executionProfile.PressureLevel != SearchPressureLevel.Normal)
            {
                _logger.LogInformation(
                "Search load shedding active ({Pressure}) for '{Query}': responseLimit={ResponseLimit}, fileLimit={FileLimit}, variationCap={VariationCap}, extraDelayMs={ExtraDelayMs}",
                executionProfile.PressureLevel,
                query,
                executionProfile.EffectiveResponseLimit,
                executionProfile.EffectiveFileLimit,
                executionProfile.EffectiveVariationCap,
                executionProfile.AdditionalThrottleDelayMs);
            }

        var seenHashes = new HashSet<string>();
        var formatFilter = preferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var strictSufficientResultCount = Math.Max(1, _config.StrictSearchSufficientResultCount);
        int totalFound = 0;

        bool countedAsActive = false;
        try 
        {
            Interlocked.Increment(ref _activeSearchCount);
            countedAsActive = true;

            for (var variationIndex = 0; variationIndex < variations.Count; variationIndex++)
            {
                var plannedLane = variations[variationIndex];
                var variation = plannedLane.Query;
                _logger.LogInformation("Cascade Search: Attempting {Lane} variation '{Variation}' (Attempting {Idx} of {Count})", 
                    plannedLane.Lane, variation, variationIndex + 1, variations.Count);

                if (plannedLane.Lane == SearchQueryLane.Desperate && !isAlbumSearch && totalFound > 0)
                {
                    _logger.LogInformation(
                        "Cascade Search: Skipping desperate fallback for '{Query}' because accepted results already exist ({Count}).",
                        query,
                        totalFound);
                    break;
                }

                if (plannedLane.Lane == SearchQueryLane.Desperate)
                {
                    var desperateDelayMs = Math.Max(
                        _config.SearchThrottleDelayMs + executionProfile.AdditionalThrottleDelayMs,
                        Math.Clamp(_config.RelaxationTimeoutSeconds, 1, 30) * 1000);
                    _logger.LogInformation(
                        "Cascade Search: Escalating to desperate lane for '{Query}' after {DelayMs}ms of prior misses.",
                        query,
                        desperateDelayMs);
                    await Task.Delay(desperateDelayMs, cancellationToken);
                }

                bool foundInThisVariation = false;
                bool strictHighConfidenceWinnerFound = false;
                
                var hardenedVariation = _hardeningService.NormalizeSearchQuery(variation);
                if (hardenedVariation == null) continue; // Skip banned query
                
                await foreach (var track in StreamAndRankResultsAsync(
                    hardenedVariation, 
                    searchPlan.Target,
                    plannedLane.Lane,
                    preferredFormats, 
                    minBitrate, 
                    maxBitrate,
                    executionProfile,
                    cancellationToken))
                {
                    if (seenHashes.Add(track.UniqueHash))
                    {
                        yield return track;
                        totalFound++;
                        foundInThisVariation = true;

                        if (!isAlbumSearch && variationIndex == 0 && IsFastLaneWinner(track, formatFilter, minBitrate))
                        {
                            strictHighConfidenceWinnerFound = true;
                        }

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

                if (!isAlbumSearch && variationIndex == 0)
                {
                    if (_config.EnableStrictHighConfidenceShortCircuit && strictHighConfidenceWinnerFound)
                    {
                        _logger.LogInformation(
                            "Cascade Search: Strict variation produced a high-confidence winner. Skipping relaxed fallbacks.");
                        break;
                    }

                    if (totalFound >= strictSufficientResultCount)
                    {
                        _logger.LogInformation(
                            "Cascade Search: Strict variation reached sufficient result threshold ({Count}/{Threshold}). Skipping relaxed fallbacks.",
                            totalFound,
                            strictSufficientResultCount);
                        break;
                    }
                }

                // Smart Stop: If we found hits with a better strategy, don't fallback to noisier ones
                // Unless it's an album search where we want as much coverage as possible.
                if (foundInThisVariation && !isAlbumSearch && totalFound >= strictSufficientResultCount)
                {
                    _logger.LogInformation("Cascade Search: Found {Count} results for '{Variation}'. Stopping cascade.", totalFound, variation);
                    break;
                }

                if (!foundInThisVariation && variationIndex < variations.Count - 1)
                {
                    _logger.LogInformation("Cascade Search: No new results for '{Variation}'. Trying next variation...", variation);
                    var delayMs = Math.Max(50, _config.SearchThrottleDelayMs + executionProfile.AdditionalThrottleDelayMs);
                    await Task.Delay(delayMs, cancellationToken); // Stagger
                }
            }
        }
        finally
        {
            if (countedAsActive)
            {
                Interlocked.Decrement(ref _activeSearchCount);
            }
        }
    }

    private static bool IsFastLaneWinner(Track track, string[] formatFilter, int minBitrate)
    {
        if (track.IsFlagged)
            return false;

        var minimumScore = Math.Min(ScoringConstants.Availability.FastLaneMinMatchScore, 50);
        if (track.CurrentRank < minimumScore)
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

    private static bool IsLosslessFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return false;

        return LosslessFormats.Contains(format.Trim());
    }

    private async IAsyncEnumerable<Track> StreamAndRankResultsAsync(
        string normalizedQuery,
        TargetMetadata target,
        SearchQueryLane lane,
        string preferredFormats,
        int minBitrate,
        int maxBitrate,
        SearchExecutionProfile executionProfile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Brain buffer: desperate lanes get the full accumulator window.
        // Non-desperate lanes get the full SearchTimeout + overhead so the Soulseek network
        // reply window isn't cut off by the CTS before responses arrive.
        // Previous clamp (3–6 s) was shorter than the token-bucket refill (3.5 s/search),
        // causing the 2nd queued search to only get ~1.5 s of actual network time.
        var formatFilter = preferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var searchTimeoutSeconds = Math.Max(5, _config.SearchTimeout / 1000);
        var isLosslessOnlyIntent = formatFilter.Length > 0 &&
                                   formatFilter.Any(IsLosslessFormat) &&
                                   !formatFilter.Contains("mp3", StringComparer.OrdinalIgnoreCase);

        var brainBufferSeconds = lane == SearchQueryLane.Desperate
            ? Math.Clamp(_config.SearchAccumulatorWindowSeconds, 5, 30)
            : Math.Clamp(_config.MinSearchDurationSeconds, searchTimeoutSeconds + 4, 30);

        if (isLosslessOnlyIntent)
        {
            brainBufferSeconds = Math.Clamp(
                Math.Max(brainBufferSeconds, _config.MinLosslessSearchDurationSeconds),
                20,
                30);
        }

        var brainWinnerCount = lane == SearchQueryLane.Desperate
            ? Math.Max(5, _config.StrictSearchSufficientResultCount)
            : 5;

        var networkQuery = BuildNetworkQuery(normalizedQuery, formatFilter, minBitrate);
        var bufferedTracks = new List<Track>();
        using var brainBufferCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        brainBufferCts.CancelAfter(TimeSpan.FromSeconds(brainBufferSeconds));

        try
        {
            if (lane == SearchQueryLane.Desperate)
            {
                bufferedTracks = await CollectDesperateLaneCandidatesAsync(
                    normalizedQuery,
                    networkQuery,
                    target,
                    formatFilter,
                    minBitrate,
                    maxBitrate,
                    executionProfile,
                    brainBufferCts.Token);
            }
            else
            {
                await foreach (var track in _soulseek.StreamResultsAsync(
                    networkQuery,
                    formatFilter,
                    (minBitrate, maxBitrate),
                    DownloadMode.Normal,
                    executionProfile,
                    brainBufferCts.Token))
                {
                    _safetyFilter.EvaluateSafety(track, normalizedQuery);
                    bufferedTracks.Add(track);

                    if (IsPerfectAccumulatorWinner(track, target, formatFilter, minBitrate))
                    {
                        _logger.LogInformation(
                            "Search accumulator short-circuit: found ideal candidate for '{Query}' from {User}.",
                            normalizedQuery,
                            track.Username ?? "Unknown");
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (brainBufferCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Brain buffer window elapsed for {Lane} query '{Query}'. Ranking {Count} buffered candidates.",
                lane,
                normalizedQuery,
                bufferedTracks.Count);
        }

        if (bufferedTracks.Count == 0)
        {
            yield break;
        }

        var ranked = RankTrackResults(bufferedTracks, target, normalizedQuery, formatFilter, minBitrate, maxBitrate)
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

    private async Task<List<Track>> CollectDesperateLaneCandidatesAsync(
        string normalizedQuery,
        string networkQuery,
        TargetMetadata target,
        string[] formatFilter,
        int minBitrate,
        int maxBitrate,
        SearchExecutionProfile executionProfile,
        CancellationToken cancellationToken)
    {
        var capacity = Math.Max(32, executionProfile.EffectiveFileLimit);
        var channel = Channel.CreateBounded<Track>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        var producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await foreach (var track in _soulseek.StreamResultsAsync(
                    networkQuery,
                    formatFilter,
                    (minBitrate, maxBitrate),
                    DownloadMode.Normal,
                    executionProfile,
                    cancellationToken))
                {
                    _safetyFilter.EvaluateSafety(track, normalizedQuery);
                    await channel.Writer.WriteAsync(track, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                channel.Writer.TryComplete(failure);
            }
        }, cancellationToken);

        var bufferedTracks = new List<Track>();
        try
        {
            await foreach (var track in channel.Reader.ReadAllAsync(cancellationToken))
            {
                bufferedTracks.Add(track);

                if (IsPerfectAccumulatorWinner(track, target, formatFilter, minBitrate))
                {
                    _logger.LogInformation(
                        "Desperate lane short-circuit: found ideal candidate for '{Query}' from {User}.",
                        normalizedQuery,
                        track.Username ?? "Unknown");
                    break;
                }
            }
        }
        finally
        {
            try
            {
                await producer;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        return bufferedTracks;
    }

    private bool IsPerfectAccumulatorWinner(
        Track track,
        TargetMetadata target,
        string[] formatFilter,
        int minBitrate)
    {
        if (track.IsFlagged)
            return false;

        if (!IsFastLaneWinner(track, formatFilter, minBitrate))
            return false;

        var fitScore = CalculateAccumulatorFitScore(track, target, formatFilter, minBitrate);
        if (fitScore < 85)
            return false;

        return track.QueueLength == 0 || track.HasFreeUploadSlot;
    }

    private double CalculateAccumulatorFitScore(
        Track candidate,
        TargetMetadata target,
        string[] formatFilter,
        int minBitrate)
    {
        return SearchCandidateFitScorer.CalculateScore(
            candidate,
            target,
            formatFilter,
            minBitrate,
            _config.SearchLengthToleranceSeconds);
    }

    private static bool ContainsNormalizedToken(string? candidate, string expected)
    {
        return SearchCandidateFitScorer.ContainsNormalizedToken(candidate, expected);
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
        TargetMetadata target,
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
        var rankedResults = ResultSorter.OrderResults(results, searchTrack, evaluator)
            .Select(track =>
            {
                var fitScore = CalculateAccumulatorFitScore(track, target, formatFilter, minBitrate);
                var baseMatchScore = SearchCandidateRankingPolicy.MatchScoreFromRank(track.CurrentRank);
                var finalScore = SearchCandidateRankingPolicy.CalculateFinalScore(
                    baseMatchScore,
                    fitScore,
                    reliability: 0.5,
                    queueLength: track.QueueLength);
                EnsureBlendTelemetryMetadata(track, baseMatchScore, fitScore, 0.5, finalScore);
                var existingBreakdown = track.ScoreBreakdown;
                var blendBreakdown = $"Blend: Match={baseMatchScore:F1}, Fit={fitScore:F1}, Rel=0.50, Queue={track.QueueLength}, Final={finalScore:F1}";
                track.ScoreBreakdown = string.IsNullOrWhiteSpace(existingBreakdown)
                    ? blendBreakdown
                    : $"{existingBreakdown}; {blendBreakdown}";
                track.MatchReason ??= SearchBlendReasonFormatter.BuildCompactReason(track.Metadata);
                track.CurrentRank = finalScore;
                return track;
            })
            .OrderByDescending(track => track.CurrentRank)
            .ThenBy(track => track.QueueLength)
            .ThenByDescending(track => track.Bitrate)
            .ToList();
        
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
            BlendMatchScore = TryGetMetadataDouble(track, "BlendMatchScore"),
            BlendFitScore = TryGetMetadataDouble(track, "BlendFitScore"),
            BlendReliability = TryGetMetadataDouble(track, "BlendReliability"),
            BlendFinalScore = TryGetMetadataDouble(track, "BlendFinalScore"),
            ScoreBreakdown = track.ScoreBreakdown ?? string.Empty
        };
    }

    private static void EnsureBlendTelemetryMetadata(Track track, double matchScore, double fitScore, double reliability, double finalScore)
    {
        track.Metadata ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        track.Metadata["BlendMatchScore"] = matchScore;
        track.Metadata["BlendFitScore"] = fitScore;
        track.Metadata["BlendReliability"] = reliability;
        track.Metadata["BlendFinalScore"] = finalScore;
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

    private static double? TryGetMetadataDouble(Track track, string key)
    {
        if (track.Metadata == null ||
            !track.Metadata.TryGetValue(key, out var raw) ||
            raw is null)
        {
            return null;
        }

        return raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => null
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
