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
/// Queue mutation, hydration, state transitions, pause/resume/cancel/delete, and retry lifecycle (#307).
/// </summary>
public partial class DownloadManager
{
    /// <summary>
    /// Queues tracks for download. Runs on a background thread — the per-track work below (hash
    /// lookups, event-bus fan-out, per-track DB sync) is O(N) real work, and for a "Download
    /// Missing"/"Download Album" batch of hundreds of tracks that used to run synchronously on
    /// whatever thread called this. Most callers are UI commands, so a big batch froze the app —
    /// including the just-opened context menu, which can't repaint/dismiss while the UI thread is
    /// blocked, making it look stuck open over other windows until the batch finished.
    /// <see cref="_downloads"/> is a plain List guarded by <see cref="_collectionLock"/>, safe to
    /// mutate from a background thread.
    /// </summary>
    public void QueueTracks(List<PlaylistTrack> tracks)
    {
        _ = Task.Run(() => QueueTracksCore(tracks));
    }

    /// <summary>
    /// Same as <see cref="QueueTracks"/>, awaitable for the rare caller that needs the freshly-
    /// created DownloadContext to exist before it continues (e.g. GhostAcquisitionOrchestrator
    /// immediately looks its own just-queued track back up in ActiveDownloads to attach an
    /// OverrideCandidate) — background-service callers can safely await this without risking a UI
    /// freeze, since they're not on the UI thread to begin with.
    /// </summary>
    public Task QueueTracksAsync(List<PlaylistTrack> tracks)
    {
        return Task.Run(() => QueueTracksCore(tracks));
    }

    private void QueueTracksCore(List<PlaylistTrack> tracks)
    {
        _logger.LogInformation("Queueing project tracks with {Count} tracks", tracks.Count);

        int skipped = 0;
        int queued = 0;

        // O(N) Optimization: Create lookup maps OUTSIDE the lock to prevent UI freeze on large imports
        var currentDownloads = ActiveDownloads;

        var existingMap = currentDownloads
            .Where(d => !string.IsNullOrEmpty(d.Model.TrackUniqueHash))
            .GroupBy(d => d.Model.TrackUniqueHash)
            .ToDictionary(g => g.Key!, g => g.First());

        var existingIds = currentDownloads
            .GroupBy(d => d.Model.Id)
            .ToDictionary(g => g.Key, g => g.First());

        // Published after the lock is released below — publishing per-track while holding
        // _collectionLock ran arbitrary subscriber code (event-bus fan-out) with the lock held,
        // for every track in the batch.
        var newlyQueued = new List<(DownloadContext Ctx, PlaylistTrack Track)>();
        var retriedContexts = new List<DownloadContext>();

        lock (_collectionLock)
        {

            foreach (var track in tracks)
            {
                // EnableAutoAcquireOnImport = false suppresses background Spotify-sync auto-downloads.
                // Priority 0 means the user explicitly confirmed this import via the preview dialog,
                // so we let those through regardless of the flag.
                if (track.AvailabilityState == TrackAvailabilityState.Ghost
                    && !_config.EnableAutoAcquireOnImport
                    && track.Priority > 0)
                {
                    skipped++;
                    continue;
                }

                DownloadContext? existingCtx = null;
                var existingCtxIsCrossPlaylistHashMatch = false;

                if (existingIds.TryGetValue(track.Id, out var byId))
                {
                    existingCtx = byId;
                }
                else if (existingMap.TryGetValue(track.TrackUniqueHash, out var byHash))
                {
                    existingCtx = byHash;
                    // This row belongs to a DIFFERENT PlaylistTrack (found by hash, not by Id) — almost
                    // always a different playlist's copy of the same song.
                    existingCtxIsCrossPlaylistHashMatch = byHash.Model.Id != track.Id;
                }

                // A cross-playlist hash match sitting in a FAILURE state (Failed/Cancelled/Deferred/
                // Pending) must NOT be "retried" here — that reused/bumped the OTHER playlist's own
                // context, so e.g. clicking "Download Missing" on playlist A would silently revive and
                // show a "Syncing" indicator on an unrelated playlist B just because they share a
                // track. Deliberately narrower than "!IsActive": a Completed cross-playlist match must
                // stay handled by the normal skip path below, not be nulled out here — an earlier
                // version of this guard used the broader "!IsActive" check, which also matched
                // Completed, so every re-queue of an already-finished track (e.g. the synthetic
                // PlaylistId=Guid.Empty rows the "All Tracks" view feeds through this same path)
                // spawned a brand-new DownloadContext that instantly short-circuit-completed again —
                // firing a fresh duplicate notification and a phantom DB row every single time.
                var existingCtxIsStaleCrossPlaylistFailure = existingCtxIsCrossPlaylistHashMatch
                    && existingCtx != null
                    && existingCtx.State is PlaylistTrackState.Failed
                        or PlaylistTrackState.Cancelled
                        or PlaylistTrackState.Deferred
                        or PlaylistTrackState.Pending;

                if (existingCtxIsStaleCrossPlaylistFailure)
                {
                    existingCtx = null;
                }

                if (existingCtx != null)
                {
                    // Fix: Smart Retry if in a terminal/failure state. Paused is included only
                    // when it wasn't an explicit user pause: an OnHold track (background search
                    // gave up) hydrates into Paused too, and without this an explicit re-queue —
                    // "Download Missing" reviving OnHold tracks, or the Unfindable-tracks "Retry"
                    // button — silently fell through to the skip branch below since the existing
                    // in-memory context was never actually touched, even though the DB row and UI
                    // both said it would be retried.
                    if (existingCtx.State == PlaylistTrackState.Failed ||
                        existingCtx.State == PlaylistTrackState.Cancelled ||
                        existingCtx.State == PlaylistTrackState.Deferred ||
                        existingCtx.State == PlaylistTrackState.Pending ||
                        (existingCtx.State == PlaylistTrackState.Paused && !existingCtx.Model.IsUserPaused))
                    {
                        _logger.LogInformation("Retrying existing track {Title} (State: {State}) - Bumping to Priority 10 (Standard)", track.Title, existingCtx.State);

                        existingCtx.Model.Priority = 10;
                        existingCtx.Model.IsClearedFromDownloadCenter = false;
                        retriedContexts.Add(existingCtx);

                        existingCtx.RetryCount = 0;
                        existingCtx.NextRetryTime = null;
                        existingCtx.RetryAfterSlot = null; // Clear slot gate on explicit retry
                        existingCtx.FailureReason = null;


                        queued++;
                        continue;
                    }

                    skipped++;
                    _logger.LogDebug("Skipping track {Artist} - {Title}: already active/completed (State: {State})",
                        track.Artist, track.Title, existingCtx.State);
                    continue;
                }

                if (_downloads.Count(d => d.State == PlaylistTrackState.Pending) >= LAZY_QUEUE_BUFFER_SIZE
                    && track.Priority > 0)
                {
                     skipped++;
                     continue;
                }

                var ctx = new DownloadContext(track);
                _downloads.Add(ctx);

                existingMap[track.TrackUniqueHash] = ctx;
                existingIds[track.Id] = ctx;

                queued++;

                newlyQueued.Add((ctx, track));
            }
        }

        foreach (var retryCtx in retriedContexts)
        {
            _ = UpdateStateAsync(retryCtx, PlaylistTrackState.Pending);
        }

        foreach (var (ctx, track) in newlyQueued)
        {
            _eventBus.Publish(new TrackAddedEvent(track, PlaylistTrackState.Pending));
            _ = SyncDbAsync(ctx);
        }

        if (skipped > 0)
        {
            _logger.LogInformation("Queued {Queued} new tracks, skipped {Skipped} already queued tracks", queued, skipped);
        }

        if (queued > 0)
        {
             // Phase 21: Targeted Refill - Only refill for projects that were just queued
             var projectIds = tracks.Select(t => t.PlaylistId).Distinct().ToList();
             foreach(var pid in projectIds)
             {
                 _ = RefillQueueAsync(pid);
             }

             // Epic 4.1: Snapshot the pending queue to DownloadQueueItems so these tracks
             // survive an app restart and appear as Pending on next launch.
             _ = Task.Run(async () =>
             {
                 try
                 {
                     List<Guid> pendingIds;
                     lock (_collectionLock)
                     {
                         pendingIds = _downloads
                             .Where(d => d.State == PlaylistTrackState.Pending && d.Model.Id != Guid.Empty)
                             .Select(d => d.Model.Id)
                             .ToList();
                     }
                     await _databaseService.SaveDownloadQueueSnapshotAsync(pendingIds);
                     _logger.LogDebug("[QueuePersist] Saved {Count} pending tracks to queue snapshot", pendingIds.Count);
                 }
                 catch (Exception ex)
                 {
                     // Was LogDebug (invisible in production) — a failure here means pending
                     // downloads can silently vanish or reappear as phantom-pending after restart.
                     _logger.LogWarning(ex, "[QueuePersist] Failed to save queue snapshot after QueueTracks");
                 }
             });

             // Auto-start engine if not running and we have tracks to process
             if (!IsRunning)
             {
                 _logger.LogInformation("Auto-starting download engine for {Count} queued tracks", queued);
                 var logger = _logger;
                 _ = StartAsync().ContinueWith(
                     t => logger.LogError(t.Exception, "Download engine auto-start failed"),
                     TaskContinuationOptions.OnlyOnFaulted);
             }
        }
    }

    private void HydrateAndAddEntities(List<PlaylistTrackEntity> entities)
    {
        // Build models and contexts outside _collectionLock to keep the critical section minimal (#316).
        var hydratedContexts = new List<DownloadContext>(entities.Count);
        var batchBuffer = new List<(PlaylistTrack, PlaylistTrackState?)>(entities.Count);

        foreach (var t in entities)
        {
            // Map PlaylistTrackEntity -> PlaylistTrack Model
            var model = new PlaylistTrack
            {
                Id = t.Id,
                PlaylistId = t.PlaylistId,
                Artist = t.Artist,
                Title = t.Title,
                Album = t.Album,
                TrackUniqueHash = t.TrackUniqueHash,
                Status = t.Status,
                ResolvedFilePath = t.ResolvedFilePath ?? "",
                SpotifyTrackId = t.SpotifyTrackId,
                AlbumArtUrl = t.AlbumArtUrl,
                Format = t.Format,
                Bitrate = t.Bitrate,
                Priority = t.Priority,
                AddedAt = t.AddedAt,
                CompletedAt = t.CompletedAt,
                IsClearedFromDownloadCenter = t.IsClearedFromDownloadCenter,
                IsUserPaused = t.IsUserPaused,
                SourcePlaylistId = t.SourcePlaylistId,
                SourcePlaylistName = t.SourcePlaylistName,
                SearchRetryCount = 0, // Reset in-session counter on start
                NotFoundRestartCount = t.NotFoundRestartCount,
                // Stamp playlist-level priority from cache (transient, not from DB column)
                JobPriority = GetEffectiveJobPriority(t.PlaylistId),
                JobManualSortOrder = _jobManualSortOrder.GetValueOrDefault(t.PlaylistId, 0),
            };

            // Map status to download state
            var ctx = new DownloadContext(model);

            // Phase 3 Hardening: Accurate State Restoration
            // 1. If user explicitly paused it, honor that state
            if (model.IsUserPaused)
            {
                ctx.State = PlaylistTrackState.Paused;
            }
            else
            {
                // 2. Otherwise map based on last known Status
                ctx.State = t.Status switch
                {
                    TrackStatus.Downloaded => PlaylistTrackState.Completed,
                    TrackStatus.Failed => PlaylistTrackState.Failed,
                    TrackStatus.Skipped => PlaylistTrackState.Cancelled,
                    TrackStatus.OnHold => IsMp3FallbackAllowed(model) ? PlaylistTrackState.Paused : PlaylistTrackState.Pending,
                    // If it was pending/downloading/searching, we make it Pending to restart
                    _ => PlaylistTrackState.Pending
                };
            }

            // Phase 21: Auto-escalate to OnHold if restart limit reached
            if (model.NotFoundRestartCount >= 3 && model.Status != TrackStatus.Downloaded && IsMp3FallbackAllowed(model))
            {
                model.Status = TrackStatus.OnHold;
                ctx.State = PlaylistTrackState.Paused;
            }

            hydratedContexts.Add(ctx);

            // Issue #4: Batch track events instead of firing per-track to prevent UI freeze
            batchBuffer.Add((model, ctx.State));
        }

        lock (_collectionLock)
        {
            _downloads.AddRange(hydratedContexts);
        }

        // Issue #4 / #316: Publish outside _collectionLock so event subscribers never run under the lock.
        if (batchBuffer.Count > 0)
        {
            _eventBus.Publish(new BatchTracksAddedEvent(batchBuffer.AsReadOnly()));
        }
    }

    /// <summary>
    /// Phase 3C.5: "The Waiting Room" - Fetches pending tracks from DB if buffer is low.
    /// Manages memory pressure by ensuring we don't hydrate 50,000 pending tracks.
    /// Refactoring: Added optional projectId to support targeted project activation.
    /// </summary>
    private async Task RefillQueueAsync(Guid? projectId = null)
    {
        try
        {
            List<Guid> excludeIds;
            int needed;

            lock (_collectionLock)
            {
                // If we are refilling for a specific project, we check the buffer for that project specifically
                int pendingCount = projectId.HasValue
                    ? _downloads.Count(d => d.Model.PlaylistId == projectId && d.State == PlaylistTrackState.Pending)
                    : _downloads.Count(d => d.State == PlaylistTrackState.Pending);

                if (pendingCount >= LAZY_QUEUE_BUFFER_SIZE) return; // Buffer full enough

                needed = LAZY_QUEUE_BUFFER_SIZE - pendingCount;
                excludeIds = _downloads.Select(d => d.Model.Id).ToList();
            }

            if (needed <= 0) return;

            // Fetch next batch from "Waiting Room" (DB)
            // If projectId is provided, we ONLY fetch for that project to prevent global mass-initialization
            var newTracks = projectId.HasValue
                ? await _databaseService.GetPendingTracksForProjectAsync(projectId.Value, needed, excludeIds)
                : await _databaseService.GetPendingPriorityTracksAsync(needed, excludeIds);

            if (newTracks.Any())
            {
                _logger.LogDebug("Refilling queue with {Count} tracks for {Scope}",
                    newTracks.Count, projectId.HasValue ? $"Project {projectId}" : "Global Queue");
                HydrateAndAddEntities(newTracks);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refill queue from database");
        }
    }
        // Processing loop picks this up automatically

    // Updated Delete to take GlobalId instead of VM
    public async Task DeleteTrackFromDiskAndHistoryAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);

        using (LogContext.PushProperty("TrackHash", globalId))
        {
            _logger.LogInformation("Deleting track from disk and history");

            // 1. Cancel active download, if any
            ctx?.CancellationTokenSource?.Cancel();

            // 2. Resolve the file path. An active DownloadContext (still mid-download, or
            // recently completed and hydrated into _downloads) has it directly, but that in-memory
            // collection does NOT contain every library track — most tracks a user right-clicks to
            // delete were downloaded/imported long ago and were never (or are no longer) tracked
            // there. Previously this whole method returned immediately when ctx was null, which
            // was a silent no-op for exactly that common case — "delete" did nothing at all, with
            // no error, no log, nothing. Fall back to the persisted library entry, same pattern
            // ResetTrackToPendingAsync already used below.
            var filePath = ctx?.Model?.ResolvedFilePath;
            if (string.IsNullOrEmpty(filePath))
            {
                var entry = await _libraryService.FindLibraryEntryAsync(globalId);
                filePath = entry?.FilePath;
            }

            // 3. Delete Physical Files. The caller (TrackOperationsViewModel) is responsible for
            // stopping playback of this track first if it's the one currently loaded — NAudio
            // holds the file open for as long as that deck is alive, so deleting a still-playing
            // track's file fails here (silently, save for the log line below) until it's released.
            DeleteLocalFiles(filePath);

            // 4. Remove from Global History (DB) — the master Tracks row.
            await _databaseService.RemoveTrackAsync(globalId);

            // 5. Remove from the Library index (LibraryEntries) — the table the Library page's
            // track list actually queries. Distinct from step 4's Tracks row; both must go or the
            // track keeps showing up in the Library view after a "successful" deletion.
            await _libraryService.RemoveTrackFromLibraryAsync(globalId);

            // 6. Remove references from every playlist that contains it. Deliberately NOT
            // UpdatePlaylistTrackStatusAndRecalculateJobsAsync(..., TrackStatus.Missing, ...) —
            // Missing means "not yet downloaded, queue it for search": marking a permanently-
            // deleted track that way left its row sitting in every playlist's list (still "there",
            // just flagged missing) AND fed straight into GhostAcquisitionOrchestrator's
            // Missing/Failed/OnHold sweep, which immediately fired off Soulseek search cascades to
            // re-acquire the very file the user just asked to delete — the app searching right
            // after "lag and hang" the user reported, and the track never actually leaving the list.
            await _libraryService.RemoveTrackFromAllPlaylistsAsync(globalId);

            // 7. Remove from Memory
            if (ctx != null)
            {
                lock (_collectionLock) _downloads.Remove(ctx);
            }
            _eventBus.Publish(new TrackRemovedEvent(globalId));
        }
    }

    /// <summary>
    /// Phase 6: Reset a track for re-download.
    /// Deletes local files and sets status back to Missing across all playlists.
    /// </summary>
    public async Task ResetTrackToPendingAsync(string globalId)
    {
        DownloadContext? ctx;
        lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);

        using (LogContext.PushProperty("TrackHash", globalId))
        {
            _logger.LogInformation("Resetting track for re-download: {Hash}", globalId);

            // 1. Cancel active download if any
            if (ctx != null)
            {
                ctx.CancellationTokenSource?.Cancel();
                lock (_collectionLock) _downloads.Remove(ctx);
            }

            // 2. Delete Physical Files from Library Entry
            var entry = await _libraryService.FindLibraryEntryAsync(globalId);
            if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
            {
                DeleteLocalFiles(entry.FilePath);
                // Also remove from library index entirely
                await _libraryService.RemoveTrackFromLibraryAsync(globalId);
            }

            // 3. Reset references in Playlists to Missing
            await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(globalId, TrackStatus.Missing, string.Empty);

            // 4. Reset global track queue state
            var track = await _databaseService.FindTrackAsync(globalId);
            if (track != null)
            {
                track.State = "Pending";
                track.Filename = string.Empty;
                track.StalledReason = null;
                await _databaseService.SaveTrackAsync(track);
            }

            // 5. Notify UI
            _eventBus.Publish(new TrackStateChangedEvent(globalId, Guid.Empty, PlaylistTrackState.Pending, DownloadFailureReason.None, "Fraud Purge / Reset"));

            _logger.LogInformation("Track {Hash} reset to Pending state. Ready for Discovery.", globalId);
        }
    }

    private void DeleteLocalFiles(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Deleted file: {Path}", path);
            }

            var partPath = path + ".part";
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
                _logger.LogInformation("Deleted partial file: {Path}", partPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file(s) for path {Path}", path);
        }
    }

    // Removed OnTrackPropertyChanged - Service no longer listens to VM property changes

    // Helper to update state and publish event (Overload: Structured Failure Reason)
    public async Task UpdateStateAsync(DownloadContext ctx, PlaylistTrackState newState, DownloadFailureReason failureReason)
    {
        // Store structured failure data
        ctx.FailureReason = failureReason;

        // Generate detailed message from enum + search attempts
        var displayMessage = failureReason.ToDisplayMessage();
        var suggestion = failureReason.ToActionableSuggestion();

        // Only append search diagnostics if the failure is search-related (i.e. we couldn't find a valid track)
        // If we passed the search phase and failed later (TransferFailed), irrelevant rejections shouldn't be shown.
        var isSearchFailure = failureReason == DownloadFailureReason.NoSearchResults ||
                              failureReason == DownloadFailureReason.AllResultsRejectedQuality ||
                              failureReason == DownloadFailureReason.AllResultsRejectedFormat ||
                              failureReason == DownloadFailureReason.AllResultsBlacklisted ||
                              failureReason == DownloadFailureReason.DiscoveryTimeout;

        // If we have search attempt logs, add the best rejection details
        if (isSearchFailure && ctx.SearchAttempts.Any())
        {
            var lastAttempt = ctx.SearchAttempts.Last();
            if (lastAttempt.Top3RejectedResults.Any())
            {
                var bestRejection = lastAttempt.Top3RejectedResults[0]; // Focus on #1
                displayMessage += $" ({bestRejection.ShortReason})";
            }
        }

        // Store detailed message for persistence
        ctx.DetailedFailureMessage = $"{displayMessage}. {suggestion}";

        // Call original method with generated error message
        await UpdateStateAsync(ctx, newState, ctx.DetailedFailureMessage);
    }

    // Helper to update state and publish event (Original: String-based)
    public async Task UpdateStateAsync(DownloadContext ctx, PlaylistTrackState newState, string? error = null, bool wasAlreadyPresent = false)
    {
        if (ctx.State == newState && ctx.ErrorMessage == error) return;

        if (newState == PlaylistTrackState.Searching && ctx.SearchStartedAt == null)
        {
            ctx.SearchStartedAt = DateTime.UtcNow;
            ctx.Model.SearchStartedAt = ctx.SearchStartedAt;
        }

        if (newState == PlaylistTrackState.Searching && ctx.IsVip)
        {
            _logger.LogInformation("🚀 VIP Track bypassing worker slot: {Title} ({TrackId})", ctx.Model.Title, ctx.GlobalId);
        }

        ctx.State = newState;
        ctx.ErrorMessage = error; // Update context
        ctx.LastStateChangeTime = DateTime.UtcNow; // Tracks when current state was entered
        _auditLogger.Log(ctx.GlobalId, $"[State] Transitioned to {newState}" + (string.IsNullOrEmpty(error) ? "" : $" | Info: {error}"));

        // Update model and timestamp for terminal states
        if (newState == PlaylistTrackState.Completed || newState == PlaylistTrackState.Failed || newState == PlaylistTrackState.Cancelled)
        {
            ctx.Model.CompletedAt = DateTime.UtcNow;

            if (newState == PlaylistTrackState.Completed)
            {
                _networkHealth.RecordTransferOutcome(null);
            }
            else if (newState == PlaylistTrackState.Cancelled)
            {
                _networkHealth.RecordTransferOutcome(DownloadFailureReason.TransferCancelled);
            }
            else
            {
                _networkHealth.RecordTransferOutcome(ctx.FailureReason ?? DownloadFailureReason.TransferFailed);
            }

            // Epic 4.1: Remove terminal tracks from the persisted queue so they
            // don't reappear after a restart as phantom pending items.
            if (ctx.Model.Id != Guid.Empty)
            {
                _ = Task.Run(async () =>
                {
                    try { await _databaseService.RemoveFromDownloadQueueAsync(ctx.Model.Id); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[QueuePersist] Failed to remove {TrackId} from download queue", ctx.Model.Id); }
                });
            }
        }

        // Publish with ProjectId for targeted updates
        // Phase 0.5: Include best search log for diagnostics
        var bestSearchLog = ctx.SearchAttempts.OrderByDescending(x => x.ResultsCount).FirstOrDefault();
        _eventBus.Publish(new TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, newState, ctx.FailureReason ?? DownloadFailureReason.None, error, bestSearchLog, ctx.CurrentUsername, wasAlreadyPresent));

        // DB Persistence (Consolidated)
        // Phase 3D: High-Efficiency Core - Master and Playlist updates now happen in a SINGLE transaction
        await SyncDbAsync(ctx);

        // Phase 6: VIP/Bypass Sync
        if (newState == PlaylistTrackState.Downloading && ctx.IsVip)
        {
             _logger.LogInformation("🚀 VIP Track Active: {Title}", ctx.Model.Title);
        }

        // Phase 6 Fix: Real-time population of "All Tracks" (LibraryEntry)
        if (newState == PlaylistTrackState.Completed && !string.IsNullOrEmpty(ctx.Model.ResolvedFilePath))
        {
            var queuedHash = string.IsNullOrWhiteSpace(ctx.Model.TrackUniqueHash)
                ? ctx.GlobalId
                : ctx.Model.TrackUniqueHash;

            await LogIngestionLifecycleAsync(
                ctx.Model.PlaylistId,
                "ingestion_queued",
                new
                {
                    trackHash = queuedHash,
                    playlistTrackId = ctx.Model.Id,
                    filePath = ctx.Model.ResolvedFilePath,
                    queuedAtUtc = DateTime.UtcNow,
                    source = "DownloadManager.UpdateStateAsync"
                });

            _eventBus.Publish(new FileIngestionQueuedEvent(
                queuedHash,
                ctx.Model.Id,
                ctx.Model.ResolvedFilePath,
                DateTime.UtcNow));

            if (ctx.Model.AvailabilityState == TrackAvailabilityState.Ghost ||
                ctx.Model.AvailabilityState == TrackAvailabilityState.QueuedForDownload ||
                ctx.Model.AvailabilityState == TrackAvailabilityState.Downloading)
            {
                ctx.Model.AvailabilityState = TrackAvailabilityState.LocalUnanalyzed;
                try
                {
                    using var dbContext = new AppDbContext();
                    var dbTrack = await dbContext.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == ctx.Model.Id);
                    if (dbTrack != null)
                    {
                        dbTrack.AvailabilityState = TrackAvailabilityState.LocalUnanalyzed;
                    }
                    var masterTrack = await dbContext.Tracks.FirstOrDefaultAsync(t => t.GlobalId == queuedHash);
                    if (masterTrack != null)
                    {
                        masterTrack.AvailabilityState = TrackAvailabilityState.LocalUnanalyzed;
                    }
                    await dbContext.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update AvailabilityState to LocalUnanalyzed in DB for {Id}", ctx.Model.Id);
                }

                // Settings → Library / setup wizard: "Analyse new downloads automatically".
                if (_config.AnalyzeNewDownloads)
                {
                    _eventBus.Publish(new TrackAnalysisRequestedEvent(
                        queuedHash,
                        AnalysisTier.Tier1,
                        IsHighPriority: true));
                }
                else
                {
                    _logger.LogDebug("Auto-analysis of new downloads is off; {Track} stays unanalysed until queued manually", ctx.Model.Title);
                }
            }

            await _libraryService.AddTrackToLibraryIndexAsync(ctx.Model, ctx.Model.ResolvedFilePath);

            // Phase 6: Reciprocal Sharing Growth
            // As new downloads land in the library/download folders, refresh the outgoing share count
            // so Soulseek peers immediately see updated availability.
            try
            {
                await _soulseek.RefreshShareStateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Share state refresh skipped/failed after completion for {Track}", ctx.Model.Title);
            }
        }
    }

    /// <summary>
    /// Consolidated DB Sync: Updates Master Record, Playlist Status, and Recalculates Job progress
    /// in a single transaction. Reduces DB overhead by 50% per state change.
    /// </summary>
    private async Task SyncDbAsync(DownloadContext ctx)
    {
        try
        {
            var dbStatus = ctx.State switch
            {
                PlaylistTrackState.Completed => TrackStatus.Downloaded,
                PlaylistTrackState.Failed => TrackStatus.Failed,
                PlaylistTrackState.Cancelled => TrackStatus.Skipped,
                _ => ctx.Model.Status
            };

            var updatedJobIds = await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(
                ctx.GlobalId,
                dbStatus,
                ctx.Model.ResolvedFilePath,
                ctx.Model.SearchRetryCount,
                ctx.Model.NotFoundRestartCount,
                state: ctx.State.ToString(),
                error: ctx.ErrorMessage,
                completedAt: ctx.Model.CompletedAt,
                stalledReason: ctx.Model.StalledReason
            );

            // Notify UI to refresh Project Headers
            if (updatedJobIds.Any())
            {
                foreach (var jobId in updatedJobIds)
                {
                    _eventBus.Publish(new ProjectUpdatedEvent(jobId));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync DB record for {Id}", ctx.GlobalId);
        }
    }



    /// <summary>
    /// Phase 2.5: Enhanced pause with immediate cancellation and IsUserPaused tracking.
    /// </summary>
    public void PromoteTrackToExpress(string globalId)
    {
        DownloadContext? ctx;
        lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);

        if (ctx != null)
        {
            ctx.Model.Priority = 0;
            _logger.LogInformation("Creating VIP Pass for {Title} (Priority 0)", ctx.Model.Title);
            // In a real implementation, we would persist this to PlaylistTrackEntity here.
        }
    }

    public async Task PauseTrackAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);

        if (ctx == null)
        {
            _logger.LogWarning("Cannot pause track {Id}: not found", globalId);
            return;
        }

        // CRITICAL: Cancel the CancellationTokenSource immediately
        // This ensures the download stops mid-transfer and preserves the .part file
        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource = new CancellationTokenSource(); // Reset for resume

        await UpdateStateAsync(ctx, PlaylistTrackState.Paused);

        // Mark as user-paused in DB so hydration knows not to auto-resume
        try
        {
            var job = await _libraryService.FindPlaylistJobAsync(ctx.Model.PlaylistId);
            if (job != null)
            {
                job.IsUserPaused = true;
                // Update via LibraryService (uses Save internally)
                await _libraryService.SavePlaylistJobAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark job as user-paused in DB (non-fatal)");
        }

        _logger.LogInformation("â¸ï¸ Paused track: {Artist} - {Title} (user-initiated)", ctx.Model.Artist, ctx.Model.Title);
    }

    /// <summary>
    /// Pauses all active downloads.
    /// </summary>
    public async Task PauseAllAsync()
    {
        List<DownloadContext> active;
        lock (_collectionLock)
        {
             active = _downloads.Where(d => d.IsActive).ToList();
        }

        if (active.Any())
        {
            _logger.LogInformation("⏸ Pausing all {Count} active downloads...", active.Count);
            foreach(var d in active)
            {
                 await PauseTrackAsync(d.GlobalId);
            }
        }
    }

    /// <summary>
    /// Resumes all paused downloads and ensures engine is running.
    /// </summary>
    public async Task ResumeAllAsync()
    {
        if (!IsRunning)
        {
            await StartAsync();
        }

        List<DownloadContext> paused;
        lock (_collectionLock)
        {
            paused = _downloads.Where(d => d.State == PlaylistTrackState.Paused).ToList();
        }

        if (paused.Any())
        {
            _logger.LogInformation("▶ Resuming all {Count} paused downloads...", paused.Count);
            foreach (var d in paused)
            {
                await ResumeTrackAsync(d.GlobalId);
            }
        }

        IsPaused = false; // Globally unpause if it was
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping DownloadManager engine (soft stop)...");
        var stopLogger = _logger;
        _ = PauseAllAsync().ContinueWith(
            t => stopLogger.LogWarning(t.Exception, "PauseAll during StopAsync failed non-fatally"),
            TaskContinuationOptions.OnlyOnFaulted); // fire-and-forget pause of active tracks
        _globalCts.Cancel();
        try
        {
            if (_processingTask != null)
                await _processingTask;
        }
        catch (OperationCanceledException)
        {
            // intentional: stopping the processing loop is expected to be cancelled
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while stopping processing loop");
        }

        _logger.LogInformation("DownloadManager engine stopped.");
        OnPropertyChanged(nameof(IsRunning));
    }

    public async Task TogglePauseEngineAsync()
    {
        IsPaused = !IsPaused;
        _logger.LogInformation("DownloadManager engine {Status}", IsPaused ? "PAUSED" : "RESUMED");
    }

    public async Task ResumeTrackAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        if (ctx != null)
        {
            _ = UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        }
    }


    /// <summary>
    /// Updates the display name stored on every in-memory download context that belongs to
    /// <paramref name="playlistId"/>.  Call this after renaming a playlist so the Download
    /// Center reflects the new title without a restart.
    /// </summary>
    public void UpdatePlaylistSourceName(Guid playlistId, string newName)
    {
        List<DownloadContext> affected;
        lock (_collectionLock)
            affected = _downloads
                .Where(d => d.Model.PlaylistId == playlistId || d.Model.SourcePlaylistId == playlistId)
                .ToList();

        foreach (var ctx in affected)
            ctx.Model.SourcePlaylistName = newName;
    }

    /// <summary>
    /// Returns the current SourcePlaylistName for the first track belonging to
    /// <paramref name="playlistId"/>, or null if no tracks are registered.
    /// </summary>
    public string? GetPlaylistSourceName(Guid playlistId)
    {
        lock (_collectionLock)
            return _downloads
                .FirstOrDefault(d => d.Model.PlaylistId == playlistId || d.Model.SourcePlaylistId == playlistId)
                ?.Model.SourcePlaylistName;
    }

    public void CancelTrack(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        if (ctx == null) return;

        _logger.LogInformation("Cancelling track: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);

        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource = new CancellationTokenSource(); // Reset

        _ = UpdateStateAsync(ctx, PlaylistTrackState.Cancelled);

        // Delete the final file + its .part sibling (handles the case where ResolvedFilePath is set)
        DeleteLocalFiles(ctx.Model.ResolvedFilePath);

        // Also delete the .part file at the download staging path, which is where the
        // download loop writes bytes BEFORE the atomic rename to the final path.
        // This path may be set even when ResolvedFilePath is still null (mid-download cancellation).
        try
        {
            // The real .part path (#328): never rebuilt from tags, which pointed nowhere and created empty folders.
            foreach (var deleted in Downloads.PartFileGuard.DeletePartFiles(ctx.PartFilePath, ctx.Model.ResolvedFilePath, StagingPartPathFor(ctx)))
                _logger.LogInformation("Deleted staging .part file on cancel: {Path}", deleted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete staging .part file for cancelled track {Title}", ctx.Model.Title);
        }
    }


    /// <summary>
    /// Phase 3B: Health Monitor Intervention
    /// Cancels a stalled download, blacklists the peer, and re-queues it for discovery.
    /// Non-destructive: Does NOT delete the .part file (optimistic resume if new peer has same file).
    /// </summary>
    public async Task AutoRetryStalledDownloadAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);

        if (ctx == null) return;

        var stalledUser = ctx.CurrentUsername;
        if (!string.IsNullOrEmpty(stalledUser))
        {
            ctx.BlacklistUser(stalledUser);
            _logger.LogWarning("⚠️ Health Monitor: Blacklisting peer {User} for {Track}", stalledUser, ctx.Model.Title);

            // Record failure/stall status to deprioritize bad peers in future searches
            _peerReliability.RecordDownloadFailed(stalledUser, stalled: true);

            // Notify UI
            _eventBus.Publish(new NotificationEvent(
                "Auto-Retry Triggered",
                $"Stalled download '{ctx.Model.Title}' switched from peer {stalledUser}",
                NotificationType.Warning));
        }

        // 1. Cancel active transfer (stops Soulseek)
        ctx.CancellationTokenSource?.Cancel();

        // 2. IMPORTANT: Don't delete files! We want to try to resume from another peer if possible.
        // Wait, Soulseek resume requires same file hash. DiscoveryService might find a different file hash.
        // If different file hash, Resume logic (based on file size match?) might be risky.
        // DownloadFileAsync logic checks .part file size.
        // If new file is different size, it might think it's truncated or ghost.
        // Safe bet: For now, we trust the resume logic to handle mismatches (it checks sizes).

        // 3. Reset state to Pending so ProcessQueueLoop picks it up
        await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Auto-retrying after stall");

        // Reset CTS for next attempt
        ctx.CancellationTokenSource = new CancellationTokenSource();
    }

    public async Task ForceStartTrack(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);

        if (ctx == null) return;

        _logger.LogInformation("🚀 Force Start (VIP) triggered for {Title}", ctx.Model.Title);

        // Mark as VIP, bump priority, and clear any slot/time gate so user intent is immediate.
        ctx.IsVip = true;
        ctx.Model.Priority = 0;
        ctx.RetryAfterSlot = null;
        ctx.NextRetryTime = null;

        if (ctx.State != PlaylistTrackState.Downloading && ctx.State != PlaylistTrackState.Searching)
        {
            await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        }
    }

    /// <summary>
    /// Batched analog of <see cref="ForceStartTrack"/> for group actions (the playlist card's
    /// "VIP Start Group" button). The single-track path goes through <see cref="UpdateStateAsync"/>
    /// → <see cref="SyncDbAsync"/>, which acquires TrackRepository's app-wide static write
    /// semaphore, opens a new DbContext, and commits — once PER TRACK. A large playlist's worth of
    /// pending tracks fired through that path in a tight loop (which is what the group command
    /// used to do — literally invoking each track's own ForceStartCommand one after another)
    /// serializes hundreds of round-trips through that one semaphore, which also blocks the
    /// background download engine's own routine state writes for the whole duration — this is the
    /// concrete cause of "clicking VIP Start freezes the UI" for anything but a small playlist.
    /// This does the same in-memory work per track (cheap — no DB, no lock contention) but only
    /// one DB round-trip for the entire batch via <see cref="TrackRepository.BulkUpdatePlaylistTrackStatusAsync"/>.
    /// </summary>
    public async Task ForceStartTracksAsync(IEnumerable<string> globalIds)
    {
        var affected = new List<DownloadContext>();
        lock (_collectionLock)
        {
            foreach (var globalId in globalIds)
            {
                var ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
                if (ctx == null) continue;

                ctx.IsVip = true;
                ctx.Model.Priority = 0;
                ctx.RetryAfterSlot = null;
                ctx.NextRetryTime = null;

                if (ctx.State != PlaylistTrackState.Downloading && ctx.State != PlaylistTrackState.Searching)
                {
                    affected.Add(ctx);
                }
            }
        }

        if (affected.Count == 0) return;

        _logger.LogInformation("🚀 Force Start (VIP) batch triggered for {Count} track(s)", affected.Count);

        var now = DateTime.UtcNow;
        foreach (var ctx in affected)
        {
            // Same in-memory bookkeeping UpdateStateAsync does for a non-terminal transition —
            // cheap, no DB/lock contention, safe to do per-track in a loop.
            ctx.State = PlaylistTrackState.Pending;
            ctx.ErrorMessage = null;
            ctx.LastStateChangeTime = now;
            _auditLogger.Log(ctx.GlobalId, "[State] Transitioned to Pending (VIP batch start)");
            _eventBus.Publish(new TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, PlaylistTrackState.Pending));
        }

        // One DB round-trip for the whole batch instead of one per track. TrackStatus (the coarse
        // DB enum) doesn't need to change for this transition — see BulkUpdatePlaylistTrackStatusAsync's
        // doc comment — only the master track's diagnostic State string does.
        var hashes = affected.Select(c => c.GlobalId).Distinct().ToList();
        await _databaseService.BulkUpdatePlaylistTrackStatusAsync(hashes, newStatus: null, state: nameof(PlaylistTrackState.Pending));
    }

    /// <summary>
    /// Batched analog of <see cref="PauseTrackAsync"/> for the playlist card's group "Pause"
    /// button — same freeze risk as <see cref="ForceStartTracksAsync"/> (one DB round-trip per
    /// track via <see cref="UpdateStateAsync"/> in a loop), plus the single-track path made it
    /// worse by re-fetching and re-saving the WHOLE PlaylistJob once per track just to flip its
    /// IsUserPaused flag, even though every track in a group action shares the same job. That
    /// job-level write now happens once per distinct job instead of once per track.
    /// </summary>
    public async Task PauseTracksAsync(IEnumerable<string> globalIds)
    {
        var affected = new List<DownloadContext>();
        lock (_collectionLock)
        {
            foreach (var globalId in globalIds)
            {
                var ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
                if (ctx == null) continue;
                ctx.CancellationTokenSource?.Cancel();
                ctx.CancellationTokenSource = new CancellationTokenSource();
                affected.Add(ctx);
            }
        }
        if (affected.Count == 0) return;

        _logger.LogInformation("⏸️ Pause batch triggered for {Count} track(s)", affected.Count);

        var now = DateTime.UtcNow;
        foreach (var ctx in affected)
        {
            ctx.State = PlaylistTrackState.Paused;
            ctx.ErrorMessage = null;
            ctx.LastStateChangeTime = now;
            _auditLogger.Log(ctx.GlobalId, "[State] Transitioned to Paused (batch pause)");
            _eventBus.Publish(new TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, PlaylistTrackState.Paused));
        }

        var hashes = affected.Select(c => c.GlobalId).Distinct().ToList();
        await _databaseService.BulkUpdatePlaylistTrackStatusAsync(hashes, newStatus: null, state: nameof(PlaylistTrackState.Paused));

        // Mark every distinct job these tracks belong to as user-paused, once per job rather than
        // once per track — the single-track PauseTrackAsync re-fetches and re-saves the whole
        // PlaylistJob for this alone, which is the same round-trip-per-track problem for a group
        // of tracks that (in the common case) all belong to the very same playlist.
        var distinctPlaylistIds = affected.Select(c => c.Model.PlaylistId).Distinct().ToList();
        foreach (var playlistId in distinctPlaylistIds)
        {
            try
            {
                var job = await _libraryService.FindPlaylistJobAsync(playlistId);
                if (job != null && !job.IsUserPaused)
                {
                    job.IsUserPaused = true;
                    await _libraryService.SavePlaylistJobAsync(job);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark job {PlaylistId} as user-paused in DB (non-fatal)", playlistId);
            }
        }
    }

    /// <summary>
    /// Batched analog of <see cref="ResumeTrackAsync"/> for the playlist card's group "Resume"
    /// button's Paused-track branch — same one-DB-round-trip-per-track problem as
    /// <see cref="ForceStartTracksAsync"/>.
    /// </summary>
    public async Task ResumePausedTracksAsync(IEnumerable<string> globalIds)
    {
        var affected = new List<DownloadContext>();
        lock (_collectionLock)
        {
            foreach (var globalId in globalIds)
            {
                var ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
                if (ctx != null) affected.Add(ctx);
            }
        }
        if (affected.Count == 0) return;

        _logger.LogInformation("▶️ Resume (Paused) batch triggered for {Count} track(s)", affected.Count);

        var now = DateTime.UtcNow;
        foreach (var ctx in affected)
        {
            ctx.State = PlaylistTrackState.Pending;
            ctx.ErrorMessage = null;
            ctx.LastStateChangeTime = now;
            _auditLogger.Log(ctx.GlobalId, "[State] Transitioned to Pending (batch resume)");
            _eventBus.Publish(new TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, PlaylistTrackState.Pending));
        }

        var hashes = affected.Select(c => c.GlobalId).Distinct().ToList();
        await _databaseService.BulkUpdatePlaylistTrackStatusAsync(hashes, newStatus: null, state: nameof(PlaylistTrackState.Pending));
    }

    /// <summary>
    /// Batched analog of <see cref="HardRetryTrack"/> for the playlist card's group "Resume"
    /// button's Failed-track branch. Per-track file cleanup and in-memory context resets stay in a
    /// loop (local, cheap — no lock contention), but the two DB writes HardRetryTrack does per
    /// track (status/state via UpdateStateAsync, then a second explicit IsUserPaused write) and
    /// the RefillQueueAsync scan it fires per track are each collapsed to one call for the whole
    /// batch.
    /// </summary>
    public async Task HardRetryTracksAsync(IEnumerable<string> globalIds)
    {
        var affected = new List<DownloadContext>();
        lock (_collectionLock)
        {
            foreach (var globalId in globalIds)
            {
                var ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
                if (ctx != null) affected.Add(ctx);
            }
        }
        if (affected.Count == 0) return;

        _logger.LogInformation("🔄 Hard Retry batch triggered for {Count} track(s)", affected.Count);

        var now = DateTime.UtcNow;
        foreach (var ctx in affected)
        {
            ctx.CancellationTokenSource?.Cancel();
            ctx.CancellationTokenSource = new CancellationTokenSource();
            ctx.RetryCount = 0;
            ctx.NextRetryTime = null;
            ctx.FailureReason = null;
            ctx.ErrorMessage = null;
            ctx.Model.IsUserPaused = false;
            ctx.Model.Status = TrackStatus.Pending;
            ctx.Model.CompletedAt = null;
            ctx.Model.StalledReason = null;
            ctx.Model.Priority = 0;
            ctx.IsFinalizing = false;
            ctx.SearchAttempts.Clear();
            ctx.ClearBlacklistedUsers();

            try
            {
                Downloads.PartFileGuard.DeletePartFiles(ctx.PartFilePath, ctx.Model.ResolvedFilePath, StagingPartPathFor(ctx)); // the real .part (#328)
                ctx.PartFilePath = null;
                if (!string.IsNullOrEmpty(ctx.Model.ResolvedFilePath) && File.Exists(ctx.Model.ResolvedFilePath))
                {
                    File.Delete(ctx.Model.ResolvedFilePath);
                    ctx.Model.ResolvedFilePath = string.Empty;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up files during batch hard retry for {Title}", ctx.Model.Title);
            }

            ctx.State = PlaylistTrackState.Pending;
            ctx.LastStateChangeTime = now;
            _auditLogger.Log(ctx.GlobalId, "[State] Transitioned to Pending (batch hard retry)");
            _eventBus.Publish(new TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, PlaylistTrackState.Pending));
        }

        var hashes = affected.Select(c => c.GlobalId).Distinct().ToList();
        await _databaseService.BulkUpdatePlaylistTrackStatusAsync(
            hashes, newStatus: TrackStatus.Pending, state: nameof(PlaylistTrackState.Pending),
            isUserPaused: false, clearRetryState: true, priority: 0);

        _ = RefillQueueAsync();
    }

    public async Task ForceDownloadIgnoreGuardsAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);

        if (ctx == null) return;

        _logger.LogInformation("🛡️ Bypassing Guards for {Title}. Force downloading ignoring quality filters.", ctx.Model.Title);

        ctx.Model.IgnoreSafetyGuards = true;
        ctx.IsVip = true;
        ctx.Model.Priority = 0;

        await UpdateStateAsync(ctx, PlaylistTrackState.Pending);

        // Reset failures and logs to give it a fresh start
        ctx.ErrorMessage = null;
        ctx.SearchAttempts.Clear();

        // Keep strict slot accounting: queue loop starts this within configured concurrency.
    }

    public async Task ForceDownloadSpecificCandidateAsync(string globalId, string username, string filename, int? bitrate = null, string? format = null)
    {
        if (string.IsNullOrWhiteSpace(globalId) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(filename))
            return;

        DownloadContext? ctx;
        lock (_collectionLock)
        {
            ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        }

        if (ctx == null)
            return;

        _logger.LogInformation("🎛️ Manual force download for {Title}: @{User} -> {File}", ctx.Model.Title, username, filename);

        ctx.Model.IgnoreSafetyGuards = true;
        ctx.IsVip = true;
        ctx.Model.Priority = 0;
        ctx.ErrorMessage = null;
        ctx.SearchAttempts.Clear();

        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource?.Dispose();
        ctx.CancellationTokenSource = new CancellationTokenSource();

        if (ctx.ActiveTransferKey is { } cancelKey)
            _activeTransfers.Remove(cancelKey);

        var resolvedFormat = !string.IsNullOrWhiteSpace(format)
            ? format.Trim().TrimStart('.').ToLowerInvariant()
            : Path.GetExtension(filename)?.TrimStart('.').ToLowerInvariant();

        var candidate = new Track
        {
            Artist = ctx.Model.Artist,
            Title = ctx.Model.Title,
            Album = ctx.Model.Album,
            Username = username,
            Filename = filename,
            Bitrate = bitrate ?? (ctx.Model.Bitrate ?? 0),
            Format = string.IsNullOrWhiteSpace(resolvedFormat) ? null : resolvedFormat,
            MatchReason = "🛠 Manual force from row details"
        };

        _eventBus.Publish(new Events.TrackDetailedStatusEvent(globalId,
            $"🛠 Manual force: starting direct download from {username}.",
            false,
            ctx.CorrelationId));

        await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Manual force candidate selected");

        // Keep strict slot accounting: queue loop starts this within configured concurrency.
        // Preserve manual candidate intent by recording it for the next discovery/download cycle.
        ctx.OverrideCandidate = candidate;
    }

    public async Task UpdateTrackFiltersAsync(string globalId, string formats, int minBitrate)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);

        if (ctx != null)
        {
            ctx.Model.PreferredFormats = formats;
            ctx.Model.MinBitrateOverride = minBitrate;

            // Persist to DB immediately
            await SyncDbAsync(ctx);

            // If it's a playlist track, update that entity too
            try
            {
                using var context = new Data.AppDbContext();
                var pt = await context.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == ctx.Model.Id);
                if (pt != null)
                {
                    pt.PreferredFormats = formats;
                    pt.MinBitrateOverride = minBitrate;
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update PlaylistTrack filters in DB for {Id}", globalId);
            }
        }
    }

    public bool EnqueueTrack(Track track, int priority = 0)
    {
        if (!TryValidateTrackForQueue(track, out var reason))
        {
            _logger.LogWarning("Skipping queue add for track {Artist} - {Title}: {Reason}", track.Artist, track.Title, reason);
            // IsActive: false here meant ApplyGlobalStatusEvent's own "!evt.IsActive -> clear" guard
            // discarded this message immediately — the rejection reason was computed and published,
            // then silently swallowed before ever reaching the banner. IsActive marks whether the
            // banner should show at all, not whether the underlying condition is ongoing.
            _eventBus.Publish(new GlobalStatusEvent($"Queue rejected: {reason}", IsActive: true, IsError: true));
            return false;
        }

        var playlistTrack = new PlaylistTrack
        {
             Id = Guid.NewGuid(),
             Artist = track.Artist ?? "Unknown",
             Title = track.Title ?? "Unknown",
             Album = track.Album ?? "Unknown",
             Status = TrackStatus.Missing,
             Priority = priority,
             ResolvedFilePath = _pathProvider.GetTrackPath(track),
             TrackUniqueHash = track.UniqueHash
        };

        QueueTracks(new List<PlaylistTrack> { playlistTrack });
        return true;
    }

    private bool TryValidateTrackForQueue(Track track, out string reason, int? minBitrateOverride = null, bool allowFallbackFormat = false)
    {
        reason = string.Empty;

        var preferredFormats = _config.PreferredFormats?
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim().ToLowerInvariant())
            .ToHashSet() ?? new HashSet<string>();

        var extension = (track.Format ?? track.GetExtension())?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
        var allowMp3FormatFallback = allowFallbackFormat &&
                                     _config.EnableMp3Fallback &&
                                     string.Equals(extension, "mp3", StringComparison.OrdinalIgnoreCase);

        if (preferredFormats.Count > 0 && !string.IsNullOrEmpty(extension) && !preferredFormats.Contains(extension) && !allowMp3FormatFallback)
        {
            reason = $"format '{extension}' not allowed";
            return false;
        }

        var requiredMinBitrate = minBitrateOverride ?? _config.PreferredMinBitrate;
        if (requiredMinBitrate > 0 && track.Bitrate > 0 && track.Bitrate < requiredMinBitrate)
        {
            reason = $"bitrate {track.Bitrate}kbps below required {requiredMinBitrate}kbps";
            return false;
        }

        if (_config.PreferredMaxSampleRate > 0 && track.SampleRate.HasValue && track.SampleRate.Value > _config.PreferredMaxSampleRate)
        {
            reason = $"sample-rate {track.SampleRate.Value}Hz exceeds {_config.PreferredMaxSampleRate}Hz";
            return false;
        }

        return true;
    }

    private bool IsMp3FallbackAllowed(PlaylistTrack track)
    {
        if (_config.EnableMp3Fallback)
            return true;

        // Legacy profile compatibility path
        var formats = _config.PreferredFormats ?? new List<string>();

        return formats.Any(f => string.Equals(f?.Trim(), "mp3", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns false when <paramref name="globalId"/> isn't currently tracked as an active/recent
    /// download (typically because it already reached a terminal "completed" state and was
    /// dropped from the in-memory working set) — this method resets in-memory download context
    /// state, so there's nothing for it to do for a track outside that set. Callers that need
    /// "redownload this specific already-completed track" (e.g. bad/truncated content) must
    /// handle a false return by re-queuing it themselves — see
    /// TrackOperationsViewModel.ExecuteHardRetry for the single-track equivalent of
    /// LibraryViewModel.Commands.ExecuteForceRedownloadAsync's reset-and-requeue recipe.
    /// </summary>
    public async Task<bool> HardRetryTrack(string globalId)
    {
        var ctx = FindContextByGlobalId(globalId);

        if (ctx == null) return false;

        _logger.LogInformation("🔄 Hard Retry triggered for {Title}", ctx.Model.Title);

        // 1. Reset State
        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource = new CancellationTokenSource();
        ctx.RetryCount = 0;
        ctx.NextRetryTime = null;
        ctx.FailureReason = null; // Clear error message
        ctx.ErrorMessage = null;
        ctx.Model.IsUserPaused = false; // Reset user pause
        ctx.Model.Status = TrackStatus.Pending;
        ctx.Model.CompletedAt = null;
        ctx.Model.StalledReason = null;
        ctx.IsFinalizing = false; // Reset critical flags
        ctx.SearchAttempts.Clear(); // Clear bad search history

        ctx.ClearBlacklistedUsers(); // Give peers a second chance

        // 2. Clean Disk (Nuanced: Only delete .part if it's failed/stalled, keep .mp3 if it was somehow completed?)
        // Actually "Hard Retry" implies "Delete everything and start over".
        try
        {
            // The real .part path (#328). The tag-built path used before never existed, so Hard Retry deleted
            // nothing and the next attempt resumed the old partial file from a different peer.
            foreach (var deleted in Downloads.PartFileGuard.DeletePartFiles(ctx.PartFilePath, ctx.Model.ResolvedFilePath, StagingPartPathFor(ctx)))
                _logger.LogInformation("Deleted stale .part file for retry: {Path}", deleted);
            ctx.PartFilePath = null;

            // If we have a resolved file path that is invalid, nuke it
            if (!string.IsNullOrEmpty(ctx.Model.ResolvedFilePath) && File.Exists(ctx.Model.ResolvedFilePath))
            {
                 // Check if it's actually valid? No, hard retry = force redownload.
                 File.Delete(ctx.Model.ResolvedFilePath);
                 ctx.Model.ResolvedFilePath = string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean up files during hard retry for {Title}. reason={Reason} correlationId={CorrelationId}",
                ctx.Model.Title,
                ex.Message,
                ctx.CorrelationId ?? "-");
        }

        // 3. Reset to Pending to trigger immediate pickup
        ctx.Model.Priority = 0; // VIP Pass
        await UpdateStateAsync(ctx, PlaylistTrackState.Pending);

        // 4. Force immediate queue refill and check
        _ = RefillQueueAsync();

        // 5. Force save to DB to ensure 'IsUserPaused=false' and Priority persists
        await _databaseService.UpdatePlaylistTrackUserPausedAsync(ctx.Model.Id, false);
        // We'll trust the SaveTrackToDb call within UpdateStateAsync to persist the Priority,
        // or explicitly save here if needed.

        return true;
    }

}
