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

/// <summary>Crash-recovery journal reconciliation — extracted from DownloadManager (#307).</summary>
public partial class DownloadManager
{
    /// <summary>
    /// Phase 13: Crash Recovery Journal Integration
    /// Reconciles state between SQLite WAL Journal and Disk.
    /// Handles:
    /// 1. Truncation Guard (fixing over-written .part files)
    /// 2. Ghost/Zombie Cleanup (removing stale checkpoints)
    /// 3. Priority Resumption (jumping queue for interrupted downloads)
    /// </summary>
    private async Task HydrateFromCrashAsync()
    {
        try
        {
            var pendingCheckpoints = await _crashJournal.GetPendingCheckpointsAsync();
            if (!pendingCheckpoints.Any())
            {
                _logger.LogDebug("Journal Check: Clean state (no pending checkpoints)");
                return;
            }

            _logger.LogInformation("Journal Check: Found {Count} pending download sessions", pendingCheckpoints.Count);

            int recovered = 0;
            int zombies = 0;

            // Phase 13 Optimization: "Batch Zombie Check"
            // Instead of querying DB one-by-one, we fetch all relevant tracks in one go.
            var uniqueHashList = pendingCheckpoints
                .Select(c => JsonSerializer.Deserialize<DownloadCheckpointState>(c.StateJson)?.TrackGlobalId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var knownTracks = new HashSet<string>();
            try
            {
                // Assuming DatabaseService has a method to check existence by list, or we add one.
                // For now, sticking to existing public surface area to avoid expanding scope too much
                // in this specific 'replace_file_content' operation.
                // If ID is in Hydrated downloads, we know it exists.
                lock (_collectionLock)
                {
                    foreach(var d in _downloads) knownTracks.Add(d.GlobalId);
                }
            }
            catch (Exception ex)
            {
                 _logger.LogWarning(ex, "Failed to optimize zombie check");
            }

            foreach (var checkpoint in pendingCheckpoints)
            {
                if (checkpoint.OperationType != OperationType.Download) continue;

                DownloadCheckpointState? state = null;
                try
                {
                    state = JsonSerializer.Deserialize<DownloadCheckpointState>(checkpoint.StateJson);
                }
                catch
                {
                    _logger.LogWarning("Corrupt checkpoint state for {Id}, marking dead letter.", checkpoint.Id);
                    await _crashJournal.MarkAsDeadLetterAsync(checkpoint.Id);
                    continue;
                }

                if (state == null) continue;

                // 2. CORRELATE: Find the DownloadContext
                DownloadContext? ctx;
                lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == state.TrackGlobalId);

                if (ctx == null)
                {
                    // Track exists in DB but not in memory.
                    // Zombie check: If file completely missing AND track gone from DB?
                    if (!File.Exists(state.PartFilePath) && !knownTracks.Contains(state.TrackGlobalId))
                    {
                        // Check DB individually if not in cache (fallback)
                        var dbTrack = await _databaseService.FindTrackAsync(state.TrackGlobalId);
                        if (dbTrack == null)
                        {
                            _logger.LogWarning("👻 Zombie Checkpoint: {Track} (File & Record missing). Cleaning up.", state.Title);
                            await _crashJournal.CompleteCheckpointAsync(checkpoint.Id);
                            zombies++;
                            continue;
                        }
                    }
                    else
                    {
                         // Track likely exists but wasn't hydrated (Lazy buffer full?)
                         // We leave the checkpoint alone. The "RefillQueueAsync" will pick up the track later.
                         _logger.LogDebug("Deferred Recovery: {Track} valid but not in active memory.", state.Title);
                    }
                    continue;
                }

                // 3. TRUNCATION GUARD (The "Industrial" Fix)
                if (File.Exists(state.PartFilePath))
                {
                    var info = new FileInfo(state.PartFilePath);
                    if (info.Length > state.BytesDownloaded)
                    {
                        try
                        {
                            _logger.LogWarning("⚠️ Truncation Guard: Truncating {Track} from {Disk} to {Journal} bytes.",
                                state.Title, info.Length, state.BytesDownloaded);

                            await using (var fs = new FileStream(state.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                            {
                                fs.SetLength(state.BytesDownloaded);
                                await fs.FlushAsync();
                            }
                        }
                        catch (IOException ioEx)
                        {
                             _logger.LogError("Locked file {Path} prevented truncation. Skipping recovery for this session. ({Msg})", state.PartFilePath, ioEx.Message);
                             continue; // Skip this track until next restart or manual retry
                        }
                        catch (Exception ex)
                        {
                             _logger.LogError(ex, "Failed to truncate file: {Path}", state.PartFilePath);
                        }
                    }
                }

                // 4. UPDATE MEMORY STATE
                ctx.BytesReceived = state.BytesDownloaded;
                ctx.TotalBytes = state.ExpectedSize;
                ctx.IsResuming = true;

                // 5. PRIORITIZE
                ctx.NextRetryTime = DateTime.MinValue;
                ctx.RetryCount = 0;

                if (ctx.State == PlaylistTrackState.Downloading || ctx.State == PlaylistTrackState.Searching || ctx.State == PlaylistTrackState.Stalled)
                {
                    // If user manually paused it, honor that
                    if (ctx.Model.IsUserPaused)
                    {
                        ctx.State = PlaylistTrackState.Paused;
                    }
                    else
                    {
                        // Otherwise, reset to pending for retry
                        ctx.State = PlaylistTrackState.Pending;
                    }
                }

                // Also catch any tracks left in 'Paused' state that SHOULD be paused
                if (ctx.Model.IsUserPaused && ctx.State != PlaylistTrackState.Paused)
                {
                    ctx.State = PlaylistTrackState.Paused;
                }

                // If it's not paused, and was in a failed/cancelled state, reset to pending
                if (!ctx.Model.IsUserPaused && (ctx.State == PlaylistTrackState.Failed || ctx.State == PlaylistTrackState.Cancelled))
                {
                    ctx.State = PlaylistTrackState.Pending;
                }

                await UpdateStateAsync(ctx, ctx.State, "Recovered from Crash Journal");

                recovered++;
                _logger.LogInformation("✅ Recovered Session: {Artist} - {Title} ({Percent}%)",
                    state.Artist, state.Title, (state.BytesDownloaded * 100.0 / Math.Max(1, state.ExpectedSize)).ToString("F0"));
            }

            // Clean up stale entries while we are here
            await _crashJournal.ClearStaleCheckpointsAsync();

            _logger.LogInformation("Recovery Summary: {Recovered} Resumed, {Zombies} Zombies squashed.", recovered, zombies);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical Error in RecoverJournaledDownloadsAsync");
        }
    }
}
