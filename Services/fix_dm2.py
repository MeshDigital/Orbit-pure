#!/usr/bin/env python3
"""
Targeted fix for DownloadManager.cs P0: Remove duplicate LibraryEntry write.
Uses byte-level slicing around known anchor strings to avoid emoji matching issues.
"""

path = "Services/DownloadManager.cs"
with open(path, encoding="utf-8") as f:
    src = f.read()

original_len = len(src)

# Strategy: Find anchor "Phase 2A: Complete checkpoint on success" block
# and replace lines 2355-2382 by slicing on unique surrounding text.

ANCHOR_BEFORE = '                ctx.Model.ResolvedFilePath = finalPath;\r\n                ctx.Progress = 100;\r\n                ctx.BytesReceived = bestMatch.Size ?? 0;  // Handle nullable size\r\n                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);\r\n\r\n                // Phase 2A: Complete checkpoint on success\r\n                if (checkpointId != null)\r\n                {\r\n                    // Phase 3A: Sentinel Flag - Prevent heartbeat from re-creating checkpoint\r\n                    ctx.IsFinalizing = true;\r\n                    \r\n                    await _crashJournal.CompleteCheckpointAsync(checkpointId);\r\n'

ANCHOR_AFTER = '                }\r\n\r\n                // CRITICAL: Create LibraryEntry for global index'

TAIL_ANCHOR = ';\r\n            }\r\n            catch (Exception renameEx)'

if ANCHOR_BEFORE in src:
    # Find where the duplicate write block ends
    start_idx = src.index(ANCHOR_BEFORE)
    tail_idx = src.index(TAIL_ANCHOR, start_idx) + len(TAIL_ANCHOR)

    # The replacement: reordered (checkpoint first, then state, no duplicate write)
    replacement = (
        '                ctx.Model.ResolvedFilePath = finalPath;\r\n'
        '                ctx.Progress = 100;\r\n'
        '                ctx.BytesReceived = bestMatch.Size ?? 0;\r\n'
        '\r\n'
        '                // Opt-P0: Complete checkpoint BEFORE state transition so IsFinalizing\r\n'
        '                // is set before UpdateStateAsync fires (prevents heartbeat race).\r\n'
        '                if (checkpointId != null)\r\n'
        '                {\r\n'
        '                    ctx.IsFinalizing = true; // Phase 3A: Sentinel Flag\r\n'
        '                    await _crashJournal.CompleteCheckpointAsync(checkpointId);\r\n'
        '                    _logger.LogDebug("\u2705 Download checkpoint completed: {Id}", checkpointId);\r\n'
        '                }\r\n'
        '\r\n'
        '                // Opt-P0 FIX: Removed duplicate SaveOrUpdateLibraryEntryAsync call.\r\n'
        '                // UpdateStateAsync(Completed) internally fires AddTrackToLibraryIndexAsync\r\n'
        '                // (see ~line 886). The old explicit call caused 2 SQLite transactions per\r\n'
        '                // download. State machine is sole source of truth for library indexing.\r\n'
        '                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);\r\n'
        '                _logger.LogInformation("\U0001f4da Download complete + indexed: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);\r\n'
        '            }\r\n'
        '            catch (Exception renameEx)'
    )

    src = src[:start_idx] + replacement + src[tail_idx:]
    print(f"Fix 1 (P0 duplicate library write): APPLIED (removed {original_len - len(src)} chars)")
else:
    print("Fix 1 (P0): ANCHOR NOT FOUND - inspect file manually")
    # Dump nearby content to help debug
    needle = "Phase 2A: Complete checkpoint on success"
    idx = src.find(needle)
    if idx >= 0:
        print(f"  Found '{needle}' at char {idx}")
        print(f"  Surrounding (300 chars):\n{repr(src[idx-200:idx+300])}")

with open(path, "w", encoding="utf-8") as f:
    f.write(src)

print("Done.")
