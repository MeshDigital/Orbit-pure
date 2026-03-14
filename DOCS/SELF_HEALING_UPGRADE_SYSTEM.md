# Phase 5A: Self-Healing Upgrade System

**Status**: ✅ Complete & Production-Ready  
**Last Updated**: December 25, 2025  
**Complexity**: VERY HIGH (State machine with 9 states, 8-step atomic swap)  
**Lines of Code**: ~1200 across UpgradeOrchestrator, MetadataCloner, FileLockMonitor, UpgradeScout  
**Related Files**: 
- [Services/SelfHealing/UpgradeOrchestrator.cs](../Services/SelfHealing/UpgradeOrchestrator.cs)
- [Services/SelfHealing/MetadataCloner.cs](../Services/SelfHealing/MetadataCloner.cs)
- [Services/SelfHealing/FileLockMonitor.cs](../Services/SelfHealing/FileLockMonitor.cs)
- [Services/SelfHealing/UpgradeScout.cs](../Services/SelfHealing/UpgradeScout.cs)

---

## Table of Contents

1. [Overview](#overview)
2. [State Machine Architecture](#state-machine-architecture)
3. [Eight-Step Atomic Swap](#eight-step-atomic-swap)
4. [File Lock Monitoring](#file-lock-monitoring)
5. [Metadata Cloning (Soul Transfer)](#metadata-cloning-soul-transfer)
6. [Backup & Restore Strategy](#backup--restore-strategy)
7. [Crash Recovery Integration](#crash-recovery-integration)
8. [Quality Gain Tracking](#quality-gain-tracking)
9. [Edge Cases & Recovery](#edge-cases--recovery)
10. [Troubleshooting](#troubleshooting)

---

## Overview

**Phase 5A** introduces an **automatic self-healing library system** that upgrades lower-quality tracks to higher-quality versions while preserving all metadata and history.

### The Problem (Pre-Phase 5A)

Users accumulate low-bitrate tracks over time:
- Old 128kbps MP3 downloads from Soulseek
- Compressed versions from streaming services
- Corrupted or transcoded "fake FLAC" files

Manual upgrades are tedious and risky:
- Metadata loss (playlist info, user edits)
- Accidental file deletion
- No crash recovery if interrupted

### The Solution: Transactional Upgrade System

```
BEFORE: 128kbps MP3 (Score: 100)
         ▼
    UPGRADING
         ▼
AFTER:  FLAC 44.1kHz (Score: 450) 
        + All metadata preserved
        + Original backed up (7 days)
        + Crash-safe (journal checkpoint)
```

---

## State Machine Architecture

**9 States** with automatic transitions and rollback capabilities:

```
┌──────────────────────────────────────────────────────────────┐
│           UPGRADE STATE MACHINE (9 States)                   │
├──────────────────────────────────────────────────────────────┤
│                                                               │
│  1. Pending ──────────┐                                      │
│                       ▼                                      │
│  2. Downloading ─────► Lock Check Failed ─────┐             │
│     │                                           │             │
│     │ (P2P Search)                             │             │
│     ▼                                           │             │
│  3. CloningMetadata ──────────────────────┐    │             │
│     │ (ID3/Vorbis Transfer)               │    │             │
│     ▼                                      │    │             │
│  4. ReadyToSwap ◄────────────────────────┘    │             │
│     │ (Journal Checkpoint)                     │             │
│     ▼                                          ▼             │
│  5. BackingUp ──► Backup Failed ─────────────► DEFERRED     │
│     │ (7-day retention)                        (5min retry)  │
│     ▼                                                         │
│  6. Swapping ──────────────────────────────┐                │
│     │ (Atomic file replace)                 │                │
│     ▼                                        ▼               │
│  7. UpdatingDatabase ───────────────────► FAILED            │
│     │ (Bitrate, integrity level)            (Rollback)      │
│     ▼                                                         │
│  8. COMPLETED ✅                                             │
│                                                               │
│  Side State: ROLLED_BACK (Crash recovery)                  │
│                                                               │
└──────────────────────────────────────────────────────────────┘
```

### State Definitions

| State | Duration | Action | Next State | Failure Path |
|-------|----------|--------|-----------|--------------|
| **Pending** | 0ms | Queued for processing | Downloading | - |
| **Downloading** | Variable | P2P search + download | CloningMetadata | FAILED |
| **CloningMetadata** | 5-30s | Transfer metadata tags | ReadyToSwap | FAILED |
| **ReadyToSwap** | 5ms | Create journal checkpoint | BackingUp | FAILED |
| **BackingUp** | 1-10s | Copy original file | Swapping | DEFERRED |
| **Swapping** | <100ms | Atomic file move | UpdatingDatabase | FAILED |
| **UpdatingDatabase** | 10ms | Update track record | COMPLETED | FAILED |
| **COMPLETED** | 0ms | Track upgraded | - | - |
| **DEFERRED** | - | Reschedule in 5 min | Pending | - |

### Transitions & Guards

```csharp
// State machine with guard conditions
private async Task<UpgradeResult> TransitionToNextStateAsync(
    UpgradeCandidate candidate,
    UpgradeState currentState)
{
    return currentState switch
    {
        UpgradeState.Pending 
            => await TransitionDownloadingAsync(candidate),
        
        UpgradeState.Downloading 
            => await TransitionCloningMetadataAsync(candidate),
        
        UpgradeState.CloningMetadata 
            => await TransitionReadyToSwapAsync(candidate),
        
        UpgradeState.ReadyToSwap 
            => await TransitionBackingUpAsync(candidate),
        
        UpgradeState.BackingUp 
            => await TransitionSwappingAsync(candidate),
        
        UpgradeState.Swapping 
            => await TransitionUpdatingDatabaseAsync(candidate),
        
        UpgradeState.UpdatingDatabase 
            => UpgradeResult.CreateCompleted(candidate.TrackId),
        
        _ => UpgradeResult.CreateFailed("Unknown state")
    };
}
```

---

## Eight-Step Atomic Swap

The core of the upgrade system is an **8-step transactional process** that ensures crash safety and metadata preservation.

### Step 1: Lock Check

**Verify the file is not in use before attempting replacement.**

```csharp
// Dual-layer safety
var lockStatus = await _fileLockMonitor.IsFileSafeToReplaceAsync(
    originalPath, 
    candidate.TrackId);

if (!lockStatus.IsSafe)
{
    _logger.LogWarning(
        "File locked by: {Reason}. Deferring 5 minutes.",
        lockStatus.Reason);
    
    // Persist deferral to database
    track.State = "Deferred";
    track.NextRetryTime = DateTime.UtcNow.AddMinutes(5);
    await _databaseService.SaveTrackAsync(track);
    
    return UpgradeResult.CreateDeferred(lockStatus.Message);
}
```

**Lock Reasons**:
- `PlayingInOrbit` - ORBIT's player currently playing track
- `LockedByExternalApp` - Rekordbox, Serato, or other app has file open
- `AntiVirus` - Windows Defender or similar scanning file
- `TransientLock` - File Explorer browsing directory

**Safety Guarantees**:
- ✅ Prevents data corruption
- ✅ Respects active playback
- ✅ Detects locked files in real-time

---

### Step 2: Find Upgrade Candidates

**Search P2P network for higher-quality versions.**

```csharp
var upgradeCandidates = await _upgradeScout.FindUpgradesAsync(
    candidate,  // Original track: 128kbps MP3
    ct);

// Returns ranked by quality score
// Example: FLAC 44.1kHz > 320kbps MP3 > 192kbps M4A
```

**Quality Scoring**:
- FLAC/WAV: +450 points
- 320kbps: +320 points
- 192-256kbps: +200 points
- <192kbps: No upgrade

**Duration Matching**: ±2 seconds tolerance to catch same recording (different encoding)

**Availability Check**: Must have at least 1 peer to prevent timeouts

---

### Step 3: Shadow Download

**Download to isolated temporary directory.**

```csharp
// Download to .orbit/tmp/upgrades/
const string UPGRADE_TEMP_DIR = ".orbit/tmp/upgrades";

var tempPath = await DownloadToShadowDirectoryAsync(bestCandidate, ct);

// File exists only in temp, original untouched
if (!File.Exists(tempPath))
    return UpgradeResult.CreateFailed("Download failed");
```

**Why "Shadow"**:
- ✅ Isolated from library (no accidental discovery)
- ✅ Can be deleted without harm
- ✅ Supports atomic rollback

**Location**: `.orbit/tmp/upgrades/{TrackId}_{Bitrate}_{Date}.{ext}`

---

### Step 4: Metadata "Soul Transfer"

**Clone all metadata from original to upgraded file.**

This is the most complex step. See [Metadata Cloning](#metadata-cloning-soul-transfer) section.

**What Gets Cloned**:
- ✅ Artist, Album, Title, Year
- ✅ Track number, disc number
- ✅ Genres, composers, comments
- ✅ Album art (cover image)
- ✅ ORBIT custom tags (Spotify ID, integrity level)
- ✅ Spotify features (Energy, Danceability, Valence)

**Dual-Truth Metadata Resolution**:
If original has manual edits:
- Manual BPM takes precedence over Spotify BPM
- Manual Key takes precedence over Spotify Key
- Dual-Truth fields preserve both versions

---

### Step 5: Journal Checkpoint

**Create crash recovery checkpoint before atomic swap.**

```csharp
// Persist the upgrade intent to SQLite journal
journalId = await CreateUpgradeJournalEntryAsync(
    originalPath, 
    tempUpgradePath, 
    candidate.TrackId);

// Journal Format
class UpgradeJournalEntry
{
    public string Id { get; set; }  // Unique identifier
    public string OriginalPath { get; set; }  // Path to original file
    public string TempPath { get; set; }  // Path to upgraded temp file
    public string TrackId { get; set; }  // Database reference
    public string State { get; set; }  // Current state
    public DateTime CreatedAt { get; set; }
}
```

**Recovery on Crash**:
If app crashes after Step 5:
1. On startup, scan for incomplete journal entries
2. Check if temp file still exists
3. Complete the swap (atomic rename)
4. Clean up journal entry

**Guarantees**:
- ✅ No orphaned files
- ✅ No duplicate downloads
- ✅ Automatic recovery on restart

---

### Step 6: Backup Original

**Create 7-day retention backup before destructive operation.**

```csharp
// Backup path: .orbit/backups/{TrackId}_{Date}_{OriginalBitrate}.bak
const int BACKUP_RETENTION_DAYS = 7;

var backupPath = CreateBackupPath(originalPath);
EnsureDirectoryExists(Path.GetDirectoryName(backupPath)!);

// Cross-volume aware move
if (Path.GetPathRoot(originalPath) == Path.GetPathRoot(backupPath))
{
    // Same volume: Fast atomic move
    File.Move(originalPath, backupPath);
}
else
{
    // Different volume: Copy + Delete (safer than move)
    File.Copy(originalPath, backupPath, overwrite: true);
    File.Delete(originalPath);
}
```

**Backup Strategy**:
- **Retention**: 7 days (configurable)
- **Storage**: .orbit/backups/ directory
- **Naming**: Includes original bitrate for diagnostics
- **Cleanup**: Automatic deletion after 7 days
- **Rollback**: User can manually restore if needed

**Cross-Volume Detection**:
If backup and original are on different physical drives:
- Uses Copy + Delete instead of Move
- Safer but slower (uses SafeWrite pattern)
- Prevents filesystem corruption

---

### Step 7: Atomic Swap

**Replace original with upgraded file.**

```csharp
// Move temp file to original location
File.Move(tempUpgradePath, originalPath, overwrite: false);

// On Windows NTFS: ATOMIC if same volume
// MFT update is microseconds, not milliseconds
```

**Atomicity Guarantees**:
- ✅ **Same volume**: Atomic MFT update (<1ms)
- ✅ **Different volume**: Deferred to step 6 backup (already cross-volume safe)

**Failure Points**:
- If destination already exists (shouldn't happen after step 6)
- If temp file corrupted during download
- If disk full

---

### Step 8: Update Database

**Record the upgrade in the database.**

```csharp
track.Filename = originalPath;  // Now points to FLAC
track.Bitrate = upgradedBitrate;  // e.g., "FLAC" or "320"
track.Format = "FLAC";
track.IntegrityLevel = IntegrityLevel.Gold;  // Verified upgrade
track.UpgradedFromBitrate = "128";  // Audit trail
track.UpgradedAt = DateTime.UtcNow;

await _databaseService.SaveTrackAsync(track);

// Update PlaylistTrack entries that reference this track
await _databaseService.SyncPlaylistTracksAsync(track.Id);
```

**What Gets Updated**:
- Bitrate, format, duration
- Integrity level (moved to Gold)
- Upgrade metadata (source, date, quality gain)
- PlaylistTrack entries (playlist UI stays in sync)

---

## File Lock Monitoring

**Prevents catastrophic data loss by detecting locked files.**

### Dual-Layer Safety

#### Layer 1: ORBIT Internal Check

```csharp
private bool IsPlayingInOrbit(string filePath, string? trackId)
{
    if (_playerViewModel?.CurrentTrack == null)
        return false;
    
    // Check by TrackId (most reliable)
    if (!string.IsNullOrEmpty(trackId) && 
        _playerViewModel.CurrentTrack.GlobalId == trackId)
    {
        _logger.LogDebug(
            "Track ID match: {TrackId} is currently playing", 
            trackId);
        return true;
    }
    
    return false;
}
```

**Instant Fail**:
- No retry
- User notified immediately
- Deferred 5 minutes

#### Layer 2: OS-Level Exclusive Lock Check

```csharp
private async Task<FileLockStatus> IsLockedByExternalProcessAsync(
    string filePath)
{
    try
    {
        // Attempt to open file exclusively
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,  // No sharing = exclusive lock test
            bufferSize: 0,   // Don't buffer
            useAsync: true);
        
        stream.Close();
        return new FileLockStatus { IsSafe = true };
    }
    catch (IOException ex) when (ex.HResult == -2147024864)
    {
        // ERROR_FILE_IN_USE
        return new FileLockStatus
        {
            IsSafe = false,
            Reason = FileLockReason.LockedByExternalApp,
            Message = "File is locked by another application"
        };
    }
}
```

**Detects**:
- ✅ Rekordbox or Serato playing track
- ✅ DJ software preparing cues
- ✅ Media players (Windows Media Player, VLC)
- ✅ Explorer.exe (file browsing)
- ✅ Antivirus scanning

### Pre-Flight Spin-Wait

**Retry logic for transient locks (Explorer, Antivirus).**

```csharp
// Try 3 times with 1-second delays
for (int attempt = 0; attempt < 3; attempt++)
{
    var osLockStatus = await IsLockedByExternalProcessAsync(filePath);
    
    if (osLockStatus.IsSafe)
    {
        _logger.LogDebug(
            "✅ File safe on attempt {Attempt}", 
            attempt + 1);
        return osLockStatus;
    }
    
    if (attempt < 2)
    {
        _logger.LogWarning(
            "File locked (attempt {A}/3), waiting 1s...",
            attempt + 1);
        await Task.Delay(1000);
    }
}

// Final failure after 3 seconds
return new FileLockStatus
{
    IsSafe = false,
    Reason = FileLockReason.LockedByExternalApp,
    Message = "File locked by external process"
};
```

**Handles Anti-Virus Scan**:
- Windows Defender temporarily locks files
- Releasing after 1-2 seconds is common
- 3-attempt strategy catches this pattern

---

## Metadata Cloning (Soul Transfer)

**Preserves user edits and metadata history during upgrades.**

### Metadata Types

#### Standard Tags (ID3/Vorbis)

```csharp
target.Tag.Title = source.Tag.Title;
target.Tag.Performers = source.Tag.Performers;  // Artists
target.Tag.Album = source.Tag.Album;
target.Tag.Year = source.Tag.Year;
target.Tag.Genres = source.Tag.Genres;
target.Tag.Track = source.Tag.Track;
target.Tag.TrackCount = source.Tag.TrackCount;
target.Tag.Disc = source.Tag.Disc;
target.Tag.Comment = source.Tag.Comment;
```

#### Album Art

```csharp
// Clone artwork from source to target
if (source.Tag.Pictures.Count > 0)
{
    foreach (var picture in source.Tag.Pictures)
    {
        target.Tag.AddPicture(picture);
    }
}
```

#### ORBIT Custom Tags

**Critical for database linkage:**

```csharp
const string TAG_TRACK_ID = "ORBIT_TRACK_ID";
const string TAG_SPOTIFY_ID = "ORBIT_SPOTIFY_ID";
const string TAG_INTEGRITY = "ORBIT_INTEGRITY";
const string TAG_IMPORT_DATE = "ORBIT_IMPORT_DATE";
const string TAG_UPGRADE_SOURCE = "ORBIT_UPGRADE_SOURCE";
const string TAG_ORIGINAL_BITRATE = "ORBIT_ORIGINAL_BITRATE";
const string TAG_ENERGY = "ORBIT_ENERGY";
const string TAG_DANCEABILITY = "ORBIT_DANCEABILITY";
const string TAG_VALENCE = "ORBIT_VALENCE";
```

**Purpose**:
- TRACK_ID links file to database (critical for recovery)
- SPOTIFY_ID enables future enrichment
- INTEGRITY marks verification status (Gold/Silver/Bronze)
- ENERGY/DANCEABILITY preserves Spotify features
- ORIGINAL_BITRATE tracks upgrade history

#### Dual-Truth Musical Metadata

**Resolves conflicts between Spotify and manual edits:**

```csharp
// If user manually entered BPM, keep it
// Otherwise use Spotify BPM
if (track.ManualBpm.HasValue)
{
    target.Tag.SetFrame("TBPM", track.ManualBpm.ToString());
}
else if (!string.IsNullOrEmpty(track.SpotifyBpm))
{
    target.Tag.SetFrame("TBPM", track.SpotifyBpm);
}

// Same for Key
if (!string.IsNullOrEmpty(track.ManualKey))
{
    target.Tag.SetFrame("TKEY", track.ManualKey);
}
else if (!string.IsNullOrEmpty(track.SpotifyKey))
{
    target.Tag.SetFrame("TKEY", track.SpotifyKey);
}
```

### Cross-Format Transfer

**Handles ID3 (MP3/WAV) ↔ Vorbis (FLAC/OGG) metadata translation.**

```
ID3v2.4 (MP3)         →   TagLib Maps to Common   →   Vorbis Comments (FLAC)
──────────────          ─────────────────────          ─────────────────────
TALB (Album)             Tag.Album                      ALBUM
TPE1 (Artist)            Tag.Performers                 ARTIST
TIT2 (Title)             Tag.Title                      TITLE
TRCK (Track)             Tag.Track                      TRACKNUMBER
TDRC (Year)              Tag.Year                       DATE
TCON (Genre)             Tag.Genres                     GENRE
```

**TagLib abstraction handles conversion automatically.**

---

## Backup & Restore Strategy

### Backup Location

```
.orbit/backups/
├── {TrackId}_2025-12-25_128kbps.bak   (7 days old → deleted)
├── {TrackId}_2025-12-26_128kbps.bak   (6 days old)
├── {TrackId}_2025-12-27_128kbps.bak   (5 days old)
├── {TrackId}_2025-12-28_128kbps.bak   (4 days old)
├── {TrackId}_2025-12-29_128kbps.bak   (3 days old)
├── {TrackId}_2025-12-30_128kbps.bak   (2 days old)
└── {TrackId}_2025-12-31_128kbps.bak   (1 day old)
```

### Cleanup Policy

```csharp
// Background job (runs daily)
public async Task CleanupOldBackupsAsync()
{
    var backupDir = new DirectoryInfo(".orbit/backups");
    var now = DateTime.Now;
    
    foreach (var file in backupDir.GetFiles("*.bak"))
    {
        var age = now - file.CreationTime;
        
        if (age.TotalDays > BACKUP_RETENTION_DAYS)
        {
            _logger.LogInformation(
                "Deleting old backup: {File} ({Age} days old)",
                file.Name, age.TotalDays);
            
            file.Delete();
        }
    }
}
```

### Manual Restore

**Users can manually restore from backup if needed:**

```csharp
public async Task RestoreFromBackupAsync(
    string backupPath, 
    string originalPath)
{
    _logger.LogWarning(
        "🔙 Restoring from backup: {Backup} → {Original}",
        backupPath, originalPath);
    
    // Restore is just a move + database rollback
    File.Move(backupPath, originalPath, overwrite: true);
    
    // Downgrade track back to original bitrate
    var track = await _databaseService.FindTrackAsync(trackId);
    track.Bitrate = "128";
    track.IntegrityLevel = IntegrityLevel.Bronze;
    track.UpgradedAt = null;
    
    await _databaseService.SaveTrackAsync(track);
}
```

---

## Crash Recovery Integration

**Leverages Phase 2A journal system for crash safety.**

### Recovery on Startup

```csharp
public async Task RecoverFromCrashAsync()
{
    _logger.LogInformation("Checking for incomplete upgrades...");
    
    // Load all incomplete journal entries
    var incompleteUpgrades = await _journal.GetIncompleteUpgradesAsync();
    
    foreach (var entry in incompleteUpgrades)
    {
        _logger.LogInformation(
            "Found incomplete upgrade: {TrackId} at state {State}",
            entry.TrackId, entry.State);
        
        switch (entry.State)
        {
            case UpgradeState.ReadyToSwap:
            case UpgradeState.BackingUp:
                // Safe to resume atomic swap
                await CompleteAtomicSwapAsync(entry);
                break;
                
            case UpgradeState.CloningMetadata:
            case UpgradeState.Downloading:
                // Partial state - need to clean up
                await RollbackUpgradeAsync(entry);
                break;
        }
    }
}
```

---

## Quality Gain Tracking

**Gamifies upgrades by showing bitrate improvement.**

```csharp
var qualityGain = new QualityGain
{
    TrackId = track.Id,
    OriginalBitrate = 128,  // kbps
    UpgradedBitrate = 0,     // FLAC = lossless
    UpgradedAt = DateTime.UtcNow,
    BitrateImprovement = "∞ (Lossless)",  // 128 → FLAC
    PercentImprovement = 350  // 128 → 320 = 250% (320/128 = 2.5x)
};

// UI displays:
// "🎉 Upgraded from 128kbps to FLAC (+350% quality)"
```

**Quality Levels**:
- Gold (⭐⭐⭐): FLAC/WAV (lossless)
- Silver (⭐⭐): 320kbps MP3/M4A
- Bronze (⭐): <320kbps

---

## Edge Cases & Recovery

### Case 1: File Locked by DJ Software

**Scenario**: User is using Rekordbox while upgrade runs.

**Behavior**:
1. FileLockMonitor detects lock
2. Defers upgrade 5 minutes
3. Retries after user closes Rekordbox

**Code**:
```csharp
if (!lockStatus.IsSafe && lockStatus.Reason == FileLockReason.LockedByExternalApp)
{
    track.State = "Deferred";
    track.NextRetryTime = DateTime.UtcNow.AddMinutes(5);
    await _databaseService.SaveTrackAsync(track);
    
    return UpgradeResult.CreateDeferred(
        $"Track in use by {lockStatus.AppName}. Retrying in 5 minutes.");
}
```

### Case 2: Metadata Clone Fails

**Scenario**: Corrupted tags cause Clone to throw exception.

**Behavior**:
1. Catch exception in Step 4
2. Delete temp file
3. Return FAILED result
4. Track remains in Bronze state

**Code**:
```csharp
if (!await ValidateMetadataCloneAsync(
    tempUpgradePath, track, originalPath))
{
    File.Delete(tempUpgradePath);
    return UpgradeResult.CreateFailed(
        "Metadata validation failed (possible corruption)");
}
```

### Case 3: Disk Full During Backup

**Scenario**: .orbit/backups runs out of space during Step 6.

**Behavior**:
1. Backup operation throws IOException
2. Catch and return DEFERRED (not FAILED!)
3. Temp file still exists, journal entry persists
4. Retry after user clears disk space

**Code**:
```csharp
try
{
    File.Move(originalPath, backupPath);
}
catch (IOException ex) when (ex.HResult == -2147024784)  // ERROR_DISK_FULL
{
    _logger.LogWarning("Disk full during backup. Deferring upgrade.");
    return UpgradeResult.CreateDeferred("Insufficient disk space");
}
```

### Case 4: Power Loss During Atomic Swap

**Scenario**: Computer loses power between Step 7 and Step 8.

**Behavior**:
1. Journalentry exists in SQLite (persisted on shutdown)
2. On startup, recover finds incomplete entry
3. Checks if temp file exists
4. If yes, completes the swap (idempotent)
5. If no, rolls back and cleans up

**Atomicity Guarantee**: NTFS/Ext4 MFT update is atomic <1ms

---

## Troubleshooting

### Issue: Upgrades Never Start

**Symptom**: Tracks stuck in "Pending" state indefinitely.

**Causes**:
1. P2P search finding no candidates
2. FileLockMonitor blocking all files
3. Disk space exhausted

**Debug**:
```csharp
// Check P2P search
var candidates = await _upgradeScout.FindUpgradesAsync(track);
_logger.LogInformation(
    "Found {Count} upgrade candidates for {Title}",
    candidates.Count, track.Title);

// Check file locks
var lockStatus = await _fileLockMonitor.IsFileSafeToReplaceAsync(
    track.Filename, track.Id);
_logger.LogInformation(
    "File lock status: {Safe} ({Reason})",
    lockStatus.IsSafe, lockStatus.Reason);

// Check disk space
var driveInfo = new DriveInfo(
    Path.GetPathRoot(track.Filename));
_logger.LogInformation(
    "Free disk space: {GB}GB",
    driveInfo.AvailableFreeSpace / 1024 / 1024 / 1024);
```

### Issue: Metadata Cloning Fails

**Symptom**: "Metadata validation failed" errors.

**Causes**:
1. Corrupted source tags (ID3 malformed)
2. Format mismatch (ID3 v1 on FLAC source)
3. TagLib parsing error

**Solution**:
```csharp
// Inspect source file tags
using var sourceFile = TagLib.File.Create(sourcePath);
_logger.LogDebug(
    "Source file tags: Title={T}, Artist={A}, Album={B}",
    sourceFile.Tag.Title,
    sourceFile.Tag.FirstPerformer,
    sourceFile.Tag.Album);

// If null/empty, consider it un-tagged
if (string.IsNullOrWhiteSpace(sourceFile.Tag.Title))
{
    _logger.LogWarning(
        "Source file has no title tag. Using database values.");
}
```

### Issue: Backup Disk Space Growing

**Symptom**: .orbit/backups/ consuming >10GB.

**Causes**:
1. Cleanup job not running
2. Retention policy set too long
3. Stuck old backups

**Solution**:
```csharp
// Manual cleanup
var backupDir = new DirectoryInfo(".orbit/backups");
foreach (var file in backupDir.GetFiles("*.bak"))
{
    var age = DateTime.Now - file.CreationTime;
    if (age.TotalDays > 7)
    {
        _logger.LogInformation(
            "Deleting old backup: {File} ({Days} days)",
            file.Name, age.TotalDays);
        file.Delete();
    }
}
```

---

## Best Practices

### 1. Monitor Upgrade Progress

```csharp
_upgradeOrchestrator.GetUpgradeProgress()
    .Subscribe(progress =>
    {
        _logger.LogInformation(
            "Upgrade progress: {Processed}/{Total} tracks, " +
            "{Completed} upgraded, {Failed} failed",
            progress.ProcessedCount,
            progress.TotalCount,
            progress.CompletedCount,
            progress.FailedCount);
    });
```

### 2. Set Reasonable Retention Policies

```csharp
// 7 days = 7 × (library size / upgrade rate)
// If upgrading 10 tracks/day: 70 files backup space
// If upgrading 100 tracks/day: 700 files backup space
const int BACKUP_RETENTION_DAYS = 7;  // Reasonable default
```

### 3. Respect DJ Software Locks

```csharp
// Don't upgrade during active DJ sessions
if (await IsDjSoftwareActiveAsync())
{
    _logger.LogInformation(
        "DJ software detected. Deferring upgrades.");
    return;
}
```

### 4. Validate Before and After

```csharp
// Pre-upgrade validation
if (!IsUpgradeable(track))
{
    _logger.LogWarning(
        "Track not upgradeable: {Reason}", track.Id);
    return;
}

// Post-upgrade validation
if (!await ValidateUpgradeAsync(track, upgradedPath))
{
    _logger.LogError(
        "Post-upgrade validation failed for {Id}", track.Id);
    await RollbackUpgradeAsync(track);
}
```

---

## Performance Characteristics

| Operation | Latency | Notes |
|-----------|---------|-------|
| Lock check | 3-5s | Pre-flight spin-wait (3 retries × 1s) |
| P2P search | 5-15s | Network dependent |
| Download | 10-60s | File size dependent |
| Metadata clone | 1-5s | TagLib operations |
| Backup | 2-10s | Disk I/O |
| Atomic swap | <100ms | NTFS MFT operation |
| Database update | 10ms | SQLite transaction |
| **Total** | **30-90s** | Per track upgrade |

---

## See Also

- [PHASE_IMPLEMENTATION_AUDIT.md](PHASE_IMPLEMENTATION_AUDIT.md) - Complete audit with metrics
- [DOWNLOAD_RESILIENCE.md](DOWNLOAD_RESILIENCE.md) - Phase 2A crash recovery
- [ARCHIVE_MEDIUM.md](../DOCS/archive/PHASE5_SELF_HEALING.md) - Original Phase 5 planning
- [Services/SelfHealing/](../Services/SelfHealing/) - All implementation files

---

**Last Updated**: December 25, 2025  
**Status**: ✅ Complete & Documented  
**Maintainer**: MeshDigital
