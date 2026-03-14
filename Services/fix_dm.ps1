$file = 'Services\DownloadManager.cs'
$c = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)

# ── Fix 1: Remove duplicate SaveOrUpdateLibraryEntryAsync ────────────────────
# Step A: Replace the UpdateStateAsync(Completed) line + remove old checkpoint block that follows it
$oldBlock = @'
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
'@.Replace("`r`n", "`n")

$newBlock = @'
                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                ctx.BytesReceived = bestMatch.Size ?? 0;

                // Opt-P0: Complete checkpoint BEFORE state transition.
                // IsFinalizing must be true before UpdateStateAsync fires so the heartbeat
                // timer cannot race between file finalization and the sentinel flag.
                if (checkpointId != null)
                {
                    ctx.IsFinalizing = true; // Phase 3A: Sentinel Flag
                    await _crashJournal.CompleteCheckpointAsync(checkpointId);
'@.Replace("`r`n", "`n")

# Work on normalized line endings
$cn = $c.Replace("`r`n", "`n")

if ($cn.Contains($oldBlock)) {
    $cn = $cn.Replace($oldBlock, $newBlock)
    Write-Output "Step A: UpdateStateAsync reorder - OK"
} else {
    Write-Output "Step A: NOT FOUND"
}

# Step B: Remove the old comment + libraryEntry block
$oldLibBlock = @'

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
'@.Replace("`r`n", "`n")

$newLibBlock = @'

                // Opt-P0 FIX: Removed SaveOrUpdateLibraryEntryAsync that was here.
                // UpdateStateAsync(Completed) already calls AddTrackToLibraryIndexAsync (~line 886).
                // That old explicit call caused TWO SQLite transactions per download.
'@.Replace("`r`n", "`n")

if ($cn.Contains($oldLibBlock)) {
    $cn = $cn.Replace($oldLibBlock, $newLibBlock)
    Write-Output "Step B: Duplicate library write removed - OK"
} else {
    Write-Output "Step B: NOT FOUND - possible emoji issue, check manually"
}

# Restore CRLF and write
$out = $cn.Replace("`n", "`r`n")
[System.IO.File]::WriteAllText($file, $out, [System.Text.Encoding]::UTF8)
Write-Output "File written. New length: $($out.Length)"
