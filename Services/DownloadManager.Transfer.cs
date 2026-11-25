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
/// Peer file transfer execution, hedge failover, progress/completion callbacks, and post-download verification (#307).
/// </summary>
public partial class DownloadManager
{
    private async Task DownloadFileAsync(DownloadContext ctx, Track bestMatch, CancellationToken ct)
    {
        await UpdateStateAsync(ctx, PlaylistTrackState.Downloading);
        _peerReliability.RecordDownloadStarted(bestMatch.Username);

        static string FormatBytes(long value) => SLSKDONET.Utils.NumFormat.Size(value);

        _eventBus.Publish(new Events.TrackDetailedStatusEvent(
            ctx.GlobalId,
            $"📥 Transfer started | user:{bestMatch.Username} | file:{bestMatch.Filename} | bitrate:{bestMatch.Bitrate}kbps | format:{bestMatch.Format ?? "unknown"}",
            false,
            ctx.CorrelationId));

        ctx.Model.Bitrate = bestMatch.Bitrate;
        if (!string.IsNullOrWhiteSpace(bestMatch.Filename))
        {
            var extension = Path.GetExtension(bestMatch.Filename);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                ctx.Model.Format = extension.TrimStart('.').ToLowerInvariant();
            }
        }
        ctx.Model.QualityDetails = $"{bestMatch.Bitrate}kbps|{bestMatch.BitDepth ?? 0}bit|{bestMatch.SampleRate ?? 0}Hz";
        ctx.Model.DiscoveryReason = ResolveDiscoveryReason(
            ctx.Model.SourceProvenance,
            bestMatch.MatchReason,
            bestMatch.ScoreBreakdown);

        PrimePipelineSearchForNextTrack(ctx.GlobalId);

        // Phase 3B: Track current peer for Health Monitor blacklisting
        ctx.CurrentUsername = bestMatch.Username;

        // Issue #233: registration happens only after the peer gate is acquired (see below),
        // so a track that is merely queued for a busy peer is never in the active registry.

        // Phase 0.3: Reset Health Metrics for new attempt
        ctx.StallCount = 0;
        ctx.CurrentSpeed = 0;

        // Phase 2.5: Use PathProviderService for consistent folder structure
        // Create a temporary track object to combine DB (enriched) metadata with search result (technical) info
        var namingTrack = new Track
        {
            Artist = ctx.Model.Artist,
            Title = ctx.Model.Title,
            Album = ctx.Model.Album,
            Bitrate = bestMatch.Bitrate,
            BPM = ctx.Model.BPM,
            MusicalKey = ctx.Model.MusicalKey,
            Energy = ctx.Model.Energy,
            Filename = bestMatch.Filename, // For {filename} variable
            Username = bestMatch.Username, // For {user} variable
            Length = ctx.Model.CanonicalDuration // For {length} variable
        };
        var templatePath = _pathProvider.GetTrackPath(namingTrack);
        // LF3 (#345): Inbox mode lands the verified download in Inbox/ for review instead of the
        // template path; "File to library" re-files it later via the single move path (LF2).
        var finalPath = _config.DownloadCommitMode == "Inbox"
            ? Path.Combine(DownloadDirectory ?? "Downloads", "Inbox", Path.GetFileName(templatePath))
            : templatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        ctx.Model.ResolvedFilePath = finalPath; // Set early so UI can probe the path (will look for .part)

        // LF3 (#345): download the .part into the hidden staging folder, then commit to the final
        // path after verification — the library tree never sees a half-written file.
        var downloadDir = DownloadDirectory ?? "Downloads";
        var partPath = DownloadStagingPath.GetPartPath(downloadDir, ctx.Model.TrackUniqueHash ?? ctx.GlobalId);
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        ctx.PartFilePath = partPath; // the one real .part path, for every cleanup path (#328)
        long startPosition = 0;

        // STEP 1: Check if final file already exists and is complete
        if (File.Exists(finalPath))
        {
            var existingFileInfo = new FileInfo(finalPath);
            if (existingFileInfo.Length == bestMatch.Size)
            {
                _logger.LogInformation("File already exists and is complete: {Path}", finalPath);
                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed, wasAlreadyPresent: true);
                return;
            }
            else
            {
                // File exists but is incomplete (corrupted?) - delete and restart
                _logger.LogWarning("Final file exists but size mismatch (expected {Expected}, got {Actual}). Deleting and restarting.",
                    bestMatch.Size, existingFileInfo.Length);
                File.Delete(finalPath);
            }
        }

        // STEP 2: Check for existing .part file to resume — only if it provably came from THIS source (#328).
        // The name template often doesn't include the peer, so another peer's partial file can sit at this
        // exact path; continuing it would glue two different recordings together.
        if (File.Exists(partPath))
        {
            var journalState = await _crashJournal.GetDownloadStateAsync(ctx.GlobalId);
            var decision = Downloads.PartFileGuard.DecideResume(
                journalState?.SoulseekUsername, journalState?.SoulseekFilename, journalState?.ExpectedSize ?? 0,
                journalState?.BytesDownloaded ?? 0, bestMatch.Username ?? string.Empty, bestMatch.Filename ?? string.Empty, bestMatch.Size ?? 0);
            if (!decision.Resume)
            {
                _logger.LogWarning("Not resuming the partial file for {Track}: {Reason}. Deleting it and starting from 0.",
                    ctx.Model.Title, decision.Reason);
                File.Delete(partPath);
            }
        }

        if (File.Exists(partPath))
        {
            var diskBytes = new FileInfo(partPath).Length;

            // Phase 3A: Atomic Handshake - Trust Journal, Truncate Disk
            var confirmedBytes = await _crashJournal.GetConfirmedBytesAsync(ctx.GlobalId);
            long expectedSize = bestMatch.Size ?? 0;

            // Fix: Ghost File Race Condition Check
            // If file is fully downloaded on disk but journal says 99% (crash during finalization),
            // TRUST THE DISK. Do not truncate. Verification step will validate integrity.
            if (expectedSize > 0 && diskBytes >= expectedSize)
            {
                startPosition = diskBytes;
                _logger.LogInformation("ðŸ‘» Ghost File Detected: Disk ({Disk}) >= Expected ({Expected}). Skipping truncation despite Journal ({Journal}).",
                    diskBytes, expectedSize, confirmedBytes);
            }
            else if (confirmedBytes > 0 && diskBytes > confirmedBytes)
            {
                // Case 1: Disk has more data than journal (unconfirmed tail)
                // Truncate to confirmed bytes to ensure no corrupt/torn data is kept
                _logger.LogWarning("âš ï¸ Atomic Resume: Truncating {Diff} bytes of unconfirmed data for {Track}",
                    diskBytes - confirmedBytes, ctx.Model.Title);

                await using (var fs = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    fs.SetLength(confirmedBytes);
                    await fs.FlushAsync();
                }
                startPosition = confirmedBytes;
            }
            else
            {
                // Case 2: Disk <= Journal, or no Journal entry (clean shutdown/new)
                // Resume from what we physically have
                startPosition = diskBytes;
            }

            ctx.IsResuming = true;
            ctx.BytesReceived = startPosition;

            _logger.LogInformation("Resuming download from byte {Position} for {Track} (Journal Confirmed: {Confirmed})",
                startPosition, ctx.Model.Title, confirmedBytes);

            _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                ctx.GlobalId,
                $"♻️ Resuming transfer | user:{bestMatch.Username} | file:{bestMatch.Filename} | offset:{FormatBytes(startPosition)}",
                false,
                ctx.CorrelationId));
        }
        else
        {
            ctx.IsResuming = false;
            ctx.BytesReceived = 0;
        }

        // STEP 3: Set total bytes for progress tracking
        ctx.TotalBytes = bestMatch.Size ?? 0;  // Handle nullable size

        // Phase 2A: CHECKPOINT LOGGING - Log before download starts
        var checkpointState = new DownloadCheckpointState
        {
            TrackGlobalId = ctx.GlobalId,
            Artist = ctx.Model.Artist,
            Title = ctx.Model.Title,
            SoulseekUsername = bestMatch.Username!,
            SoulseekFilename = bestMatch.Filename!,
            ExpectedSize = bestMatch.Size ?? 0,
            PartFilePath = partPath,
            FinalPath = finalPath,
            BytesDownloaded = startPosition // Start with existing progress if resuming
        };

        var checkpoint = new RecoveryCheckpoint
        {
            Id = ctx.GlobalId, // CRITICAL: Use TrackGlobalId to prevent duplicates on retry
            OperationType = OperationType.Download,
            TargetPath = finalPath,
            StateJson = JsonSerializer.Serialize(checkpointState),
            Priority = 10 // High priority - active user download
        };

        string? checkpointId = await _crashJournal.LogCheckpointAsync(checkpoint);
        _logger.LogDebug("âœ… Download checkpoint logged: {Id} - {Artist} - {Title}",
            checkpointId, ctx.Model.Artist, ctx.Model.Title);

        // Phase 2A: PERIODIC HEARTBEAT with stall detection
        var heartbeatIntervalSeconds = Math.Max(1, Math.Min(5, _config.StallTimeoutSeconds));
        var stallThresholdTicks = Math.Max(1, (int)Math.Ceiling(_config.StallTimeoutSeconds / (double)heartbeatIntervalSeconds));
        var throughputFloorBytesPerSecond = Math.Max(1, _config.MinThroughputFloorKbps) * 1024.0;

        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(heartbeatIntervalSeconds));
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stallMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        int stallCount = 0;
        long lastHeartbeatBytes = startPosition;
        long lastThroughputBytes = startPosition;
        bool stalledByMonitor = false;
        bool journalWarned = false;

        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (await heartbeatTimer.WaitForNextTickAsync(heartbeatCts.Token))
                {
                    // Phase 3A: Finalization Guard - Stop heartbeat immediately if completion logic started
                    if (ctx.IsFinalizing) return;

                    var currentBytes = ctx.BytesReceived; // Thread-safe Interlocked read

                    // STALL DETECTION: throughput below floor for configured timeout window.
                    // CRITICAL: Skip stall detection entirely when the download is QUEUED.
                    // Queued = we are waiting in the peer's upload queue. Zero throughput is
                    // 100% expected and correct. The SoulseekAdapter handles zombie queue detection
                    // via Queue Velocity (stagnation window). Do NOT abort here.
                    if (ctx.State == PlaylistTrackState.Queued)
                    {
                        // Reset stall counter so there is no accumulated stall debt when
                        // the peer eventually starts transferring.
                        stallCount = 0;
                        lastThroughputBytes = currentBytes;
                    }
                    else
                    {
                        var bytesDelta = currentBytes - lastThroughputBytes;
                        var minimumBytesExpected = throughputFloorBytesPerSecond * heartbeatIntervalSeconds;

                        if (bytesDelta < minimumBytesExpected)
                        {
                            stallCount++;
                            if (stallCount >= stallThresholdTicks && !stalledByMonitor)
                            {
                                var currentKbps = bytesDelta > 0
                                    ? (bytesDelta / 1024.0) / heartbeatIntervalSeconds
                                    : 0;

                                _logger.LogWarning("⚠️ Download stalled for >{StallTimeout}s: {Artist} - {Title} ({Current}/{Total} bytes, {Kbps:0.0} KB/s)",
                                    _config.StallTimeoutSeconds,
                                    ctx.Model.Artist, ctx.Model.Title, currentBytes, checkpointState.ExpectedSize, currentKbps);

                                ctx.Model.StalledReason = $"Throughput below {_config.MinThroughputFloorKbps} KB/s for {_config.StallTimeoutSeconds}s";
                                stalledByMonitor = true;
                                stallMonitorCts.Cancel();
                                return;
                            }
                        }
                        else
                        {
                            stallCount = 0; // Reset on progress
                        }

                        lastThroughputBytes = currentBytes;
                    }

                    // PERFORMANCE: Only update if progress > 1KB to reduce SQLite overhead
                    if (currentBytes > 0 && currentBytes > lastHeartbeatBytes + 1024 && checkpointId != null)
                    {
                        checkpointState.BytesDownloaded = currentBytes;

                        // Backlog C8: the checkpoint write shares this loop with stall detection.
                        // A journal failure (e.g. SQLITE_BUSY) used to escape to the outer catch,
                        // which ended the loop — silently switching stall detection off for the
                        // rest of the download. Contain it; the next tick retries.
                        try
                        {
                            // SSD OPTIMIZATION: Skip if no meaningful progress (built into UpdateHeartbeatAsync)
                            await _crashJournal.UpdateHeartbeatAsync(
                                checkpointId,
                                JsonSerializer.Serialize(checkpointState), // Serialize in heartbeat thread
                                lastHeartbeatBytes,
                                currentBytes);
                            lastHeartbeatBytes = currentBytes;
                        }
                        catch (Exception journalEx) when (journalEx is not OperationCanceledException)
                        {
                            if (!journalWarned)
                            {
                                journalWarned = true;
                                _logger.LogWarning(journalEx, "Crash-journal heartbeat write failed for {GlobalId}; stall detection continues", ctx.GlobalId);
                            }
                        }

                        _logger.LogTrace("Heartbeat: {Current}/{Total} bytes ({Percent}%)",
                            currentBytes, checkpointState.ExpectedSize,
                            (currentBytes * 100.0 / checkpointState.ExpectedSize));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on download completion or cancellation
                _logger.LogDebug("Heartbeat cancelled for {GlobalId}", ctx.GlobalId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Heartbeat error for {GlobalId}. reason={Reason} correlationId={CorrelationId}",
                    ctx.GlobalId,
                    ex.Message,
                    ctx.CorrelationId ?? "-");
            }
        }, heartbeatCts.Token);

        try
        {
            // STEP 4: Progress tracking with 100ms throttling
        var lastNotificationTime = DateTime.MinValue;
        var lastDetailedProgressTime = DateTime.MinValue;
        var totalFileSize = bestMatch.Size ?? 1;  // Avoid division by zero
        var progress = new Progress<double>(p =>
        {
            ctx.Progress = p * 100;
            ctx.BytesReceived = (long)((bestMatch.Size ?? 0) * p);

            // Throttle to 2 updates/sec (500ms) to prevent UI stuttering under heavy load
            if ((DateTime.Now - lastNotificationTime).TotalMilliseconds > 500)
            {
                _eventBus.Publish(new TrackProgressChangedEvent(
                    ctx.GlobalId,
                    ctx.Progress,
                    ctx.BytesReceived,
                    ctx.TotalBytes,
                    ctx.CorrelationId
                ));

                lastNotificationTime = DateTime.Now;
            }

            if ((DateTime.Now - lastDetailedProgressTime).TotalSeconds >= 3)
            {
                var speedBytesPerSecond = Math.Max(0, (long)ctx.CurrentSpeed);
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                    ctx.GlobalId,
                    $"↔ Transfer progress | user:{bestMatch.Username} | file:{bestMatch.Filename} | speed:{FormatBytes(speedBytesPerSecond)}/s | received:{FormatBytes(ctx.BytesReceived)} | total:{FormatBytes(ctx.TotalBytes)}",
                    false,
                    ctx.CorrelationId));
                lastDetailedProgressTime = DateTime.Now;
            }
        });

        // STEP 5: Download to .part file with resume support
        // Epic 4.1: Acquire per-peer gate before opening the TCP connection.
        // If a different track is already transferring from this same peer, we wait here
        // rather than open a second simultaneous connection (which often causes queue rejection).
        var peerGate = GetOrCreatePeerGate(bestMatch.Username!);
        await peerGate.WaitAsync(ct);

        // Issue #233: only the track that actually holds the peer gate is registered, keyed by
        // (peer, remote path) so a second track from the same peer can't steal its progress.
        if (!string.IsNullOrEmpty(bestMatch.Username) && !string.IsNullOrEmpty(bestMatch.Filename))
        {
            var key = new ActiveTransferRegistry.TransferKey(bestMatch.Username!, bestMatch.Filename!);
            ctx.ActiveTransferKey = key;
            _activeTransfers.Register(key, ctx);
        }

        bool success = false;
        // We already found and vetted this peer — a single dropped/cancelled transfer doesn't mean
        // they're bad, it's often just a transient hiccup. Give the SAME peer a couple of immediate
        // shots before falling back to a full fresh search for a different one (which costs a real
        // search window, not just a couple of seconds).
        const int maxSameUserAttempts = 3;
        try
        {
            for (var attempt = 1; attempt <= maxSameUserAttempts; attempt++)
            {
                try
                {
                    // Recompute the resume offset from disk before each attempt. A prior attempt
                    // in this same-peer retry loop can fail mid-transfer after already writing
                    // bytes past the offset we started with; reusing that stale offset here would
                    // reopen the .part file in Append mode (positioned at the new, larger EOF) but
                    // tell the transfer client to resume from the old, smaller offset — which
                    // requires a backward Seek that an Append-mode stream cannot perform, and
                    // throws (uncatchable from here — surfaces as an unobserved task exception).
                    startPosition = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    ctx.BytesReceived = startPosition;
                    if (ctx.ActiveTransferKey is { } activeKey) // also 0: reset a failed attempt's count
                        _activeTransfers.Seed(activeKey, startPosition);

                    success = await _soulseek.DownloadAsync(
                        bestMatch.Username!,
                        bestMatch.Filename!,
                        partPath,          // Download to .part file
                        bestMatch.Size,
                        progress,
                        lifecycleUpdate: update =>
                        {
                            switch (update.Phase)
                            {
                                case TransferLifecyclePhase.RemoteQueued:
                                    // Track when we entered the peer's queue for Queue Velocity monitoring.
                                    // Only set once — QueueEnteredAt should not reset on repeated Queued events.
                                    if (!ctx.QueueEnteredAt.HasValue)
                                    {
                                        ctx.QueueEnteredAt = DateTime.UtcNow;
                                        ctx.QueuePositionLastUpdated = DateTime.UtcNow;
                                    }
                                    _ = UpdateStateAsync(ctx, PlaylistTrackState.Queued, update.Detail ?? "Queued remotely by peer");
                                    _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                                        ctx.GlobalId,
                                        $"⏳ Remote queue: {bestMatch.Username} is holding the transfer in queue.",
                                        false,
                                        ctx.CorrelationId));
                                    break;
                                case TransferLifecyclePhase.QueuePositionUpdate:
                                    // Position improved — update the velocity clock and push to UI.
                                    ctx.CurrentQueuePosition = update.QueuePosition ?? ctx.CurrentQueuePosition;
                                    ctx.QueuePositionLastUpdated = DateTime.UtcNow;
                                    _logger.LogDebug("📋 Queue position update: {Artist} - {Title} → #{Position} (peer: {Peer})",
                                        ctx.Model.Artist, ctx.Model.Title, ctx.CurrentQueuePosition, bestMatch.Username);
                                    _eventBus.Publish(new TrackQueuePositionUpdatedEvent(
                                        ctx.GlobalId, ctx.CurrentQueuePosition, bestMatch.Username ?? ""));
                                    break;
                                case TransferLifecyclePhase.Transferring:
                                    ctx.CurrentQueuePosition = 0; // We're no longer in queue
                                    _ = UpdateStateAsync(ctx, PlaylistTrackState.Downloading, update.Detail);
                                    break;
                            }
                        },
                        ct: stallMonitorCts.Token,
                        startOffset: startPosition,     // Resume from existing bytes
                        // Only the episode's final attempt reports to peer-reliability scoring —
                        // a single flaky/transient failure shouldn't dock a peer 3x just because
                        // this loop gives them a few quick same-peer shots before moving on.
                        suppressCompletionEventOnFailure: attempt < maxSameUserAttempts
                    );
                }
                catch (OperationCanceledException) when (stalledByMonitor)
                {
                    // A real stall (no throughput for the configured timeout) is a different,
                    // already-handled scenario (see the Stall-detection retry path) — don't retry
                    // it here as if it were a quick same-peer hiccup.
                    throw new TimeoutException($"Local stall monitor triggered ({_config.StallTimeoutSeconds}s with no throughput). Dropping peer.");
                }

                if (success || attempt == maxSameUserAttempts)
                {
                    break;
                }

                _auditLogger.Log(ctx.GlobalId, $"⚠️ Transfer attempt {attempt}/{maxSameUserAttempts} failed from {bestMatch.Username} — retrying the same peer immediately.", isError: true);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        finally
        {
            peerGate.Release();
        }

        if (success)
        {
            // STEP 6: Atomic Rename - Only if download completed successfully
            try
            {
                // Brief pause to ensure all file handles are released
                await Task.Delay(100, ct);

                // Verify .part file exists and has correct size
                if (!File.Exists(partPath))
                {
                    throw new FileNotFoundException($"Part file disappeared: {partPath}");
                }

                var finalPartSize = new FileInfo(partPath).Length;
                // Fix: Allow file to be slightly larger (metadata padding)
                // We rely on VerifyAudioFormatAsync later for actual integrity
                if (finalPartSize < bestMatch.Size)
                {
                    throw new InvalidDataException(
                        $"Downloaded file truncated. Expected {bestMatch.Size}, got {finalPartSize}");
                }

                // LF6 (#348): when this download is an in-place quality upgrade, swap the staged file
                // into the old file's folder, trash the old file to .orbit-trash/, and shift cues/beatgrid
                // by the measured decoder offset.
                if (!string.IsNullOrEmpty(ctx.UpgradeSourcePath)
                    && File.Exists(ctx.UpgradeSourcePath)
                    && _qualityUpgradeService is not null)
                {
                    var realExt = Path.GetExtension(finalPath);
                    var stagedWithExt = string.IsNullOrEmpty(realExt)
                        ? partPath
                        : Path.ChangeExtension(partPath, realExt);
                    if (!string.Equals(stagedWithExt, partPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var stageMoveOk = await _fileWriteService.MoveAtomicAsync(partPath, stagedWithExt);
                        if (!stageMoveOk)
                            throw new IOException($"Failed to stage upgraded file with extension {realExt}: {partPath}");
                    }

                    var upgradeResult = await _qualityUpgradeService.ReplaceWithOffsetEstimationAsync(
                        ctx.Model.TrackUniqueHash ?? ctx.GlobalId,
                        stagedWithExt,
                        ctx.UpgradeSourcePath,
                        downloadDir,
                        allowProtectedOverride: false,
                        ct);

                    finalPath = upgradeResult.NewPath;
                    ctx.Model.ResolvedFilePath = upgradeResult.NewPath;
                    ctx.UpgradeSourcePath = null;
                    _logger.LogInformation(
                        "LF6 quality upgrade complete: {NewPath} (trashed {TrashedOld}, offset {OffsetMs:0.0} ms)",
                        upgradeResult.NewPath, upgradeResult.TrashedOldPath, upgradeResult.MeasuredOffsetSeconds * 1000.0);
                }
                else
                {
                    // Clean up old final file if it exists (race condition edge case)
                    if (File.Exists(finalPath))
                    {
                        _logger.LogWarning("Final file already exists, overwriting: {Path}", finalPath);
                        // File.Delete is handled by MoveAtomicAsync logic (via WriteAtomicAsync)
                    }

                    // ATOMIC OPERATION: Use SafeWrite to move .part to .mp3
                    var moveSuccess = await _fileWriteService.MoveAtomicAsync(partPath, finalPath);

                    if (!moveSuccess)
                    {
                         // If move failed (e.g. disk full during copy phase), throw execution to trigger retry/fail logic
                         throw new IOException($"Failed to atomically move file from {partPath} to {finalPath}");
                    }

                    _logger.LogInformation("Atomic move complete: {Part} â†’ {Final}",
                        Path.GetFileName(partPath), Path.GetFileName(finalPath));
                }

                _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                    ctx.GlobalId,
                    $"✅ Transfer finalized | user:{bestMatch.Username} | file:{Path.GetFileName(finalPath)} | total:{FormatBytes(finalPartSize)}",
                    false,
                    ctx.CorrelationId));

                // Strict mode uses the dedicated verifier; legacy mode keeps the lightweight helper checks.
                try
                {
                    _logger.LogDebug("Verifying downloaded file: {Path}", finalPath);

                    if (_config.EnableAutoDownloadStrictMode)
                    {
                        var verification = await _prefetchVerifier.VerifyDownloadAsync(ctx.Model, bestMatch, finalPath, ct);
                        if (verification != VerificationResult.Success && verification != VerificationResult.Disabled)
                        {
                            _logger.LogWarning("Strict verification failed for {Path}: {Result}", finalPath, verification);
                            _auditLogger.Log(ctx.GlobalId, $"❌ Validation failure: Strict verification failed: {verification}", isError: true);
                            _prefetchVerifier.CleanupStagingFile(finalPath);
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, DownloadFailureReason.FileVerificationFailed);
                            if (checkpointId != null) await _crashJournal.CompleteCheckpointAsync(checkpointId);
                            return;
                        }
                    }
                    else
                    {
                        var isValidAudio = await SLSKDONET.Services.IO.FileVerificationHelper.VerifyAudioFormatAsync(finalPath);
                        if (!isValidAudio)
                        {
                            _logger.LogWarning("Downloaded file failed audio format verification: {Path}", finalPath);
                            _auditLogger.Log(ctx.GlobalId, $"❌ Validation failure: Downloaded file failed audio format verification: {finalPath}", isError: true);
                            File.Delete(finalPath);
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, DownloadFailureReason.FileVerificationFailed);
                            if (checkpointId != null) await _crashJournal.CompleteCheckpointAsync(checkpointId);
                            return;
                        }

                        var isValidSize = await SLSKDONET.Services.IO.FileVerificationHelper.VerifyFileSizeAsync(finalPath, 10 * 1024);
                        if (!isValidSize)
                        {
                            _logger.LogWarning("Downloaded file too small (< 10KB): {Path}", finalPath);
                            _auditLogger.Log(ctx.GlobalId, $"❌ Validation failure: Downloaded file too small (< 10KB): {finalPath}", isError: true);
                            File.Delete(finalPath);
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, DownloadFailureReason.FileVerificationFailed);
                            if (checkpointId != null) await _crashJournal.CompleteCheckpointAsync(checkpointId);
                            return;
                        }
                    }

                    _logger.LogInformation("✅ File verification passed: {Path}", finalPath);
                    _auditLogger.Log(ctx.GlobalId, $"✅ File verification passed: {finalPath}");
                }
                catch (Exception verifyEx)
                {
                    _logger.LogError(verifyEx, "File verification error for {Path}", finalPath);
                    _auditLogger.Log(ctx.GlobalId, $"❌ File verification error for {finalPath}: {verifyEx.Message}", isError: true);

                    try { File.Delete(finalPath); } catch (IOException ex) { _logger.LogDebug(ex, "Best-effort failed-download file cleanup failed for {Path}", finalPath); }

                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, DownloadFailureReason.FileVerificationFailed);
                    if (checkpointId != null) await _crashJournal.CompleteCheckpointAsync(checkpointId);
                    return;
                }

                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                ctx.BytesReceived = bestMatch.Size ?? 0;  // Handle nullable size
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);

                // Phase 2A: Complete checkpoint on success
                if (checkpointId != null)
                {
                    // Phase 3A: Sentinel Flag - Prevent heartbeat from re-creating checkpoint
                    ctx.IsFinalizing = true;

                    await _crashJournal.CompleteCheckpointAsync(checkpointId);
                    _logger.LogDebug("✅ Download checkpoint completed: {Id}", checkpointId);
                }

                // CRITICAL: Create LibraryEntry for global index (enables All Tracks view + cross-project deduplication)
                var libraryEntry = new LibraryEntry
                {
                    UniqueHash = ctx.Model.TrackUniqueHash ?? string.Empty,
                    Artist = ctx.Model.Artist,
                    Title = ctx.Model.Title,
                    Album = ctx.Model.Album ?? "Unknown",
                    FilePath = finalPath,
                    Format = Path.GetExtension(finalPath).TrimStart('.'),
                    Bitrate = bestMatch.Bitrate,
                    // Carry over Spotify metadata from the PlaylistTrack so artwork/BPM/key are available immediately
                    SpotifyTrackId = ctx.Model.SpotifyTrackId,
                    SpotifyAlbumId = ctx.Model.SpotifyAlbumId,
                    SpotifyArtistId = ctx.Model.SpotifyArtistId,
                    AlbumArtUrl = ctx.Model.AlbumArtUrl,
                    ArtistImageUrl = ctx.Model.ArtistImageUrl,
                    Genres = ctx.Model.Genres,
                    Popularity = ctx.Model.Popularity,
                    BPM = ctx.Model.BPM,
                    MusicalKey = ctx.Model.MusicalKey,
                    CanonicalDuration = ctx.Model.CanonicalDuration,
                    ReleaseDate = ctx.Model.ReleaseDate,
                };

                // Issue #235/#239: hash the audio payload (ignores tags). Failure is non-fatal.
                if (_contentHasher is not null)
                {
                    var contentHash = await _contentHasher.ComputeAsync(finalPath, ct).ConfigureAwait(false);
                    if (contentHash is not null)
                    {
                        libraryEntry.AudioContentHash = contentHash.Value;

                        // Already in library by content hash? Link to the existing entry instead of
                        // creating a silent second one. The downloaded file is kept on disk.
                        var duplicate = await _databaseService
                            .FindLibraryEntryByContentHashAsync(contentHash.Value)
                            .ConfigureAwait(false);
                        // Only when the existing file is really there: re-downloading a track whose
                        // file was deleted/moved must replace it, not link to the missing path.
                        if (duplicate != null &&
                            !string.Equals(duplicate.UniqueHash, ctx.Model.TrackUniqueHash, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(duplicate.FilePath) && File.Exists(duplicate.FilePath))
                        {
                            ctx.Model.TrackUniqueHash = duplicate.UniqueHash;
                            ctx.Model.ResolvedFilePath = duplicate.FilePath;
                            await _libraryService.UpdatePlaylistTrackAsync(ctx.Model).ConfigureAwait(false);
                            // Neutral row status — not IsFlagged, which marks a fake/high-risk file.
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                                ctx.GlobalId, $"Already in library — same audio as {duplicate.FilePath}", false, ctx.CorrelationId));
                            _eventBus.Publish(new NotificationEvent(
                                "Already in library",
                                $"'{ctx.Model.Artist} – {ctx.Model.Title}' is a duplicate of '{duplicate.Artist} – {duplicate.Title}'.",
                                NotificationType.Information));
                            _auditLogger.Log(ctx.GlobalId,
                                $"• Duplicate: same audio as {duplicate.FilePath} (kept {finalPath})", isError: false);
                            return;
                        }
                    }
                }

                await _libraryService.SaveOrUpdateLibraryEntryAsync(libraryEntry);
                _logger.LogInformation("📚 Added to library: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);
                _auditLogger.Log(ctx.GlobalId, $"📚 Added to library: {ctx.Model.Artist} - {ctx.Model.Title} | Format: {libraryEntry.Format} | Bitrate: {libraryEntry.Bitrate}kbps");
            }
            catch (Exception renameEx)
            {
                _logger.LogError(renameEx, "Failed to perform atomic rename for {Track}", ctx.Model.Title);
                _auditLogger.Log(ctx.GlobalId, $"❌ Failed to perform atomic rename: {renameEx.Message}", isError: true);
                await UpdateStateAsync(ctx, PlaylistTrackState.Failed,
                    DownloadFailureReason.AtomicRenameFailed);
                if (checkpointId != null) await _crashJournal.CompleteCheckpointAsync(checkpointId);
            }
        }
        else
        {
            // A peer-side transfer failure (disconnected, cancelled, network hiccup) went straight
            // to a terminal Failed with zero retry — unlike every other failure path in this file
            // (a search miss gets MaxSearchAttempts in-session retries, a stall blacklists the peer
            // and requeues), a single flaky peer permanently doomed a track that a fresh search for
            // a different peer would very plausibly have fixed. Applies the same retry-budget
            // pattern used for search misses: blacklist this peer, back-of-queue retry, terminal
            // Failed only once that budget is exhausted.
            if (!string.IsNullOrEmpty(bestMatch.Username))
            {
                ctx.BlacklistUser(bestMatch.Username);
            }

            ctx.Model.SearchRetryCount++;

            if (ctx.Model.SearchRetryCount < _config.MaxSearchAttempts)
            {
                _auditLogger.Log(ctx.GlobalId, $"❌ Transfer failed from user {bestMatch.Username} after {maxSameUserAttempts} same-peer attempts. Retrying with a different peer (#{ctx.Model.SearchRetryCount}/{_config.MaxSearchAttempts}).", isError: true);

                ctx.Model.Priority = 20; // Low-priority lane for retries, same convention as a search-miss retry
                ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(15); // Minimum gap, same as a stall retry

                await UpdateStateAsync(ctx, PlaylistTrackState.Pending,
                    $"Transfer failed from {bestMatch.Username} — retrying with a different peer (#{ctx.Model.SearchRetryCount}/{_config.MaxSearchAttempts})");
            }
            else
            {
                _auditLogger.Log(ctx.GlobalId, $"❌ Download failed: Transfer failed from user {bestMatch.Username}. Exhausted {_config.MaxSearchAttempts} retries.", isError: true);
                await UpdateStateAsync(ctx, PlaylistTrackState.Failed,
                    DownloadFailureReason.TransferFailed);
                if (checkpointId != null) await _crashJournal.CompleteCheckpointAsync(checkpointId);
            }
        }
    }

    finally
    {
        // Issue #233: remove the exact transfer that held the gate.
        if (ctx.ActiveTransferKey is { } key)
            _activeTransfers.Remove(key);
        else if (!string.IsNullOrEmpty(bestMatch.Username) && !string.IsNullOrEmpty(bestMatch.Filename))
            _activeTransfers.Remove(new ActiveTransferRegistry.TransferKey(bestMatch.Username!, bestMatch.Filename!));

        // Phase 2A: CRITICAL CLEANUP - Stop heartbeat timer
        heartbeatCts.Cancel(); // Signal heartbeat to stop
        heartbeatTimer.Dispose();

        try
        {
            await heartbeatTask; // Wait for heartbeat task to complete
        }
        catch (OperationCanceledException)
        {
            // Expected when heartbeat is cancelled
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for heartbeat task cleanup");
        }
    }
}

    private async Task<bool> TryRunHedgeFailoverAsync(DownloadContext ctx, CancellationToken ct, string reason)
    {
        if (!_config.EnableHedgedDownloadFailover || ctx.HedgeAttempted || ctx.HedgeMatch == null)
        {
            return false;
        }

        var hedgeMatch = ctx.HedgeMatch;
        if (string.IsNullOrWhiteSpace(hedgeMatch.Username) || string.IsNullOrWhiteSpace(hedgeMatch.Filename))
        {
            return false;
        }

        ctx.HedgeAttempted = true;

        if (!string.IsNullOrEmpty(ctx.CurrentUsername))
        {
            ctx.BlacklistUser(ctx.CurrentUsername);
        }

        _logger.LogWarning("Hedge failover for {Title}: switching to runner-up peer {User}. reason={Reason} correlationId={CorrelationId}",
            ctx.Model.Title,
            hedgeMatch.Username,
            reason,
            ctx.CorrelationId ?? "-");

        _eventBus.Publish(new Events.TrackDetailedStatusEvent(
            ctx.GlobalId,
            $"⚡ Switching to runner-up peer {hedgeMatch.Username} ({reason}).",
            false,
            ctx.CorrelationId));

        try
        {
            await DownloadFileAsync(ctx, hedgeMatch, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Hedge failover attempt failed for {Title}. reason={Reason} correlationId={CorrelationId}",
                ctx.Model.Title,
                ex.Message,
                ctx.CorrelationId ?? "-");
            return false;
        }
    }

    private void OnDownloadProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        // Find context by username via the O(1) active-transfer index (kept in sync alongside
        // DownloadFileAsync's own username tracking — see the "Opt-P1" comments where it's
        // populated/cleared) instead of a locked linear scan of every download on every progress
        // packet. Guard the State check since the index and _downloads can theoretically be
        // observed a moment out of sync across threads (e.g. entry not yet removed post-completion).
        // Issue #233: key by (peer, remote path) so a queued second track never receives a
        // transferring first track's packets.
        var key = new ActiveTransferRegistry.TransferKey(e.Username, e.Filename);
        if (!_activeTransfers.TryGet(key, out var found) || found is null ||
            found.State != PlaylistTrackState.Downloading)
        {
            return;
        }
        var ctx = found;

        ctx.Progress = e.Progress * 100;
        ctx.BytesReceived = e.BytesReceived;
        ctx.TotalBytes = e.TotalBytes;

        var delta = _activeTransfers.RecordBytes(key, e.BytesReceived);
        if (delta > 0)
        {
            _peerReliability.RecordProgress(e.Username, delta);
        }

            // Phase 9: Stall Detection
            var now = DateTime.Now;
            if (ctx.LastSpeedUpdate.HasValue)
            {
                var timeDelta = (now - ctx.LastSpeedUpdate.Value).TotalSeconds;
                if (timeDelta > 0)
                {
                    ctx.CurrentSpeed = (long)(delta / timeDelta);
                    const long stallThreshold = 15 * 1024; // 15 KB/s
                    if (ctx.CurrentSpeed < stallThreshold)
                    {
                        if (!ctx.StallStartTime.HasValue)
                        {
                            ctx.StallStartTime = now;
                        }
                        else if ((now - ctx.StallStartTime.Value).TotalSeconds > 15 && !ctx.IsStalled)
                        {
                            ctx.IsStalled = true;
                            _eventBus.Publish(new TrackStalledEvent(ctx.GlobalId, e.Username));
                            _logger.LogWarning("🚧 Track stalled: {Title} from {User}, triggering hedge", ctx.Model.Title, e.Username);
                            // Trigger hedge failover
                            _ = Task.Run(async () =>
                            {
                                try { await TryRunHedgeFailoverAsync(ctx, _globalCts.Token, "stalled"); }
                                catch (OperationCanceledException) { }
                                catch (Exception ex) { _logger.LogError(ex, "Hedge failover failed for track {Title}", ctx.Model.Title); }
                            });
                        }
                    }
                    else
                    {
                        // Reset stall state if speed recovered
                        ctx.StallStartTime = null;
                        ctx.IsStalled = false;
                    }
                }
            }
            ctx.LastSpeedUpdate = now;
    }

    private void OnDownloadCompleted(object? sender, DownloadCompletedEventArgs e)
    {
        _logger.LogDebug("Adapter download completed event: {File} ({Success})", e.Filename, e.Success);
        _activeTransfers.Remove(new ActiveTransferRegistry.TransferKey(e.Username, e.Filename));

        if (e.Success)
        {
            _peerReliability.RecordDownloadCompleted(e.Username);
            return;
        }

        var isStall = !string.IsNullOrWhiteSpace(e.Error) &&
                      (e.Error.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                       e.Error.Contains("stalled", StringComparison.OrdinalIgnoreCase));
        _peerReliability.RecordDownloadFailed(e.Username, stalled: isStall);
    }

}
