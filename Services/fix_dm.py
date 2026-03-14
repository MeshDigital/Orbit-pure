#!/usr/bin/env python3
"""
Applies three hardening fixes to DownloadManager.cs:
  1. P0: Removes duplicate SaveOrUpdateLibraryEntryAsync call (double library write)
  2. P2: Fixes PauseLowestPriorityDownloadAsync passing TrackUniqueHash instead of GlobalId
  3. P1: Replaces O(N) locked OnDownloadProgressChanged with O(1) dict lookup already wired up
"""

import re

path = "Services/DownloadManager.cs"
with open(path, encoding="utf-8") as f:
    src = f.read()

original = src

# ── Fix 1: P0 ─────────────────────────────────────────────────────────────────
# Remove duplicate library write block (lines ~2370-2382)
# and reorder: checkpoint before UpdateStateAsync, add O(1) index cleanup comment.
old_completion = '''                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                ctx.BytesReceived = bestMatch.Size ?? 0;  // Handle nullable size
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);

                // Phase 2A: Complete checkpoint on success
                if (checkpointId != null)
                {
                    // Phase 3A: Sentinel Flag - Prevent heartbeat from re-creating checkpoint
                    ctx.IsFinalizing = true;
                    
                    await _crashJournal.CompleteCheckpointAsync(checkpointId);
                    _logger.LogDebug("\u2705 Download checkpoint completed: {Id}", checkpointId);
                }

                // CRITICAL: Create LibraryEntry for global index (enables All Tracks view + cross-project deduplication)
                var libraryEntry = new LibraryEntry
                {
                    UniqueHash = ctx.Model.TrackUniqueHash,
                    Artist = ctx.Model.Artist,
                    Title = ctx.Model.Title,
                    Album = ctx.Model.Album ?? "Unknown",
                    FilePath = finalPath,
                    Format = Path.GetExtension(finalPath).TrimStart('.'),
                    Bitrate = bestMatch.Bitrate
                };
                await _libraryService.SaveOrUpdateLibraryEntryAsync(libraryEntry);
                _logger.LogInformation("\U0001f4da Added to library: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);'''

new_completion = '''                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                ctx.BytesReceived = bestMatch.Size ?? 0;

                // Opt-P0: Complete checkpoint BEFORE state transition.
                // IsFinalizing must be set before UpdateStateAsync fires so the heartbeat
                // timer cannot race between file finalization and the sentinel flag being set.
                if (checkpointId != null)
                {
                    ctx.IsFinalizing = true; // Phase 3A: Sentinel Flag
                    await _crashJournal.CompleteCheckpointAsync(checkpointId);
                    _logger.LogDebug("\u2705 Download checkpoint completed: {Id}", checkpointId);
                }

                // Opt-P0 FIX: Removed duplicate SaveOrUpdateLibraryEntryAsync call.
                // UpdateStateAsync(Completed) internally calls AddTrackToLibraryIndexAsync (~line 886),
                // which already writes LibraryEntry. The old explicit call below caused two separate
                // SQLite transactions per download. State machine is sole source of truth.
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                _logger.LogInformation("\U0001f4da Download complete + indexed: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);'''

if old_completion in src:
    src = src.replace(old_completion, new_completion, 1)
    print("Fix 1 (P0 duplicate library write): APPLIED")
else:
    print("Fix 1 (P0 duplicate library write): TARGET NOT FOUND - check manually")

# ── Fix 2: P2 ─────────────────────────────────────────────────────────────────
# Fix PauseLowestPriorityDownloadAsync using TrackUniqueHash instead of GlobalId
old_pause = "            await PauseTrackAsync(lowestPriority.Model.TrackUniqueHash);"
new_pause = "            // Opt-P2 BugFix: PauseTrackAsync searches by t.GlobalId. Pass GlobalId, not TrackUniqueHash.\n            await PauseTrackAsync(lowestPriority.GlobalId);"

if old_pause in src:
    src = src.replace(old_pause, new_pause, 1)
    print("Fix 2 (P2 wrong field in PauseTrackAsync): APPLIED")
else:
    print("Fix 2 (P2 wrong field in PauseTrackAsync): TARGET NOT FOUND - check manually")

# ── Fix 3: P1 ─────────────────────────────────────────────────────────────────
# Replace O(N) locked OnDownloadProgressChanged with O(1) ConcurrentDictionary lookup
old_progress = '''    private void OnDownloadProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        // Find context by username (reliable enough for active transfers)
        DownloadContext? ctx;
        lock (_collectionLock)
        {
            ctx = _downloads.FirstOrDefault(d => 
                d.State == PlaylistTrackState.Downloading && 
                d.CurrentUsername == e.Username);
        }

        if (ctx != null)
        {
            ctx.Progress = e.Progress * 100;
            ctx.BytesReceived = e.BytesReceived;
            ctx.TotalBytes = e.TotalBytes;
        }
    }'''

new_progress = '''    private void OnDownloadProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        // Opt-P1: O(1) lock-free lookup via _activeByUsername.
        // Previously scanned _downloads under _collectionLock on every incoming data packet (O(N)).
        // With 5000 buffered tracks that was significant on fast transfers and caused UI stuttering.
        // _activeByUsername is maintained in DownloadFileAsync start/finally block.
        if (!string.IsNullOrEmpty(e.Username) && _activeByUsername.TryGetValue(e.Username, out var ctx))
        {
            ctx.BytesReceived = e.BytesReceived;
            ctx.TotalBytes = e.TotalBytes;
            ctx.Progress = e.Progress * 100;
        }
    }'''

if old_progress in src:
    src = src.replace(old_progress, new_progress, 1)
    print("Fix 3 (P1 O(N) progress handler): APPLIED")
else:
    print("Fix 3 (P1 O(N) progress handler): TARGET NOT FOUND - check manually")

if src != original:
    with open(path, "w", encoding="utf-8") as f:
        f.write(src)
    print("\nFile WRITTEN successfully.")
else:
    print("\nNo changes written (all targets not found or already applied).")
