using System;
using System.Collections.Generic;
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
    private readonly ISafetyFilterService _safetyFilter;
    private readonly Import.AutoCleanerService _autoCleaner;
    private readonly Network.ProtocolHardeningService _hardeningService;
    private readonly PeerReliabilityService _peerReliability;

    public DownloadDiscoveryService(
        ILogger<DownloadDiscoveryService> logger,
        SearchOrchestrationService searchOrchestrator,
        SearchResultMatcher matcher,
        AppConfig config,
        IEventBus eventBus,
        ISafetyFilterService safetyFilter,
        Import.AutoCleanerService autoCleaner,
        Network.ProtocolHardeningService hardeningService,
        PeerReliabilityService peerReliability)
    {
        _logger = logger;
        _searchOrchestrator = searchOrchestrator;
        _matcher = matcher;
        _config = config;
        _eventBus = eventBus;
        _safetyFilter = safetyFilter;
        _autoCleaner = autoCleaner;
        _hardeningService = hardeningService;
        _peerReliability = peerReliability;
    }

    public record DiscoveryResult(Track? BestMatch, SearchAttemptLog? Log, Track? RunnerUpMatch = null)
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120)); // Bumped for fallback support
        var timedCt = timeoutCts.Token;

        var tiers = _autoCleaner.Clean($"{track.Artist} - {track.Title}");
        var log = new SearchAttemptLog();
        
        // Phase 3D: Integrated Fallback - try lossless tiers first, then a single MP3 fallback if needed
        var queryTiers = new[] { tiers.Dirty, tiers.Smart, tiers.Aggressive };
        var tierNames = new[] { "Dirty", "Smart", "Aggressive" };

        try
        {
            // Pass 1: Gold Standard (Lossless)
            for (int i = 0; i < queryTiers.Length; i++)
            {
                var query = queryTiers[i];
                if (string.IsNullOrEmpty(query)) continue;

                // Phase 26: Protocol Hardening - Double Sanitization
                var hardenedQuery = _hardeningService.NormalizeSearchQuery(query);
                if (hardenedQuery == null) continue;

                if (!string.Equals(hardenedQuery, query, StringComparison.Ordinal))
                {
                    track.SourceProvenance = "ShieldSanitized";
                }

                // Hyper-Drive: Hedged Search
                // Run first FLAC lane and a delayed MP3 hedge in parallel.
                // Winner is whichever produces an acceptable match first.
                if (i == 0 && _config.EnableHedgedSearch && track.Status != TrackStatus.OnHold)
                {
                    var flacLog = new SearchAttemptLog();
                    var hedgeLog = new SearchAttemptLog();

                    using var hedgeCts = CancellationTokenSource.CreateLinkedTokenSource(timedCt);
                    var flacTask = PerformSearchTierAsync(track, hardenedQuery, tierNames[i], timedCt, blacklistedUsers, flacLog, forceMp3: false);
                    var hedgeTask = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, _config.HedgedSearchDelaySeconds)), hedgeCts.Token);
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, "⚡ Hedge activated: launching MP3 lane in parallel."));
                            return await PerformSearchTierAsync(track, tiers.Smart, "MP3-Hedge", hedgeCts.Token, blacklistedUsers, hedgeLog, forceMp3: true);
                        }
                        catch (OperationCanceledException)
                        {
                            return new DiscoveryResult(null, hedgeLog);
                        }
                    }, hedgeCts.Token);

                    var firstCompleted = await Task.WhenAny(flacTask, hedgeTask);
                    var firstResult = await firstCompleted;

                    if (firstCompleted == flacTask)
                        MergeAttemptLog(log, flacLog);
                    else
                        MergeAttemptLog(log, hedgeLog);

                    if (firstResult.BestMatch != null)
                    {
                        hedgeCts.Cancel();
                        return firstResult;
                    }

                    var secondTask = firstCompleted == flacTask ? hedgeTask : flacTask;
                    var secondResult = await secondTask;
                    if (firstCompleted == flacTask)
                        MergeAttemptLog(log, hedgeLog);
                    else
                        MergeAttemptLog(log, flacLog);

                    if (secondResult.BestMatch != null)
                    {
                        return secondResult;
                    }

                    if (timedCt.IsCancellationRequested) break;
                    continue;
                }

                _logger.LogInformation("Discovery Tier {Tier} (Lossless) for: {Query}", tierNames[i], hardenedQuery);
                var result = await PerformSearchTierAsync(track, hardenedQuery, tierNames[i], timedCt, blacklistedUsers, log, forceMp3: false);

                if (result.BestMatch != null) return result;
                if (timedCt.IsCancellationRequested) break;
                
                // PERFORMANCE Optimization: If FIRST tier (Dirty) finds absolutely ZERO results, 
                // and it's a very specific query, we might want to skip directly to MP3 if configured.
                // But for "Pure", we'll stick to the plan: pivot after lossless fails.
            }

            // Phase 3D: High-Efficiency Fallback
            // If we found NOTHING in FLAC and we are not already strictly searching for MP3 (OnHold),
            // perform one last "Safety Tier" with MP3 within the same discovery session.
            if (track.Status != TrackStatus.OnHold && !timedCt.IsCancellationRequested)
            {
                _logger.LogInformation("🥈 Lossless discovery yielded no matches. Triggering integrated MP3 Fallback Pass for: {Title}", track.Title);
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, "🥈 Lossless tiers failed. Trying MP3 fallback..."));
                
                var fallbackResult = await PerformSearchTierAsync(track, tiers.Smart, "MP3-Fallback", timedCt, blacklistedUsers, log, forceMp3: true);
                if (fallbackResult.BestMatch != null)
                {
                    _logger.LogInformation("✅ MP3 Fallback SUCCESS for {Title}.", track.Title);
                    return fallbackResult;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("⏱️ Discovery TIMEOUT for {Title}.", track.Title);
            _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, "⏱️ Search timed out."));
            log.TimedOut = true;
        }

        return new DiscoveryResult(null, log);
    }

    private async Task<DiscoveryResult> PerformSearchTierAsync(PlaylistTrack track, string query, string tierName, CancellationToken ct, HashSet<string>? blacklistedUsers, SearchAttemptLog log, bool forceMp3 = false)
    {
        try
        {
            if (!_searchOrchestrator.IsConnected)
            {
                // Connection check inside tier as well (redundant but safe)
                if (!await WaitForConnectionAsync(ct)) return new DiscoveryResult(null, log);
            }
            // 1. Configure preferences (Respect per-track overrides)
            // Phase 21: FLAC-First Policy. If OnHold OR forceMp3, we ONLY want MP3.
            List<string> formatsList;
            if (track.Status == TrackStatus.OnHold || forceMp3)
            {
                formatsList = new List<string> { "mp3" };
                _logger.LogInformation("🛠️ MP3 Mode: Searching strictly for MP3 fallback for {Title} (Reason: {Reason})", 
                    track.Title, forceMp3 ? "Integrated Fallback" : "OnHold Status");
            }
            else
            {
                // Strict Gold Standard: Lossless only
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
            
            // Workstation 2026: enforce network-side quality gate for lossless lanes.
            // This pushes filtering upstream to Soulseek and reduces local scoring overhead.
            if (!forceMp3 && formatsList.Contains("flac", StringComparer.OrdinalIgnoreCase))
            {
                minBitrate = Math.Max(minBitrate, 500);
            }
            
            // Cap at reasonable high unless strictly set, but for discovery we want quality
            var maxBitrate = 0; 

            // 2. Perform Search via Orchestrator
            // Use streaming, but since we need the 'best' match from the entire set,
            // we probably need to wait a bit or collect a decent buffer.
            // "The Seeker" fundamentally wants the BEST match, which implies seeing most options.
            // However, since results are ranked on-the-fly, if we trust the ranking, we might find good chunks.
            // But 'OverallScore' is relative? No, it's absolute calculation in ResultSorter now.
            
            var allTracks = new List<Track>();
            var searchStartTime = DateTime.UtcNow;
            Track? bestSilverMatch = null;
            double bestSilverScore = 0;
            Track? runnerUpSilverMatch = null;
            double runnerUpSilverScore = 0;
            var pendingCandidates = new List<Track>(8);
            var minSearchDurationSeconds = Math.Clamp(_config.MinSearchDurationSeconds, 3, 5);

            void UpdateTopSilverCandidates(Track candidate, double score)
            {
                if (bestSilverMatch == null || score > bestSilverScore)
                {
                    runnerUpSilverMatch = bestSilverMatch;
                    runnerUpSilverScore = bestSilverScore;
                    bestSilverMatch = candidate;
                    bestSilverScore = score;
                    return;
                }

                if (runnerUpSilverMatch == null || score > runnerUpSilverScore)
                {
                    runnerUpSilverMatch = candidate;
                    runnerUpSilverScore = score;
                }
            }

            async Task<DiscoveryResult?> EvaluatePendingCandidatesAsync()
            {
                if (!pendingCandidates.Any())
                {
                    return null;
                }

                var batch = pendingCandidates.ToList();
                pendingCandidates.Clear();

                var scoredBatch = await Task.WhenAll(batch.Select(candidate =>
                    Task.Run(() =>
                    {
                        var localResult = _matcher.CalculateMatchResult(track, candidate);
                        return (Candidate: candidate, Result: localResult, Score: localResult.Score);
                    }, ct)));

                foreach (var scored in scoredBatch.OrderByDescending(x => x.Score))
                {
                    var searchTrack = scored.Candidate;
                    var matchResult = scored.Result;
                    var reliability = _peerReliability.GetReliabilityScore(searchTrack.Username);
                    var reliabilityBonus = (reliability - 0.5) * 10.0;
                    var score = scored.Score + reliabilityBonus;

                    searchTrack.ScoreBreakdown = matchResult.ScoreBreakdown;
                    searchTrack.CurrentRank = score;

                    var isGoldenCriteria = !forceMp3 &&
                                           string.Equals(searchTrack.Format, "flac", StringComparison.OrdinalIgnoreCase) &&
                                           searchTrack.Bitrate >= 500 &&
                                           score >= 85;

                    // Workstation 2026: first-past-the-post quality gate.
                    // As soon as a verified FLAC 500kbps+ candidate appears, we stop this lane early.
                    if (isGoldenCriteria)
                    {
                        _logger.LogInformation("🏁 GOLDEN CRITERIA hit ({Score}/100): {File} [{Bitrate}kbps {Format}] - ending tier early.",
                            score, searchTrack.Filename, searchTrack.Bitrate, searchTrack.Format);

                        _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash,
                            $"🏁 Golden match: {searchTrack.Username} ({searchTrack.Bitrate}kbps FLAC)."));

                        return new DiscoveryResult(searchTrack, log, runnerUpSilverMatch);
                    }

                    if (score > 95)
                    {
                        _logger.LogInformation("🚀 QUICK STRIKE: Found high-confidence match ({Score}/100) early! Skipping rest of search. File: {File}",
                            score, searchTrack.Filename);

                        _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"🚀 Found high-confidence match from {searchTrack.Username} ({score:F0}/100)"));
                        return new DiscoveryResult(searchTrack, log, runnerUpSilverMatch);
                    }

                    if (score > 70)
                    {
                        UpdateTopSilverCandidates(searchTrack, score);
                    }
                    else
                    {
                        if (allTracks.Count < 100)
                        {
                            if (matchResult.ShortReason?.StartsWith("Duration") == true) log.RejectedByQuality++;
                            else if (matchResult.ShortReason?.Contains("Low Score") == true) log.RejectedByQuality++;
                        }
                    }

                    if (score < 40 && allTracks.Count < 30)
                    {
                        _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"Rejected {searchTrack.Username}: {matchResult.ShortReason}", true));
                    }

                    allTracks.Add(searchTrack);
                }

                return null;
            }

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
                    // Phase 6: Security audit trail
                    _eventBus.Publish(new SecurityAuditEvent(
                        Category: SecurityAuditCategory.Blacklist,
                        Severity: SecurityAuditSeverity.Block,
                        Summary: $"Blocked peer: {searchTrack.Username}",
                        Detail: $"Peer is on the blacklist. File: {searchTrack.Filename}",
                        AssociatedHash: track.TrackUniqueHash));
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
                    _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"Rejected {searchTrack.Username}: {safety.Reason}", true));
                    // Phase 6: Security audit trail
                    _eventBus.Publish(new SecurityAuditEvent(
                        Category: SecurityAuditCategory.Gate,
                        Severity: SecurityAuditSeverity.Block,
                        Summary: $"Gate blocked: {safety.Reason}",
                        Detail: $"Peer: {searchTrack.Username} | {safety.TechnicalDetails ?? $"Bitrate: {searchTrack.Bitrate}kbps"}",
                        AssociatedHash: track.TrackUniqueHash));
                    continue; 
                }

                if (!forceMp3 && !track.IgnoreSafetyGuards && (searchTrack.Format == "flac" && searchTrack.Bitrate < 400))
                {
                    log.RejectedByForensics++;
                    var suspiciousReason = "Suspicious FLAC transcode detected (Low bitrate).";
                    _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"Skipped {searchTrack.Username}: {suspiciousReason}", true));
                    // Phase 6: Security audit trail
                    _eventBus.Publish(new SecurityAuditEvent(
                        Category: SecurityAuditCategory.ForensicLab,
                        Severity: SecurityAuditSeverity.Block,
                        Summary: $"Fake FLAC blocked: {suspiciousReason}",
                        Detail: $"Peer: {searchTrack.Username} | {searchTrack.Bitrate}kbps | {searchTrack.Filename}",
                        AssociatedHash: track.TrackUniqueHash));
                    continue;
                }

                _peerReliability.RecordSearchCandidate(searchTrack.Username);

                pendingCandidates.Add(searchTrack);

                // Aggressive Bulk Matching: score candidates in parallel by batch.
                if (pendingCandidates.Count >= 8)
                {
                    var quickResult = await EvaluatePendingCandidatesAsync();
                    if (quickResult != null) return quickResult;
                }

                // Check speculative timeout (configured min search duration)
                if ((DateTime.UtcNow - searchStartTime).TotalSeconds > minSearchDurationSeconds)
                {
                    var flushResult = await EvaluatePendingCandidatesAsync();
                    if (flushResult != null) return flushResult;

                    if (bestSilverMatch != null)
                    {
                        _logger.LogInformation("🥈 SPECULATIVE TRIGGER: 3s timeout reached with match ({Score}/100). Starting download. File: {File}",
                            bestSilverScore, bestSilverMatch.Filename);

                        _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"⏳ 3s timeout reached. Processing silver match from {bestSilverMatch.Username}"));
                        return new DiscoveryResult(bestSilverMatch, log, runnerUpSilverMatch);
                    }
                }
            }

            var finalBatchResult = await EvaluatePendingCandidatesAsync();
            if (finalBatchResult != null) return finalBatchResult;

            if (!allTracks.Any())
            {
                _logger.LogWarning("No results found for {Query}", query);
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"❌ No results found on network for this query.", true));
                return new DiscoveryResult(null, log);
            }

            // 3. Select Best Match with simple Bitrate sorting since TieredTrackComparer is removed
            var rankedCandidates = allTracks
                .OrderByDescending(t => t.CurrentRank)
                .ThenByDescending(t => t.Bitrate)
                .ToList();

            var bestMatch = rankedCandidates.FirstOrDefault();
            var runnerUpMatch = rankedCandidates.Skip(1).FirstOrDefault();
            
            // Phase 14: Decision Matrix Logging (Full Transparency)
            if (allTracks.Any())
            {
                _logger.LogInformation("🧠 BRAIN: Matcher considered {Count} candidates. Query: {Query}", allTracks.Count, query);
            }

            if (bestMatch != null)
            {
                _logger.LogInformation("🧠 BRAIN: Unified Matcher selected: {Filename}", bestMatch.Filename);
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(track.TrackUniqueHash, $"🧠 Selected {bestMatch.Username}'s file"));
                return new DiscoveryResult(bestMatch, log, runnerUpMatch);
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

    private static void MergeAttemptLog(SearchAttemptLog target, SearchAttemptLog source)
    {
        target.ResultsCount += source.ResultsCount;
        target.RejectedByQuality += source.RejectedByQuality;
        target.RejectedByFormat += source.RejectedByFormat;
        target.RejectedByBlacklist += source.RejectedByBlacklist;
        target.RejectedByForensics += source.RejectedByForensics;
        target.TimedOut = target.TimedOut || source.TimedOut;

        if (source.Top3RejectedResults.Any())
        {
            target.Top3RejectedResults.AddRange(source.Top3RejectedResults);
            target.Top3RejectedResults = target.Top3RejectedResults
                .OrderByDescending(x => x.SearchScore)
                .Take(3)
                .ToList();
        }
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
