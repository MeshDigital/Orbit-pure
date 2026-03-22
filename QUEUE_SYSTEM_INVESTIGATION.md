# Download Center Queue System - Deep Dive Investigation

**Date:** March 21, 2026  
**Status:** Critical Issues Identified - 5 Architectural Flaws Confirmed

---

## Executive Summary

The download center queue system has 5 critical architectural flaws that will:
- Get users **permanently banned** from Soulseek peers (Issue #1)
- **Lose queued downloads** on app restart (Issue #2) 
- **Freeze the UI** during batch imports (Issue #3)
- **Hang indefinitely** when peers disconnect (Issue #4)
- **Waste bandwidth** downloading duplicates (Issue #5)

---

## Issue #1: Fire & Forget Concurrency Flood (Peer Banning) 🔴 **CRITICAL**

**Current Status:** ❌ **NOT FIXED**

### The Problem
When a user queues 15 tracks from the **same peer**, the download manager processes them sequentially through `ProcessQueueLoop`, but each track launches a background task via:
```csharp
_ = Task.Run(async () => { await ProcessTrackAsync(nextContext, token); }, token);
```

The global `SemaphoreSlim _downloadSemaphore` blocks at 3-5 concurrent downloads, **BUT there is NO per-peer limit**. If all 3 active downloads are from the same peer (UserA), Soulseek sees:
- 3 simultaneous TCP connections from your client to UserA
- UserA's router detects "port scanning" (DoS attack)
- UserA is flagged as under attack
- **Your user gets permanent ban** from UserA's peer list

### Current Code Vulnerability
[Services/DownloadManager.cs](Services/DownloadManager.cs) has:

```csharp
private readonly SemaphoreSlim _downloadSemaphore;  // Global limit only
// NO FIELD: private readonly ConcurrentDictionary<string, SemaphoreSlim> _peerLimits;

// In ProcessQueueLoop (line ~2009):
_ = Task.Run(async () => 
{
    try { await ProcessTrackAsync(nextContext, token); }  // Fire immediately, no peer check
    finally { _downloadSemaphore.Release(); }
}, token);
```

### The Fix Required
Implement **Per-Peer Concurrency Tracking**:
1. Add `ConcurrentDictionary<string, SemaphoreSlim> _peerLimits` to track per-peer slots
2. Before firing download, acquire BOTH global slot AND peer slot
3. Limit peer to 2 concurrent transfers max (Soulseek protocol standard)
4. Clean up peer locks when no active downloads remain

### Impact
- **Severity:** 🔴 CRITICAL (Users will be banned)
- **Scope:** Affects all multi-track imports from single peer
- **Fix Effort:** ~2 hours
- **Testing:** Requires multi-track album download from single peer

---

## Issue #2: Amnesia Queue - Data Loss on Restart 🔴 **CRITICAL**

**Current Status:** ❌ **NOT FIXED**

### The Problem
When user queues 200 tracks:
1. All tracks added to `List<DownloadContext> _downloads` (RAM only)
2. Each track state set to `PlaylistTrackState.Pending`
3. User closes app or it crashes
4. On restart: App hydrates from DB, but **only re-initializes tracks with State=Pending**
5. Any intermediate queue state is **lost**

### Current Code Vulnerability
[Services/DownloadManager.cs](Services/DownloadManager.cs) hydration (line ~395):
```csharp
var pendingTracks = await _databaseService.GetPendingPriorityTracksAsync(LAZY_QUEUE_BUFFER_SIZE, ...);
HydrateAndAddEntities(pendingTracks);  // Only loads from DB with Pending status
```

The `_downloads` list is **ephemeral** - no separate queue table.

### The Fix Required
Implement **Queue Persistence**:
1. Create `QueuePositionEntity` table with columns:
   - `Id` (PK)
   - `PlaylistTrackId` (FK)
   - `QueuePosition` (0-based index)
   - `EnqueuedAt` (when added to queue)
   - `PriorityNumber` (queue priority)

2. On `QueueTracks()`: Insert row into `QueuePositionEntity` **before** adding to `_downloads`
3. On startup: Load queue order from `QueuePositionEntity`, reconstruct `_downloads` list
4. OnCompleted: Delete row from `QueuePositionEntity`
5. On cancel: Delete row from `QueuePositionEntity`

### Impact
- **Severity:** 🔴 CRITICAL (Users lose all queued work)
- **Scope:** Affects all users who queue then restart
- **Fix Effort:** ~3 hours (schema + migrations)
- **Testing:** Queue 100 tracks, kill app, restart

---

## Issue #3: Avalonia UI Freeze on Batch Loading 🟡 **HIGH**

**Current Status:** ⚠️ **PARTIAL** (Progress batching works, but batch enqueue doesn't)

### The Problem
When `QueueTracks(500 PlaylistTrack items)` is called:

```csharp
foreach (var track in tracks)  // Line ~593
{
    var ctx = new DownloadContext(track);
    _downloads.Add(ctx);
    _eventBus.Publish(new TrackAddedEvent(track));  // Fires 500 times!
    _ = SaveTrackToDb(ctx);  // DB writes 500 times!
}
```

Each `TrackAddedEvent` triggers:
1. UI observes event → adds to SourceCache
2. SourceCache triggers collection changed
3. Avalonia re-layouts entire DataGrid
4. Result: **500 UI updates in ~50ms = massive frame drop**

### Current Code Status
[ViewModels/Downloads/DownloadCenterViewModel.cs](ViewModels/Downloads/DownloadCenterViewModel.cs):
- ✅ Progress updates batched (200ms window, line 934)
- ❌ Track enqueue NOT batched (fires event per track)

### The Fix Required
Implement **Batch Enqueue to UI**:
1. Add `EnqueueBatch()` method that:
   - Collects all new tracks into temp list
   - Fires **single** `BatchTracksAddedEvent` with list
   - SourceCache adds all at once via `AddRange()`

2. Change `QueueTracks()` to NOT fire `TrackAddedEvent` per track
3. Add single batch event call at end

### Impact
- **Severity:** 🟡 HIGH (UI freeze for 2-3 seconds on large imports)
- **Scope:** Affects all users importing playlists >50 tracks
- **Fix Effort:** ~1.5 hours
- **Testing:** Import playlist with 500+ tracks, measure UI responsiveness

---

## Issue #4: Infinite Remotely Queued Deadlock 🔴 **CRITICAL**

**Current Status:** ⚠️ **PARTIAL** (5-min zombie timeout added to transfers, but no queue sweep)

### The Problem
When a peer doesn't respond after accepting a download request:
1. Transfer enters state `RemotelyQueued` 
2. No progress for 5 minutes
3. SoulseekAdapter detects stall and cancels (line ~1645)
4. BUT: If peer goes **completely radio silent**, no even/odd cancellation occurs
5. Job stays in `RemotelyQueued` state forever
6. Semaphore slot is consumed permanently
7. **Downloads get stuck indefinitely**

### Current Code Status
[Services/SoulseekAdapter.cs](Services/SoulseekAdapter.cs):
- ✅ 5-min queue timeout added (line 1644)
- ❌ No background sweep for truly stuck jobs

[Services/DownloadManager.cs](Services/DownloadManager.cs):
- ❌ No `StaleQueueSweeper` background task

### The Fix Required
Implement **Stale Queue Sweeper**:
1. Add background `System.Threading.Timer` to `DownloadManager`
2. Run every 5 minutes to:
   - Find all jobs in `PlaylistTrackState.Queued` or similar
   - Check `LastStateChangeTime`
   - If > 15 minutes without update: Cancel job, log `TimeoutException`
   - Release semaphore slot
   - Demote peer reliability score

### Impact
- **Severity:** 🔴 CRITICAL (Hangs all downloads on stuck items)
- **Scope:** Affects users downloading from unreliable peers
- **Fix Effort:** ~1.5 hours
- **Testing:** Simulate peer disconnect while in queue

---

## Issue #5: Duplication Blindness - Wasted Bandwidth 🟡 **MEDIUM**

**Current Status:** ⚠️ **PARTIAL** (Dedup within single enqueue, but not global)

### The Problem
User queues Track A at 10:00am from Peer1.  
User searches 10 minutes later, finds Track A from Peer2, queues it.  
Both download starts, creating `Track A.mp3` and `Track A (1).mp3`.

Current dedup check (line ~587):
```csharp
if (existingIds.TryGetValue(track.Id, out var byId)) existingCtx = byId;  // Only CurrentDownloads
```

This checks **only tracks actively being downloaded**, not:
- Completed downloads already on disk
- Previously downloaded files in library
- Other sources of same track

### Current Code Status
[Services/DownloadManager.cs](Services/DownloadManager.cs):
- ✅ In-flight dedup (same batch)
- ❌ No global/filesystem dedup

### The Fix Required
Implement **Pre-flight Filesystem Check**:
1. Before calling `ProcessTrackAsync()`, check:
   - Does `DestinationPath` already exist? → Skip
   - Does library have same `(FileSize, Format)` hash? → Skip
   - Is track already in different peer's queue? → Use existing instead

2. Create `CheckForDuplicate(track)` method:
   ```csharp
   private bool TrackAlreadyExists(PlaylistTrack track)
   {
       // Check file system
       if (File.Exists(track.ResolvedFilePath)) return true;
       
       // Check library
       if (_libraryService.FindByHash(track.TrackUniqueHash) != null) return true;
       
       // Check queue
       if (_downloads.Any(d => d.Model.TrackUniqueHash == track.TrackUniqueHash)) return true;
       
       return false;
   }
   ```

### Impact
- **Severity:** 🟡 MEDIUM (Wasted bandwidth, cluttered library)
- **Scope:** Affects power users who re-search frequently
- **Fix Effort:** ~1 hour
- **Testing:** Queue same track from 2 peers, verify only one downloads

---

## Implementation Priority

| Priority | Issue | Severity | Effort | Impact |
|----------|-------|----------|--------|--------|
| 🔴 **1** | Fire & Forget Peer Banning | CRITICAL | 2h | User bans |
| 🔴 **2** | Amnesia Queue | CRITICAL | 3h | Data loss |
| 🔴 **3** | Stale Queue Deadlock | CRITICAL | 1.5h | App hang |
| 🟡 **4** | Batch UI Freeze | HIGH | 1.5h | UX freeze |
| 🟡 **5** | Duplication Blindness | MEDIUM | 1h | Wasted BW |

**Total Fix Effort:** ~9 hours

---

## Testing Checklist

- [ ] Download 15 tracks from single peer, verify peer limit respected
- [ ] Queue 200 tracks, restart app, verify queue reconstructed
- [ ] Import 500-track playlist, measure UI freeze duration
- [ ] Simulate peer disconnect while queued, verify timeout + sweep
- [ ] Queue Track A from Peer1 then Peer2, verify dedup check

---

## Files to Modify

1. **DownloadManager.cs** - Add per-peer limits, stale sweep, dedup check
2. **SoulseekAdapter.cs** - Already has zombie timeout ✅
3. **AppDbContext.cs** - Add `QueuePositionEntity` migration
4. **DatabaseService.cs** - Add queue persistence methods
5. **DownloadCenterViewModel.cs** - Add batch enqueue event
6. **Events/DownloadEvents.cs** - Add `BatchTracksAddedEvent`

---

## Follow-up Work

After these 5 fixes, the queue system will need:
1. **Priority Queue Algorithm** - Weighted selection based on user preference
2. **Network Fairness** - Distribute across multiple peers for same file
3. **Bandwidth Prediction** - Estimate completion time based on speed
4. **Concurrent Format Selection** - Intelligent FLAC→MP3 fallback during enqueue
