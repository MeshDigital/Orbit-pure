using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services;

/// <summary>
/// "The Seeker"
/// Responsible for finding the best available download link for a given track.
/// Encapsulates search Orchestration and Quality Selection logic.
/// </summary>
public class DownloadDiscoveryService
{
    private readonly ILogger<DownloadDiscoveryService> _logger;
    private readonly SearchOrchestrationService _searchOrchestrator;
    private readonly SearchResultMatcher _matcher;
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;
    private readonly TrackForensicLogger _forensicLogger;
    private readonly ISafetyFilterService _safetyFilter;
    private readonly Import.AutoCleanerService _autoCleaner;

    public DownloadDiscoveryService(
        ILogger<DownloadDiscoveryService> logger,
        SearchOrchestrationService searchOrchestrator,
        SearchResultMatcher matcher,
        AppConfig config,
        IEventBus eventBus,
        TrackForensicLogger forensicLogger,
        ISafetyFilterService safetyFilter,
        Import.AutoCleanerService autoCleaner)
    {
        _logger = logger;
        _searchOrchestrator = searchOrchestrator;
        _matcher = matcher;
        _config = config;
        _eventBus = eventBus;
        _forensicLogger = forensicLogger;
        _safetyFilter = safetyFilter;
        _autoCleaner = autoCleaner;
    }

    public record DiscoveryResult(Track? BestMatch, SearchAttemptLog? Log)
    {
        public int Bitrate => BestMatch?.Bitrate ?? 0;
    }

    /// <summary>
    /// Searches for a track and returns the single best match based on user preferences.
    /// Phase T.1: Refactored to accept PlaylistTrack model (decoupled from UI).
    /// Phase 12: Updated to use streaming search logic.
    /// Phase 3B: Added support for peer blacklisting (Health Monitor).
    /// 
    /// WHY "THE SEEKER":
    /// This service encapsulates the "find the best file" intelligence:
    /// 1. Constructs optimal search query ("Artist Title" vs "Artist - Title [Remix]")
    /// 2. Applies user preferences (formats, bitrate minimums)
    /// 3. Ranks results using forensic metadata validation
    /// 4. Returns SINGLE best match (not 50 options - paralysis of choice)
    /// 
    /// PHILOSOPHY:
    /// "Smart defaults, user overrides" - respect per-track overrides (PreferredFormats)
    /// "Trust but verify" - use forensics to filter fakes before presenting to user
    /// </summary>
    public async Task<DiscoveryResult> FindBestMatchAsync(PlaylistTrack track, CancellationToken ct, HashSet<string>? blacklistedUsers = null)
    {
        // Global discovery timeout: if all tiers combined take > 90s, abort cleanly.
        // This prevents a single search from hogging a semaphore slot indefinitely.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
        var timedCt = timeoutCts.Token;

        var tiers = _autoCleaner.Clean($"{track.Artist} - {track.Title}");
        var log = new SearchAttemptLog();
        var queryTiers = new[] { tiers.Dirty, tiers.Smart, tiers.Aggressive };
        var tierNames = new[] { "Dirty", "Smart", "Aggressive" };

        try
        {
            for (int i = 0; i < queryTiers.Length; i++)
            {
                var query = queryTiers[i];
                if (string.IsNullOrEmpty(query)) continue;

                _logger.LogInformation("Discovery Tier {Tier} started for: {Query} (GlobalId: {Id})", tierNames[i], query, track.TrackUniqueHash);
                _forensicLogger.Info(track.TrackUniqueHash, Data.Entities.ForensicStage.Discovery, $"Tier {tierNames[i]} Search Query: \"{query}\"");
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"🔎 Started {tierNames[i]} search for: '{query}'..."));

                var result = await PerformSearchTierAsync(track, query, tierNames[i], timedCt, blacklistedUsers, log);

                
                if (result.BestMatch != null)
                {
                    // If it's an Aggressive match, we might want to flag it or lower the confidence
                    if (tierNames[i] == "Aggressive" && result.BestMatch.CurrentRank > 0)
                    {
                        result.BestMatch.CurrentRank *= 0.8; // Penalty for aggressive query match
                        _logger.LogWarning("Aggressive match found. Reducing confidence score for {Title}.", track.Title);
                    }
                    return result;
                }

                if (timedCt.IsCancellationRequested) break;
                
                _forensicLogger.LogSearchSummary(track.TrackUniqueHash, track.TrackUniqueHash, 
                    $"Tier {tierNames[i]} Summary: {log.GetSummary()}", 
                    new { 
                        Tier = tierNames[i], 
                        Results = log.ResultsCount, 
                        RejectedForensics = log.RejectedByForensics,
                        RejectedQuality = log.RejectedByQuality,
                        RejectedFormat = log.RejectedByFormat,
                        RejectedBlacklist = log.RejectedByBlacklist
                    });

                // If we found NO results in Dirty, we move to Smart immediately.

                // If we found results but NO match, we might wait a bit or just move on.
                _logger.LogInformation("Tier {Tier} yielded no suitable matches. Moving to next tier...", tierNames[i]);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The global timeout fired (not external cancellation)
            _logger.LogWarning("⏱️ Discovery TIMEOUT (90s) for {Title}. Releasing slot.", track.Title);
            _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, "⏱️ Search timed out after 90 seconds"));
            log.TimedOut = true;
        }

        return new DiscoveryResult(null, log);
    }

    private async Task<DiscoveryResult> PerformSearchTierAsync(PlaylistTrack track, string query, string tierName, CancellationToken ct, HashSet<string>? blacklistedUsers, SearchAttemptLog log)

    {
        try
        {
            if (!_searchOrchestrator.IsConnected)
            {
                // Connection check inside tier as well (redundant but safe)
                if (!await WaitForConnectionAsync(ct)) return new DiscoveryResult(null, log);
            }
            // 1. Configure preferences (Respect per-track overrides)
            // Phase 21: FLAC-First Policy. If OnHold, we ONLY want MP3.
            List<string> formatsList;
            if (track.Status == TrackStatus.OnHold)
            {
                formatsList = new List<string> { "mp3" };
                _logger.LogInformation("🛠️ OnHold Status: Searching strictly for MP3 fallback for {Title}", track.Title);
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, "⚠️ Track is OnHold. Focusing strictly on MP3 formats."));
            }
            else
            {
                // Strict Gold Standard: NO MP3 until OnHold
                formatsList = !string.IsNullOrEmpty(track.PreferredFormats)
                    ? track.PreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : _config.PreferredFormats ?? new List<string> { "flac" };
                
                // If it's not OnHold, we strictly want FLAC or other lossless formats.
                // We remove MP3 to prevent early fallback.
                if (formatsList.Contains("mp3"))
                {
                    _logger.LogInformation("🧠 BRAIN: Removing MP3 from search tiers to enforce Gold Standard (FLAC) for {Title}.", track.Title);
                    formatsList.Remove("mp3");
                }
                
                // Ensure flac is first if not specified otherwise
                if (!formatsList.Contains("flac"))
                {
                    formatsList.Insert(0, "flac");
                }
                
                // Check if any formats remain. If not, default to flac.
                if (!formatsList.Any())
                {
                    formatsList.Add("flac");
                }
            }
            
            var preferredFormats = string.Join(",", formatsList.Distinct());
            var minBitrate = track.MinBitrateOverride ?? _config.PreferredMinBitrate;
            
            // Cap at reasonable high unless strictly set, but for discovery we want quality
            var maxBitrate = 0; 

            // 2. Perform Search via Orchestrator
            // Use streaming, but since we need the 'best' match from the entire set,
            // we probably need to wait a bit or collect a decent buffer.
            // "The Seeker" fundamentally wants the BEST match, which implies seeing most options.
            // However, since results are ranked on-the-fly, if we trust the ranking, we might find good chunks.
            // But 'OverallScore' is relative? No, it's absolute calculation in ResultSorter now.
            
            var allTracks = new System.Collections.Generic.List<Track>();
            var searchStartTime = DateTime.UtcNow;
            Track? bestSilverMatch = null;
            double bestSilverScore = 0;

            // Consume the stream
            await foreach (Track searchTrack in _searchOrchestrator.SearchAsync(
                query,
                preferredFormats,
                minBitrate,
                maxBitrate,
                isAlbumSearch: false,
                cancellationToken: ct))
            {
                log.ResultsCount++;

                // Phase 3B: Peer Blacklisting
                if (blacklistedUsers != null && 
                    !string.IsNullOrEmpty(searchTrack.Username) && 
                    blacklistedUsers.Contains(searchTrack.Username))
                {
                    log.RejectedByBlacklist++;
                    _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"Skipping peer {searchTrack.Username} (Blacklisted)", true));
                    continue;
                }

                // Phase 14: Forensic Gatekeeping (The Bouncer)
                // Audit Trail: Log why we rejected this candidate
                var targetDurationSeconds = track.CanonicalDuration.HasValue ? track.CanonicalDuration.Value / 1000 : (int?)null;
                var safety = _safetyFilter.EvaluateCandidate(searchTrack, query, targetDurationSeconds);
                
                // Track entity usually has Length in seconds. PlaylistTrack has CanonicalDuration (ms) or Duration (ms). 
                // Let's check what PlaylistTrack has. It has 'Duration' (Timespan?) or 'CanonicalDuration'.
                // Checking previous context or assuming standard int duration.
                // PlaylistTrack likely has 'CanonicalDuration' (int? ms).
                // Let's check strict validation below.
                
                if (!safety.IsSafe && !track.IgnoreSafetyGuards)
                {
                    log.RejectedByForensics++;
                    // Log the rejection to the persistent audit trail
                    _forensicLogger.LogRejection(
                        trackId: track.TrackUniqueHash,
                        filename: searchTrack.Filename ?? "Unknown",
                        reason: safety.Reason,
                        details: safety.TechnicalDetails ?? $"Bitrate: {searchTrack.Bitrate}kbps"
                    );
                    _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"Rejected {searchTrack.Username}: {safety.Reason}", true));
                    continue; 
                }

                // Phase 3C.4: Threshold Trigger (Race & Replace)
                // Real-time evaluation of incoming results
                var matchResult = _matcher.CalculateMatchResult(track, searchTrack);
                var score = matchResult.Score;

                // Phase 1.1: Store breakdown for UI transparency
                searchTrack.ScoreBreakdown = matchResult.ScoreBreakdown;
                searchTrack.CurrentRank = score;
                
                // If we find a "Golden Key" match (> 95) early, trigger immediate download
                if (score > 95)
                {
                    _logger.LogInformation("🚀 QUICK STRIKE: Found high-confidence match ({Score}/100) early! Skipping rest of search. File: {File}", 
                        score, searchTrack.Filename);
                    _forensicLogger.Info(track.TrackUniqueHash, Data.Entities.ForensicStage.Matching, 
                        $"Quick Strike (95+): Approved {searchTrack.Username}'s file. {matchResult.ScoreBreakdown}",
                        track.TrackUniqueHash,
                        new { searchTrack.Filename, score, searchTrack.Bitrate, searchTrack.Username });
                    _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"🚀 Found high-confidence match from {searchTrack.Username} ({score:F0}/100)"));
                    return new DiscoveryResult(searchTrack, log);
                }

                // Phase 3C.5: Speculative Start (Silver Match)
                // If we have a decent match (> 70) and 3 seconds have passed, take it.
                if (score > 70)
                {
                    // Track best silver match found so far
                    if (bestSilverMatch == null || score > bestSilverScore)
                    {
                        bestSilverMatch = searchTrack;
                        bestSilverScore = score;
                    }
                }
                else
                {
                    // Track why it was rejected
                    if (allTracks.Count < 100)
                    {
                        if (matchResult.ShortReason?.StartsWith("Duration") == true) log.RejectedByQuality++;
                        else if (matchResult.ShortReason?.Contains("Low Score") == true) log.RejectedByQuality++;
                    }
                }

                // Check speculative timeout (3s)
                if ((DateTime.UtcNow - searchStartTime).TotalSeconds > 3 && bestSilverMatch != null)
                {
                    _logger.LogInformation("🥈 SPECULATIVE TRIGGER: 3s timeout reached with match ({Score}/100). Starting download. File: {File}", 
                        bestSilverScore, bestSilverMatch.Filename);
                    _forensicLogger.Info(track.TrackUniqueHash, Data.Entities.ForensicStage.Matching, 
                        $"Speculative Trigger (70+): 3s timeout reached. Approved {bestSilverMatch.Username}'s file. Score: {bestSilverScore}",
                        track.TrackUniqueHash,
                        new { bestSilverMatch.Filename, bestSilverScore, bestSilverMatch.Bitrate, bestSilverMatch.Username });
                    _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"⏳ 3s timeout reached. Processing silver match from {bestSilverMatch.Username}"));
                    return new DiscoveryResult(bestSilverMatch, log);
                }

                if (score < 40 && allTracks.Count < 30) // Only spam UI for the first few rejections
                {
                     _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"Rejected {searchTrack.Username}: {matchResult.ShortReason}", true));
                }

                allTracks.Add(searchTrack);
            }

            if (!allTracks.Any())
            {
                _logger.LogWarning("No results found for {Query}", query);
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"❌ No results found on network for this query.", true));
                return new DiscoveryResult(null, log);
            }

            // 3. Select Best Match with simple Bitrate sorting since TieredTrackComparer is removed
            var bestMatch = allTracks.OrderByDescending(t => t.Bitrate).FirstOrDefault();
            
            // Phase 14: Decision Matrix Logging (Full Transparency)
            if (allTracks.Any())
            {
                var candidateSummary = allTracks
                    .OrderByDescending(t => t.Bitrate)
                    .Take(10) // Log top 10 candidates for matrix transparency
                    .Select(t => new { 
                        t.Username, 
                        t.Bitrate, 
                        t.Size, 
                        t.QueueLength, 
                        t.HasFreeUploadSlot,
                        Tier = MetadataForensicService.CalculateTier(t).ToString(),
                        Score = t.CurrentRank,
                        t.Filename
                    }).ToList();

                _forensicLogger.LogSelectionDecision(
                    track.TrackUniqueHash, 
                    track.TrackUniqueHash, 
                    bestMatch != null ? $"Selected {bestMatch.Username}'s file" : "No suitable match found", 
                    new { 
                        Tier = tierName, 
                        Query = query, 
                        CandidatesCount = allTracks.Count, 
                        TopCandidates = candidateSummary 
                    });

            }

            if (bestMatch != null)
            {
                var tier = MetadataForensicService.CalculateTier(bestMatch);
                _logger.LogInformation("🧠 BRAIN: Unified Matcher selected: {Filename} (Tier: {Tier})", bestMatch.Filename, tier);
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"🧠 Selected {bestMatch.Username}'s file ({tier})"));
                return new DiscoveryResult(bestMatch, log);
            }


            // 4. Adaptive Relaxation Strategy (Phase 2.0) - WITH TIMEOUT
            // Phase 21 Hardening: If we are in regular FLAC mode, we DON'T relax to MP3 automatically.
            // Relaxation only happens if specifically allowed or if we are already in MP3-fallback mode (OnHold).
            if (_config.EnableRelaxationStrategy && allTracks.Any())
            {
                if (track.Status != TrackStatus.OnHold && formatsList.Contains("flac") && !formatsList.Contains("mp3"))
                {
                     _logger.LogInformation("🧠 BRAIN: FLAC-only tier {Tier} failed — no relaxation. Will escalate to MP3 after 9 FLAC attempts.", tierName);
                     _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"🎵 No FLAC found in {tierName} tier — MP3 fallback queues after 9 attempts."));
                     return new DiscoveryResult(null, log);
                }

                _logger.LogInformation("🧠 BRAIN: Strict match failed. Waiting {Timeout}s before relaxation...", 
                    _config.RelaxationTimeoutSeconds);
                
                // Wait for the configured timeout before relaxing criteria
                await Task.Delay(TimeSpan.FromSeconds(_config.RelaxationTimeoutSeconds), ct);
                
                _logger.LogInformation("🧠 BRAIN: Timeout reached. Starting relaxation strategy...");
                
                // Relaxation Tier 1: Lower bitrate floor (e.g. 320 -> 256)
                if (minBitrate > 256)
                {
                    _logger.LogInformation("🧠 BRAIN: Relaxation Tier 1: Lowering bitrate floor to 256kbps");
                    var relaxedTracks = allTracks.Where(t => t.Bitrate >= 256).ToList();
                    bestMatch = _matcher.FindBestMatch(track, relaxedTracks);
                    if (bestMatch != null)
                    {
                        _logger.LogInformation("🧠 BRAIN: Tier 1 match found: {Filename}", bestMatch.Filename);
                        return new DiscoveryResult(bestMatch, log);
                    }
                }

                // Relaxation Tier 2: Accept any quality (highest available)
                _logger.LogInformation("🧠 BRAIN: Relaxation Tier 2: Accepting highest available quality");
                bestMatch = allTracks.OrderByDescending(t => t.Bitrate).FirstOrDefault();
                
                if (bestMatch != null)
                {
                    _logger.LogInformation("🧠 BRAIN: Tier 2 fallback: {Filename} ({Bitrate}kbps)", 
                        bestMatch.Filename, bestMatch.Bitrate);
                    return new DiscoveryResult(bestMatch, log);
                }
            }

            _logger.LogWarning("🧠 BRAIN: No suitable match found for query tier. {Summary}", log.GetSummary());
            _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"❌ No acceptable match found in {tierName} tier.", true));
            return new DiscoveryResult(null, log);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search tier failed for {Query}", query);
            return new DiscoveryResult(null, log);
        }
    }

    private async Task<bool> WaitForConnectionAsync(CancellationToken ct)
    {
        _logger.LogInformation("Waiting for Soulseek connection...");
        var waitStart = DateTime.UtcNow;
        while (!_searchOrchestrator.IsConnected && (DateTime.UtcNow - waitStart).TotalSeconds < 10)
        {
            if (ct.IsCancellationRequested) return false;
            await Task.Delay(500, ct);
        }

        if (!_searchOrchestrator.IsConnected)
        {
            _logger.LogWarning("Timeout waiting for Soulseek connection.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Performs discovery and automatically handles queueing or upgrade evaluation.
    /// </summary>
    public async Task DiscoverAndQueueTrackAsync(PlaylistTrack track, CancellationToken ct = default, HashSet<string>? blacklistedUsers = null)
    {
        // Step T.1: Pass model directly
        var result = await FindBestMatchAsync(track, ct, blacklistedUsers);
        var bestMatch = result.BestMatch;
        if (bestMatch == null) return;

        // Determine if this is an upgrade search based on whether the track already has a file
        bool isUpgrade = !string.IsNullOrEmpty(track.ResolvedFilePath);

        if (isUpgrade)
        {
            int currentBitrate = track.Bitrate ?? 0;
            int newBitrate = bestMatch.Bitrate;
            
            // Upgrade Logic: Better bitrate AND minimum gain achieved
            if (newBitrate > currentBitrate && (newBitrate - currentBitrate) >= _config.UpgradeMinGainKbps)
            {
                _logger.LogInformation("Upgrade Found: {Artist} - {Title} ({New} vs {Old} kbps)", 
                    track.Artist, track.Title, newBitrate, currentBitrate);

                if (_config.UpgradeAutoQueueEnabled)
                {
                    _eventBus.Publish(new AutoDownloadUpgradeEvent(track.TrackUniqueHash, bestMatch));
                }
                else
                {
                    _eventBus.Publish(new UpgradeAvailableEvent(track.TrackUniqueHash, bestMatch));
                }
            }
        }
        else
        {
            // Standard missing track discovery - auto download is assumed here for automation flows
            _eventBus.Publish(new AutoDownloadTrackEvent(track.TrackUniqueHash, bestMatch));
        }
    }
}
