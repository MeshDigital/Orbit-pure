using System;
using SLSKDONET.Views;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json; // Phase 2A: Checkpoint serialization
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Utils;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Downloads;
using SLSKDONET.Data.Essentia;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Services.Repositories; // [NEW] Namespace
using SLSKDONET.Services.IO; // Added explicit using
using SLSKDONET.Events;



namespace SLSKDONET.Services;

/// <summary>
/// Queue processing loop, multi-lane priority allocation, discovery dispatch, and per-track orchestration (#307).
/// </summary>
public partial class DownloadManager
{
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        // Recreate the CTS if it was previously cancelled by StopAsync
        if (_globalCts.IsCancellationRequested)
        {
            _globalCts.Dispose();
            _globalCts = new CancellationTokenSource();
        }

        _logger.LogInformation("DownloadManager Orchestrator started.");


        await InitAsync();

        // Phase 13: Non-blocking Journal Recovery
        // We run this in background to avoid blocking the UI/Splash Screen
        // while it reconciles potentially thousands of checks.
        // Run recovery in background
        _ = Task.Run(async () =>
        {
            try
            {
                await HydrateFromCrashAsync();

                // Phase 2.5: Zombie Cleanup - Delete orphaned .part files older than 24 hours
                // Moved from InitAsync to prevent blocking Engine startup over slow directories
                var activePartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lock (_collectionLock)
                {
                    foreach (var ctx in _downloads.Where(t => t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Downloading))
                    {
                        // Protect the REAL .part files of live downloads (#328): the tag-built path never existed,
                        // so an active partial file older than 24 h could be swept as an orphan.
                        foreach (var partPath in Downloads.PartFileGuard.CandidatePartPaths(ctx.PartFilePath, ctx.Model.ResolvedFilePath, StagingPartPathFor(ctx)))
                            activePartPaths.Add(partPath);
                    }
                }
                await _pathProvider.CleanupOrphanedPartFilesAsync(activePartPaths);

                // Startup Orphan Sweep: catch tracks the crash journal didn't handle
                // (e.g., tracks stuck in Searching/Downloading without journal entries)
                List<DownloadContext> orphans;
                lock (_collectionLock)
                {
                    orphans = _downloads.Where(d =>
                        d.State == PlaylistTrackState.Searching ||
                        d.State == PlaylistTrackState.Downloading ||
                        d.State == PlaylistTrackState.Stalled).ToList();
                }

                if (orphans.Any())
                {
                    _logger.LogWarning("🧹 Orphan Sweep: Found {Count} tracks stuck in active states from previous session.", orphans.Count);
                    foreach (var orphan in orphans)
                    {
                        await UpdateStateAsync(orphan, PlaylistTrackState.Failed,
                            DownloadFailureReason.Interrupted);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover journaled downloads or cleanup orphaned files");
            }
        }, ct);

        _processingTask = Task.Run(() => ProcessQueueLoop(_globalCts.Token), _globalCts.Token);
        if (_laneAutotuneTask == null || _laneAutotuneTask.IsCompleted)
        {
            _laneAutotuneTask = Task.Run(() => LaneAutotuneLoop(_globalCts.Token), _globalCts.Token);
        }
        OnPropertyChanged(nameof(IsRunning));

        await Task.CompletedTask;
    }



    /// <summary>
    /// Windows-level network availability changed (e.g. Wi-Fi reconnected). Only wakes the circuit
    /// breaker's backoff wait on a transition TO available — an unavailable transition doesn't need
    /// to do anything since the breaker is already backing off in that case.
    /// </summary>
    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;

        _logger.LogInformation("🌐 Windows reports network availability restored — waking circuit breaker early.");
        if (_networkAvailabilitySignal.CurrentCount == 0)
        {
            _networkAvailabilitySignal.Release();
        }
    }

    private async Task ProcessQueueLoop(CancellationToken token)
    {
        int disconnectBackoff = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Global Engine Pause
                if (_isPaused)
                {
                    await Task.Delay(1000, token);
                    continue;
                }
                // Phase 0.9: Circuit Breaker
                if (!_soulseek.IsLoggedIn)
                {
                    // Distinguishes "Windows/Wi-Fi has no route at all" from "the OS network is
                    // fine but Soulseek's own login/protocol failed" — these need different
                    // messaging (and, via the wait below, different urgency) rather than always
                    // implying a Soulseek-specific fault.
                    bool osNetworkAvailable = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

                    if (disconnectBackoff == 0)
                    {
                        _logger.LogWarning("🔌 Circuit Breaker: Queue processing PAUSED due to disconnection. OS network available={OsNetworkAvailable}", osNetworkAvailable);
                        _eventBus.Publish(new GlobalStatusEvent(
                            osNetworkAvailable ? "Disconnected: Waiting for Soulseek..." : "No network connection — waiting for Wi-Fi/network...",
                            true, true));

                        // Transition active downloads and in-flight searches to waiting state
                        lock (_collectionLock)
                        {
                            var activeDownloading = _downloads.Where(d =>
                                d.State == PlaylistTrackState.Downloading ||
                                d.State == PlaylistTrackState.Searching).ToList();
                            foreach (var d in activeDownloading)
                            {
                                _ = UpdateStateAsync(d, PlaylistTrackState.WaitingForConnection);
                            }
                        }
                    }

                    // Exponential Backoff: 2, 4, 8, 16... max 60s
                    int delaySeconds = Math.Min(60, (int)Math.Pow(2, Math.Min(6, disconnectBackoff + 1)));
                    disconnectBackoff++;
                    CurrentBackoffSeconds = delaySeconds;
                    OnPropertyChanged(nameof(CurrentBackoffSeconds));
                    OnPropertyChanged(nameof(IsBackingOff));

                    if (disconnectBackoff % 5 == 0) // Log occasionally
                    {
                        _logger.LogInformation("Circuit Breaker: Waiting for connection... (Next check in {Seconds}s)", delaySeconds);
                        _eventBus.Publish(new GlobalStatusEvent(
                            // No countdown here (#330): this is the queue's poll interval, not the reconnect timer
                            // (ConnectionLifecycleService owns that), so a number would contradict the real one.
                            osNetworkAvailable ? "Disconnected: waiting for Soulseek to reconnect…" : "No network connection — waiting for the network…",
                            true, true));
                    }

                    // Race the backoff delay against Windows reporting the network came back, so a
                    // Wi-Fi drop-and-reconnect doesn't sit out up to 60s of a stale backoff window
                    // after connectivity is already restored.
                    using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var delayTask = Task.Delay(delaySeconds * 1000, delayCts.Token);
                        var networkRestoredTask = _networkAvailabilitySignal.WaitAsync(delayCts.Token);
                        try
                        {
                            var completed = await Task.WhenAny(delayTask, networkRestoredTask);
                            if (completed == networkRestoredTask)
                            {
                                _logger.LogInformation("🌐 Network came back early — re-checking Soulseek connection instead of waiting out the remaining {Seconds}s backoff.", delaySeconds);
                            }
                        }
                        finally
                        {
                            delayCts.Cancel();
                        }
                    }
                    continue;
                }

                // Reset backoff if connected
                if (disconnectBackoff > 0)
                {
                    _logger.LogInformation("✅ Circuit Breaker: Connection restored! Resuming queue processing.");
                    _eventBus.Publish(new GlobalStatusEvent("Connection Restored", false));
                    disconnectBackoff = 0;
                    CurrentBackoffSeconds = 0;
                    OnPropertyChanged(nameof(CurrentBackoffSeconds));
                    OnPropertyChanged(nameof(IsBackingOff));

                    // Transition waiting downloads back to pending
                    lock (_collectionLock)
                    {
                        var waiting = _downloads.Where(d => d.State == PlaylistTrackState.WaitingForConnection).ToList();
                        foreach (var d in waiting)
                        {
                            _ = UpdateStateAsync(d, PlaylistTrackState.Pending);
                        }
                    }
                }

                DownloadContext? nextContext = null;
                var needsRefill = false;
                lock (_collectionLock)
                {
                    // Phase 3D: High-Efficiency Core - Single Pass Selection
                    // Collect eligible tracks and current lane occupancy in one loop to minimize lock time
                    var eligibleTracks = new List<DownloadContext>();
                    var activeByPriority = new Dictionary<int, int>();

                    foreach (var ctx in _downloads)
                    {
                        if (ctx.State == PlaylistTrackState.Searching || ctx.State == PlaylistTrackState.Downloading)
                        {
                            activeByPriority[ctx.Model.Priority] = activeByPriority.GetValueOrDefault(ctx.Model.Priority) + 1;
                        }
                        else if (ctx.State == PlaylistTrackState.Pending &&
                            (!ctx.NextRetryTime.HasValue || ctx.NextRetryTime.Value <= DateTime.UtcNow) &&
                            (!ctx.RetryAfterSlot.HasValue || ctx.RetryAfterSlot.Value <= _schedulerSlot))
                        {
                            eligibleTracks.Add(ctx);
                        }
                    }

                    if (eligibleTracks.Any())
                    {
                        // Priority 0 tracks should always be evaluated first
                        var sortedEligible = eligibleTracks.OrderBy(t => t.Model.Priority).ToList();
                        nextContext = SelectNextTrackWithLaneAllocation(sortedEligible, activeByPriority);
                    }

                    // Periodic trim: remove terminal downloads older than 30 min to prevent unbounded growth
                    var cutoff = DateTime.UtcNow.AddMinutes(-30);
                    _downloads.RemoveAll(d =>
                        d.State is PlaylistTrackState.Completed or PlaylistTrackState.Failed or PlaylistTrackState.Cancelled
                        && d.Model.CompletedAt.HasValue
                        && d.Model.CompletedAt.Value < cutoff);

                    // Phase 3C.5: Check if we need to release the hounds (Refill)
                    needsRefill = _downloads.Count(d => d.State == PlaylistTrackState.Pending) < REFILL_THRESHOLD;

                    // Pre-search is intentionally disabled in strict-concurrency mode.
                }

                if (needsRefill)
                {
                    // Trigger background refill outside _collectionLock (#316)
                    _ = Task.Run(async () =>
                    {
                        try { await RefillQueueAsync(); }
                        catch (Exception ex) { _logger.LogError(ex, "Background queue refill failed"); }
                    });
                }

                if (nextContext == null)
                {
                    // No tracks ready or all lanes saturated (Except VIPs which are handled proactively)
                    await Task.Delay(500, token);
                    continue;
                }

                _logger.LogDebug("Spinning up search/download for {Title} (Priority: {Prio}, VIP: {Vip})",
                    nextContext.Model.Title, nextContext.Model.Priority, nextContext.IsVip);

                bool tookSemaphoreSlot = false;
                // STRICT CONCURRENCY: every track lifecycle (search -> match -> download) consumes exactly one worker slot.
                // No VIP bypass here; slider value is authoritative for "no more, no less" active tracks.
                if (!_downloadSemaphore.Wait(0))
                {
                    await Task.Delay(200, token);
                    continue;
                }
                tookSemaphoreSlot = true;

                OnPropertyChanged(nameof(ActiveWorkerSlots));

                // Phase 3C Hardening: Race Condition Check
                // After waiting, the world may have changed (e.g., lane filled by stealth/high prio).
                // We MUST re-confirm this track is still the best choice and valid.
                DownloadContext? confirmedContext = null;
                lock (_collectionLock)
                {
                    // Update Active map with new reality
                    var activeByPriority = GetActiveDownloadsByPriority();

                    // Check if our pre-selected 'nextContext' is still valid and optimal
                    // Or simply re-run selection to be safe
                    var eligibleTracks = _downloads.Where(t =>
                        t.State == PlaylistTrackState.Pending &&
                        (!t.NextRetryTime.HasValue || t.NextRetryTime.Value <= DateTime.UtcNow) &&
                        (!t.RetryAfterSlot.HasValue || t.RetryAfterSlot.Value <= _schedulerSlot))
                        .ToList();

                    confirmedContext = SelectNextTrackWithLaneAllocation(eligibleTracks, activeByPriority);
                }

                if (confirmedContext == null)
                {
                    // False alarm or lane filled up while waiting
                    _logger.LogDebug("Race Condition: Slot acquired but no eligible track found after wait. Releasing.");
                    if (tookSemaphoreSlot) _downloadSemaphore.Release();
                    await Task.Delay(100, token); // Backoff
                    continue;
                }

                // If we switched tracks (e.g. a higher priority one came in), use the new one.
                // If confirmedContext matches nextContext, great. If not, confirmedContext is better.
                nextContext = confirmedContext;

                // Transition state via update method
                await UpdateStateAsync(nextContext, PlaylistTrackState.Searching);

                // Advance the slot counter so retry-gated tracks count down correctly.
                Interlocked.Increment(ref _schedulerSlot);

                // Fire-and-forget pattern with guaranteed semaphore release
                bool finalTookSlot = tookSemaphoreSlot; // Capture for lambda
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessTrackAsync(nextContext, token);
                    }
                    finally
                    {
                        // ALWAYS release the semaphore, but ONLY if we actually took a slot (not VIP)
                        if (finalTookSlot)
                        {
                            _downloadSemaphore.Release();
                            _logger.LogDebug("Released semaphore slot. Available slots: {Available}/{Total}",
                                _downloadSemaphore.CurrentCount, _maxActiveDownloads);
                        }
                        OnPropertyChanged(nameof(ActiveWorkerSlots));
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DownloadManager processing loop exception");
                await Task.Delay(1000, token); // Prevent hot loop on error
            }
        }
    }

    private void EnqueuePreSearch(DownloadContext track)
    {
        // STRICT CONCURRENCY MODE:
        // Pre-search can create extra parallel network searches outside worker slots.
        // Keep discovery inside ProcessTrackAsync so total active network work equals slider value.
        _ = track;
    }

    private void PrimePipelineSearchForNextTrack(string currentTrackGlobalId)
    {
        _ = currentTrackGlobalId;
    }

    /// <summary>
    /// Advances a track's AvailabilityState past Ghost/QueuedForDownload/Downloading whenever its
    /// file is confirmed present, mirroring the same fixup <see cref="UpdateStateAsync"/> applies
    /// on a full download completion. The three "file already exists, skip the actual download"
    /// fast paths in <see cref="ProcessTrackAsync"/> set Status = Downloaded without going through
    /// that path, which is what left existing tracks stuck showing "FILE MISSING" in the Inspector
    /// despite having a real, already-analyzed file on disk (see AvailabilityStateReconciliationService
    /// for the one-off fix to rows that got stuck before this existed). Returns true if it changed anything.
    /// </summary>
    private static bool AdvancePastGhost(PlaylistTrack model)
    {
        if (model.AvailabilityState != TrackAvailabilityState.Ghost &&
            model.AvailabilityState != TrackAvailabilityState.QueuedForDownload &&
            model.AvailabilityState != TrackAvailabilityState.Downloading)
        {
            return false;
        }

        model.AvailabilityState = TrackAvailabilityState.LocalUnanalyzed;
        return true;
    }

    private async Task ProcessTrackAsync(DownloadContext ctx, CancellationToken ct)
    {
        ctx.CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trackCt = ctx.CancellationTokenSource.Token;

        using (LogContext.PushProperty("TrackHash", ctx.GlobalId))
        using (LogContext.PushProperty("CorrelationId", ctx.CorrelationId))
        {
            try
            {
                // Pre-check: File already exists at resolved destination path (global filesystem dedup)
                if (!string.IsNullOrWhiteSpace(ctx.Model.ResolvedFilePath) && File.Exists(ctx.Model.ResolvedFilePath))
                {
                    _logger.LogInformation("Track already exists on disk, reusing local file: {Artist} - {Title} => {Path}",
                        ctx.Model.Artist, ctx.Model.Title, ctx.Model.ResolvedFilePath);
                    _auditLogger.Log(ctx.GlobalId, $"Track already exists on disk, reusing local file: {ctx.Model.ResolvedFilePath}");

                    ctx.Model.Status = TrackStatus.Downloaded;
                    AdvancePastGhost(ctx.Model);
                    await _libraryService.UpdatePlaylistTrackAsync(ctx.Model);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Completed, wasAlreadyPresent: true);
                    return;
                }

                // Pre-check: Already downloaded in this project
                if (ctx.Model.Status == TrackStatus.Downloaded && File.Exists(ctx.Model.ResolvedFilePath))
                {
                    _auditLogger.Log(ctx.GlobalId, "Track already marked downloaded in project, verifying file path.");
                    if (AdvancePastGhost(ctx.Model))
                    {
                        await _libraryService.UpdatePlaylistTrackAsync(ctx.Model);
                    }
                    await UpdateStateAsync(ctx, PlaylistTrackState.Completed, wasAlreadyPresent: true);
                    return;
                }

                // Phase 0: Check if file already exists in global library (cross-project deduplication)
                // Gated by the "Prevent Duplicate Downloads" setting — previously ran unconditionally
                // regardless of the toggle.
                if (_config.CheckForDuplicates)
                {
                    var existingEntry = string.IsNullOrWhiteSpace(ctx.Model.TrackUniqueHash)
                        ? null
                        : await _libraryService.FindLibraryEntryAsync(ctx.Model.TrackUniqueHash);
                    if (existingEntry != null && File.Exists(existingEntry.FilePath))
                    {
                        _logger.LogInformation("â™»ï¸  Track already in library: {Artist} - {Title}, reusing file: {Path}",
                            ctx.Model.Artist, ctx.Model.Title, existingEntry.FilePath);
                        _auditLogger.Log(ctx.GlobalId, $"Track already in library, reusing file: {existingEntry.FilePath}");

                        // Reuse existing file instead of downloading
                        ctx.Model.ResolvedFilePath = existingEntry.FilePath;
                        ctx.Model.Status = TrackStatus.Downloaded;
                        AdvancePastGhost(ctx.Model);
                        await _libraryService.UpdatePlaylistTrackAsync(ctx.Model);
                        await UpdateStateAsync(ctx, PlaylistTrackState.Completed, wasAlreadyPresent: true);
                        return;
                    }
                }

                // Phase 3.1: Use Detection Service (Searching State)
                // Refactor Note: DiscoveryService now takes PlaylistTrack (Decoupled).
                // Phase 3B: Pass Blacklisted users for Health Monitor retries
                // Phase 2: Parallel Pre-Search Integration
                // Check if we already have a running search task for this track
                DownloadDiscoveryService.DiscoveryResult discoveryResult;
                Track? bestMatch;

                if (ctx.OverrideCandidate != null)
                {
                    bestMatch = ctx.OverrideCandidate;
                    ctx.OverrideCandidate = null;
                    discoveryResult = new DownloadDiscoveryService.DiscoveryResult(bestMatch, null, null);
                    _logger.LogInformation("Using manual override candidate for {Title}", ctx.Model.Title);
                }
                else if (_preSearchTasks.TryRemove(ctx.GlobalId, out var existingTask))
                {
                    _logger.LogDebug("⚡ Using Pre-Search result for {Title}", ctx.Model.Title);
                    discoveryResult = await existingTask; // Await the already running task
                    bestMatch = discoveryResult.BestMatch;
                }
                else
                {
                    discoveryResult = await ResolveDiscoveryWithStrictGateAsync(
                        _config.EnableAutoDownloadStrictMode,
                        _config.AutoDownloadAllowFuzzyFallback,
                        () => _autoSearchService.FindBestMatchAsync(ctx.Model, trackCt),
                        () => _discoveryService.FindBestMatchAsync(ctx.Model, trackCt, SnapshotBlacklistedUsers(ctx), ctx.CorrelationId),
                        strictDiagnostics =>
                        {
                            if (_config.AutoDownloadDiagnosticsEnabled)
                            {
                                _logger.LogInformation(
                                    "Strict-mode AutoDownload selected {Artist} - {Title} via {MatchType} in {ElapsedMs}ms",
                                    ctx.Model.Artist,
                                    ctx.Model.Title,
                                    strictDiagnostics.MatchType ?? "unknown",
                                    (DateTime.UtcNow - strictDiagnostics.StartedAtUtc).TotalMilliseconds);
                            }
                        },
                        (strictDiagnostics, fallbackAllowed) =>
                        {
                            _logger.LogInformation(
                                "Strict-mode miss for track {TrackId} ({Artist} - {Title}); fallback {FallbackPolicy}. query='{NormalizedQuery}'",
                                ctx.Model.Id,
                                ctx.Model.Artist,
                                ctx.Model.Title,
                                fallbackAllowed ? "allowed" : "blocked",
                                strictDiagnostics.NormalizedQuery ?? string.Empty);
                        });
                    bestMatch = discoveryResult.BestMatch;
                }
                ctx.HedgeAttempted = false;
                ctx.HedgeMatch = null;

                if (_config.EnableHedgedDownloadFailover && discoveryResult.RunnerUpMatch != null)
                {
                    var candidate = discoveryResult.RunnerUpMatch;
                    if (!string.IsNullOrWhiteSpace(candidate.Username) &&
                        !string.IsNullOrWhiteSpace(candidate.Filename) &&
                        !string.Equals(candidate.Username, bestMatch?.Username, StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.HedgeMatch = candidate;
                    }
                }

                // Capture search diagnostics
                if (discoveryResult.Log != null)
                {
                    if (string.IsNullOrWhiteSpace(discoveryResult.Log.CorrelationId))
                    {
                        discoveryResult.Log.CorrelationId = ctx.CorrelationId;
                    }
                    ctx.SearchAttempts.Add(discoveryResult.Log);
                }

                if (bestMatch == null)
                {
                    // Determine specific failure reason based on search history
                    var failureReason = DownloadFailureReason.NoSearchResults; // Default

                    // Phase 21: Progressive Retry Logic (3x Session, 3x Restart)
                    ctx.Model.SearchRetryCount++;

                    // Determine specific failure reason based on search history for better logs/UI
                    if (ctx.SearchAttempts.Any())
                    {
                        var lastAttempt = ctx.SearchAttempts.Last();

                        // Check for discovery timeout first (90s global limit)
                        if (lastAttempt.TimedOut)
                        {
                            failureReason = DownloadFailureReason.DiscoveryTimeout;
                        }
                        else if (lastAttempt.ResultsCount > 0)
                        {
                            // Results were found but rejected - determine why
                            if (lastAttempt.RejectedByQuality > 0)
                                failureReason = DownloadFailureReason.AllResultsRejectedQuality;
                            else if (lastAttempt.RejectedByFormat > 0)
                                failureReason = DownloadFailureReason.AllResultsRejectedFormat;
                            else if (lastAttempt.RejectedByBlacklist > 0)
                                failureReason = DownloadFailureReason.AllResultsBlacklisted;
                        }
                    }

                    if (ctx.Model.SearchRetryCount < _config.MaxSearchAttempts)
                    {
                        // In-session retry: send to the back of the queue by requiring all
                        // currently-pending tracks to be dispatched first.  This prevents a
                        // failed track from immediately re-competing with fresh tracks.
                        ctx.Model.Priority = 20; // Low-priority lane for retries

                        long pendingAhead;
                        lock (_collectionLock)
                            pendingAhead = _downloads.Count(d =>
                                d != ctx &&
                                d.State == PlaylistTrackState.Pending &&
                                (!d.RetryAfterSlot.HasValue || d.RetryAfterSlot.Value <= _schedulerSlot));

                        // Slot gate: track becomes eligible again after every currently-pending
                        // track has been dispatched at least once — capped so this doesn't scale
                        // unboundedly with total queue size (see MAX_RETRY_SLOT_DEFERRAL).
                        ctx.RetryAfterSlot = _schedulerSlot + Math.Clamp(pendingAhead, 1, MAX_RETRY_SLOT_DEFERRAL);

                        // Time gate: safety net so we don't hammer the network if the queue
                        // drains faster than expected.  30s base + small jitter.
                        var jitterSeconds = _jitterRandom.Next(0, 30);
                        ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(30 + jitterSeconds);

                        _logger.LogWarning(
                            "🔍 No match for {Title}. In-session retry {Count}/{MaxAttempts}. " +
                            "Back-of-queue: eligible after slot {Slot} (currently {Now}) | Reason: {Reason}.",
                            ctx.Model.Title, ctx.Model.SearchRetryCount, _config.MaxSearchAttempts,
                            ctx.RetryAfterSlot, _schedulerSlot, failureReason);

                        await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Queued for retry #{ctx.Model.SearchRetryCount} ({failureReason})");
                        return;
                    }
                    else
                    {
                        // Exceeded session retries: increment restart count and mark Failed/OnHold
                        ctx.Model.SearchRetryCount = 0; // Reset for next session
                        ctx.Model.NotFoundRestartCount++;

                        // If already in OnHold (MP3 lane) and still failing → truly terminal
                        if (ctx.Model.Status == TrackStatus.OnHold && ctx.Model.NotFoundRestartCount >= _config.MaxSearchAttempts)

                        {
                            _logger.LogError("🚫 TERMINAL: {Title} failed all FLAC AND MP3 attempts. Marking as permanently Failed.", ctx.Model.Title);
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(ctx.GlobalId, "🚫 Not found on network — tried FLAC + MP3 across multiple sessions.", false, ctx.CorrelationId));
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, "Terminal: Not found (FLAC + MP3 exhausted)");
                            return;
                        }

                        // 3 sessions x 3 retries = 9 total (FLAC lane)
                        if (ctx.Model.NotFoundRestartCount >= _config.MaxSearchAttempts && IsMp3FallbackAllowed(ctx.Model))

                        {
                            _logger.LogWarning("🎵 MP3 FALLBACK: {Title} exhausted all FLAC attempts. Auto-escalating to MP3 search lane.", ctx.Model.Title);
                            ctx.Model.Status = TrackStatus.OnHold;
                            ctx.Model.NotFoundRestartCount = 0; // Reset so MP3 lane gets fresh attempts
                            ctx.Model.SearchRetryCount = 0;
                            ctx.Model.Priority = 15; // Slightly lower priority in the queue
                            // Send to back of queue — MP3 fallback competes only after fresh tracks
                            long pendingAheadMp3;
                            lock (_collectionLock)
                                pendingAheadMp3 = _downloads.Count(d => d != ctx && d.State == PlaylistTrackState.Pending);
                            ctx.RetryAfterSlot = _schedulerSlot + Math.Clamp(pendingAheadMp3, 1, MAX_RETRY_SLOT_DEFERRAL);
                            ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(30);
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(ctx.GlobalId, $"🎵 FLAC not found after {_config.MaxSearchAttempts * _config.MaxSearchAttempts} attempts — switching to MP3 fallback lane automatically.", false, ctx.CorrelationId));
                            await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"FLAC failed ({_config.MaxSearchAttempts * _config.MaxSearchAttempts}x) → Auto MP3 Fallback queued");

                        }
                        else if (ctx.Model.NotFoundRestartCount >= _config.MaxSearchAttempts)

                        {
                            _logger.LogWarning("🛡️ Lossless-only profile active for {Title}; skipping MP3 fallback escalation after FLAC exhaustion.", ctx.Model.Title);
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(ctx.GlobalId, "🛡️ Lossless-only profile active. MP3 fallback skipped.", false, ctx.CorrelationId));
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, $"Lossless-only search exhausted ({failureReason}). MP3 fallback disabled by profile.");
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ Session Failure for {Title}: No results found after 3 attempts. Deferring to next app restart (Session {Count}/3 | Reason: {Reason}).",
                                ctx.Model.Title, ctx.Model.NotFoundRestartCount, failureReason);
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, $"Search failed 3x in this session ({failureReason}). Deferring to next restart.");
                        }
                        return;
                    }
                }

                // Phase 3.1: Download Logic (Downloading State)
                if (!TryValidateTrackForQueue(
                    bestMatch,
                    out var validationReason,
                    ctx.Model.MinBitrateOverride,
                    allowFallbackFormat: IsMp3FallbackAllowed(ctx.Model)))
                {
                    _logger.LogWarning("Rejected candidate before download for {Title}: {Reason}. Candidate: {Filename}",
                        ctx.Model.Title, validationReason, bestMatch.Filename);

                    ctx.Model.SearchRetryCount++;
                    ctx.Model.Priority = 20;
                    ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(10);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Candidate rejected: {validationReason}. Retrying later.");
                    return;
                }

                await DownloadFileAsync(ctx, bestMatch, trackCt);
            }
            catch (OperationCanceledException)
            {
                // Enhanced cancellation diagnostics
                var cancellationReason = "Unknown";

                // Fix #3: Preemption-aware cancellation handling
                if (ctx.Model.Priority >= 10 && ctx.State == PlaylistTrackState.Downloading)
                {
                    cancellationReason = "Preempted for high-priority download";
                    _logger.LogInformation("â¸ Download preempted for high-priority work: {Title} - deferring to queue", ctx.Model.Title);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Deferred, "Deferred for high-priority downloads");
                    return;
                }

                // Check if it was user-initiated pause
                if (ctx.State == PlaylistTrackState.Paused)
                {
                    cancellationReason = "User paused download";
                    _logger.LogInformation("â¸ Download paused by user: {Title}", ctx.Model.Title);
                    return;
                }

                // Check if it was explicit cancellation
                if (ctx.State == PlaylistTrackState.Cancelled)
                {
                    cancellationReason = "User cancelled download";
                    _logger.LogInformation("âŒ Download cancelled by user: {Title}", ctx.Model.Title);
                    return;
                }

                // Check if it's a global shutdown
                if (_globalCts.Token.IsCancellationRequested)
                {
                    _logger.LogInformation("Shutdown detected. Preserving state for {Title} (Current State: {State})",
                        ctx.Model.Title, ctx.State);
                    return;
                }

                // Circuit breaker already moved this track — just return; it will be re-queued on reconnect
                if (ctx.State == PlaylistTrackState.WaitingForConnection)
                    return;

                // Connection dropped mid-task: re-queue rather than marking Cancelled
                if (!_soulseek.IsLoggedIn)
                {
                    _logger.LogInformation(
                        "Connection lost during {State} for {Title} — re-queuing for reconnect",
                        ctx.State, ctx.Model.Title);
                    ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(5);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Connection lost — will retry after reconnect");
                    return;
                }

                // Otherwise it's an unexpected cancellation (health monitor, timeout, etc.)
                cancellationReason = "System/timeout cancellation";
                _logger.LogWarning(
                    "Unexpected cancellation for {Title} in state {State}. reason={Reason} correlationId={CorrelationId}",
                    ctx.Model.Title,
                    ctx.State,
                    cancellationReason,
                    ctx.CorrelationId ?? "-");
                await UpdateStateAsync(ctx, PlaylistTrackState.Cancelled);
            }
            catch (TimeoutException tex)
            {
                // Phase 3: Stalled Detection
                _logger.LogWarning(
                    "Download stall detected for {Title}. reason={Reason} correlationId={CorrelationId}",
                    ctx.Model.Title,
                    tex.Message,
                    ctx.CorrelationId ?? "-");

                if (await TryRunHedgeFailoverAsync(ctx, trackCt, "primary transfer stalled"))
                {
                    return;
                }

                if (!string.IsNullOrEmpty(ctx.CurrentUsername))
                {
                    ctx.BlacklistUser(ctx.CurrentUsername);
                }

                // Stall: blacklist the peer, go back-of-queue (not immediately re-compete).
                ctx.Model.Priority = 20;
                long pendingAheadStall;
                lock (_collectionLock)
                    pendingAheadStall = _downloads.Count(d => d != ctx && d.State == PlaylistTrackState.Pending);
                ctx.RetryAfterSlot = _schedulerSlot + Math.Clamp(pendingAheadStall, 1, MAX_RETRY_SLOT_DEFERRAL);
                ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(15); // Minimum gap after stall
                await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Stall detected — re-queued behind active tracks.");
            }
            catch (DownloadDiscoveryService.DiscoveryConnectionUnavailableException ex)
            {
                // Transient connectivity issue: do NOT consume "not found" attempt budget.
                // Keep the track hot in queue with a short retry delay.
                _logger.LogInformation("Discovery paused for {Title} due to temporary connection loss: {Message}", ctx.Model.Title, ex.Message);
                ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(15);
                await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Waiting for Soulseek connection. Retrying shortly.");
            }
            catch (SearchRejectedException srex)
            {
                _logger.LogWarning(
                    "Search rejected for {Title}. reason={Reason} correlationId={CorrelationId}",
                    ctx.Model.Title,
                    srex.Message,
                    ctx.CorrelationId ?? "-");

                // 1. Capture Diagnostics
                if (srex.SearchLog != null)
                {
                    ctx.SearchAttempts.Add(srex.SearchLog);
                }

                // 2. Exponential Backoff for "No Results" (Retry Logic)
                ctx.RetryCount++;
                if (_config.AutoRetryFailedDownloads && ctx.RetryCount < _config.MaxDownloadRetries)
                {
                    var delayMinutes = Math.Pow(2, ctx.RetryCount); // 2, 4, 8, 16...
                    ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(delayMinutes);
                    ctx.Model.Priority = 20; // Low priority

                    // Important: Set state to Pending so it stays in the queue, but with a status message explaining the delay
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Retrying in {delayMinutes}m: Search Rejected");
                    _logger.LogInformation("Scheduled retry #{Count} for {GlobalId} at {Time} due to search rejection", ctx.RetryCount, ctx.GlobalId, ctx.NextRetryTime);
                }
                else
                {
                    // Terminal Failure
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, DownloadFailureReason.NoSearchResults);
                }
            }
            catch (Exception ex)
            {
                // If the Soulseek client is no longer connected, the exception is ours (dropped
                // WiFi, mesh roam, server disconnect) — not the peer's fault.  Re-queue instead of
                // burning a retry slot or marking the track Failed.
                if (!_soulseek.IsLoggedIn)
                {
                    _logger.LogInformation(
                        "Transfer exception for {Title} while client is disconnected — re-queuing ({ExType}: {Msg})",
                        ctx.Model.Title, ex.GetType().Name, ex.Message);
                    ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(5);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Connection lost mid-transfer — will retry after reconnect");
                    return;
                }

                var transferDisposition = ClassifyTransferFailure(ex);

                // #334: a file-read error on a resumed transfer is usually our stale resume offset
                // (the peer's file changed or is shorter than our .part), not the peer's fault.
                // Discard the partial and retry from offset 0 rather than blacklisting an innocent peer.
                var resetPartForRetry = transferDisposition.ResetPartAndRetry && ctx.IsResuming;
                if (resetPartForRetry)
                {
                    _logger.LogWarning(
                        "File-read error on resumed transfer for {Title}; discarding the partial file and retrying from 0. detail={Detail}",
                        ctx.Model.Title,
                        ex.Message);
                    foreach (var deleted in Downloads.PartFileGuard.DeletePartFiles(ctx.PartFilePath, ctx.Model.ResolvedFilePath, StagingPartPathFor(ctx)))
                        _logger.LogInformation("Discarded partial file on file-read error: {Path}", deleted);
                    ctx.PartFilePath = null;
                    ctx.IsResuming = false;
                    ctx.BytesReceived = 0;
                }

                if (transferDisposition.AllowHedgeFailover)
                {
                    _logger.LogWarning(
                        "Peer transfer failure for {Title}. reason={Reason} correlationId={CorrelationId}",
                        ctx.Model.Title,
                        ex.Message,
                        ctx.CorrelationId ?? "-");

                    if (await TryRunHedgeFailoverAsync(ctx, trackCt, "primary transfer was rejected"))
                    {
                        return;
                    }
                }
                else if (!resetPartForRetry)
                {
                    _logger.LogError(
                        ex,
                        "ProcessTrackAsync error for {GlobalId}. reason={Reason} correlationId={CorrelationId}",
                        ctx.GlobalId,
                        ex.Message,
                        ctx.CorrelationId ?? "-");
                }

                // FIX: Blacklist the failing peer so we don't pick them again instantly.
                // Skipped for a file-read error on resume — that is our stale offset, not the peer.
                if (!resetPartForRetry && !string.IsNullOrEmpty(ctx.CurrentUsername))
                {
                    if (ctx.BlacklistUser(ctx.CurrentUsername))
                    {
                        _logger.LogWarning(
                            "Blacklisted peer {User} for {Track}. reason={Reason} detail={Detail} correlationId={CorrelationId}",
                            ctx.CurrentUsername,
                            ctx.Model.Title,
                            transferDisposition.OperatorMessage,
                            ex.Message,
                            ctx.CorrelationId ?? "-");
                    }
                }

                if (!transferDisposition.RetryFailureReason.ShouldAutoRetry())
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, transferDisposition.RetryFailureReason);
                    return;
                }

                // Exponential / classified backoff logic
                ctx.RetryCount++;
                if (_config.AutoRetryFailedDownloads && ctx.RetryCount < _config.MaxDownloadRetries)
                {
                    // Epic 4.1: Use seconds-scale exponential backoff (1s → 2s → 4s → 8s, capped 32s)
                    // for transient network failures (Delay == null).  Fixed delays (e.g. 2 minutes
                    // for peer-queue rejection) are respected verbatim.
                    TimeSpan retryDelay = transferDisposition.Delay
                        ?? TimeSpan.FromSeconds(Math.Min(32, Math.Pow(2, ctx.RetryCount - 1)));
                    ctx.NextRetryTime = DateTime.UtcNow.Add(retryDelay);
                    ctx.Model.Priority = 20; // LOW PRIORITY: Send retries to back of queue
                    var delayDesc = retryDelay.TotalSeconds < 60
                        ? $"{retryDelay.TotalSeconds:0}s"
                        : NumFormat.Ui($"{retryDelay.TotalMinutes:0.#}m");
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Retrying in {delayDesc}: {transferDisposition.OperatorMessage}");
                    _logger.LogInformation("Scheduled retry #{Count} for {GlobalId} at {Time} (low priority, delay={Delay})",
                        ctx.RetryCount, ctx.GlobalId, ctx.NextRetryTime, delayDesc);
                }
                else
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, transferDisposition.RetryFailureReason);
                }
            }
        }
    }

    // ========================================
    // Phase 3C: Multi-Lane Priority Engine
    // ========================================

    /// <summary>
    /// Gets count of active downloads grouped by priority level.
    /// Returns dictionary: Priority -> Count
    /// </summary>
    private Dictionary<int, int> GetActiveDownloadsByPriority()
    {
        var activeDownloads = _downloads
            .Where(d => d.State == PlaylistTrackState.Searching || d.State == PlaylistTrackState.Downloading)
            .GroupBy(d => d.Model.Priority)
            .ToDictionary(g => g.Key, g => g.Count());

        return activeDownloads;
    }

    /// <summary>
    /// Selects next track by queue priority only.
    /// Concurrency is enforced exclusively by the global worker-slot semaphore.
    /// </summary>
    private DownloadContext? SelectNextTrackWithLaneAllocation(
        List<DownloadContext> eligibleTracks,
        Dictionary<int, int>? activeByPriority) // Made optional
    {
        if (!eligibleTracks.Any()) return null;

        _ = activeByPriority;
        return eligibleTracks
            .OrderByDescending(t => t.Model.AddedAt == PinnedToTop) // 0. Pinned by "first in line" / Bump to top
            .ThenBy(t => t.Model.JobPriority)          // 1. Playlist tier (0=Critical … 3=Low)
            .ThenBy(t => t.Model.JobManualSortOrder)   // 2. User drag-drop order within tier
            .ThenBy(t => t.Model.Priority)             // 3. Track-level priority (0=VIP, 1=Std, 10=BG)
            .ThenBy(t => t.Model.AddedAt)              // 4. FIFO tiebreaker
            .FirstOrDefault();
    }

    /// <summary>AddedAt value that marks a track as pinned to the very top of the download queue —
    /// ahead of every playlist tier (see SelectNextTrackWithLaneAllocation), not just first within
    /// its own playlist as Priority 0 alone gives.</summary>
    public static readonly DateTime PinnedToTop = DateTime.MinValue;

    public void BumpTrackToTop(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        if (ctx != null)
        {
            ctx.Model.Priority = 0;
            ctx.Model.AddedAt = PinnedToTop; // absolute top, across playlists

            if (ctx.State == PlaylistTrackState.Pending || ctx.State == PlaylistTrackState.Stalled || ctx.State == PlaylistTrackState.Paused)
            {
                _ = UpdateStateAsync(ctx, PlaylistTrackState.Pending);
            }
            _ = RefillQueueAsync();
        }
    }

    /// <summary>
    /// Prioritizes all tracks from a specific project by bumping to Priority 0 (High).
    /// Phase 3C: The "VIP Pass" - allows user to jump queue with specific playlist.
    /// Hardening Fix #1: Now persists to database for crash resilience.
    /// </summary>
    public async Task PrioritizeProjectAsync(Guid playlistId)
    {
        _logger.LogInformation("ðŸš€ Prioritizing project: {PlaylistId}", playlistId);

        // Fix #1: Persist to database FIRST for crash resilience
        await _databaseService.UpdatePlaylistTracksPriorityAsync(playlistId, 0);

        // Update in-memory contexts
        int updatedCount = 0;
        lock (_collectionLock)
        {
            foreach (var download in _downloads.Where(d => d.Model.PlaylistId == playlistId && d.State == PlaylistTrackState.Pending))
            {
                download.Model.Priority = 0;
                updatedCount++;
            }
        }

        _logger.LogInformation("âœ… Prioritized {Count} tracks from project {PlaylistId} (database + in-memory)",
            updatedCount, playlistId);
    }

    /// <summary>
    /// Pauses the lowest priority active download to free a slot for high-priority track.
    /// Phase 3C: Preemption support.
    /// </summary>
    private async Task PauseLowestPriorityDownloadAsync()
    {
        DownloadContext? lowestPriority = null;

        lock (_collectionLock)
        {
            lowestPriority = _downloads
                .Where(d => d.State == PlaylistTrackState.Downloading || d.State == PlaylistTrackState.Searching)
                .OrderByDescending(d => d.Model.Priority) // Highest priority value = lowest priority
                .ThenBy(d => d.Model.AddedAt)
                .FirstOrDefault();
        }

        if (lowestPriority != null && lowestPriority.Model.Priority > 0) // Preempt anything lower than High Priority (0)
        {
            _logger.LogInformation("â¸ Preempting lower priority download (Priority {Prio}): {Title}",
                lowestPriority.Model.Priority, lowestPriority.Model.Title);
            await PauseTrackAsync(lowestPriority.Model.TrackUniqueHash);
        }
    }


    // ── Playlist-level priority public API ───────────────────────────────────

    /// <summary>
    /// Returns the current effective playlist priority for a given job.
    /// Returns Normal (2) if the job is unknown.
    /// </summary>
    public PlaylistPriority GetJobPriority(Guid jobId)
        => (PlaylistPriority)Math.Clamp(_jobEffectivePriority.GetValueOrDefault(jobId, (int)PlaylistPriority.Normal), 0, 3);

}
