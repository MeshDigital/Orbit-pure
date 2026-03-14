# TODO: Active Downloads Tracking Implementation

## Overview
Complete the playlist card active downloads feature by implementing runtime download state queries in DownloadManager.

---

## Current Status
- ✅ UI complete (playlist cards show stats placeholders)
- ✅ Data model updated (`PlaylistJob.ActiveDownloadsCount`, `CurrentDownloadingTrack`)
- ✅ EventBus subscription wired up in `ProjectListViewModel`
- ⚠️ Backend methods missing (returns 0/null placeholders)

---

## Required Implementation

### 1. Add Method to DownloadManager: `GetActiveDownloadsForProject`

**File**: `Services/DownloadManager.cs`

**Signature**:
```csharp
public int GetActiveDownloadsForProject(Guid projectId)
```

**Logic**:
1. Access `_downloadContexts` dictionary
2. Filter by `context.PlaylistId == projectId`
3. Count only tracks where `context.State == PlaylistTrackState.Downloading`
   - **Exclude** `Queued` (waiting to start)
   - **Exclude** `Searching` (not yet downloading) 
   - **Include** only active transfers
4. Consider concurrency limit (2/4/6 simultaneous downloads)
5. Return count

**Example**:
```csharp
public int GetActiveDownloadsForProject(Guid projectId)
{
    lock (_downloadContexts)
    {
        return _downloadContexts.Values
            .Where(ctx => ctx.PlaylistId == projectId)
            .Count(ctx => ctx.State == PlaylistTrackState.Downloading);
    }
}
```

---

### 2. Add Method to DownloadManager: `GetCurrentlyDownloadingTrack`

**File**: `Services/DownloadManager.cs`

**Signature**:
```csharp
public PlaylistTrack? GetCurrentlyDownloadingTrack(Guid projectId)
```

**Logic**:
1. Access `_downloadContexts` dictionary
2. Filter by `context.PlaylistId == projectId`
3. Filter by `context.State == PlaylistTrackState.Downloading`
4. Order by start time or queue position (if tracked)
5. Take first/current track
6. Return associated `PlaylistTrack` model

**Example**:
```csharp
public PlaylistTrack? GetCurrentlyDownloadingTrack(Guid projectId)
{
    lock (_downloadContexts)
    {
        var downloadingContext = _downloadContexts.Values
            .Where(ctx => ctx.PlaylistId == projectId)
            .FirstOrDefault(ctx => ctx.State == PlaylistTrackState.Downloading);
            
        if (downloadingContext == null) return null;
        
        // Get the actual track model from database or memory
        return _libraryService.FindPlaylistTrackByHash(downloadingContext.GlobalId);
    }
}
```

---

### 3. Update `ProjectListViewModel.OnTrackStateChanged`

**File**: `ViewModels/Library/ProjectListViewModel.cs`

**Current Code** (lines 140-158):
```csharp
private void OnTrackStateChanged(Events.TrackStateChangedEvent evt)
{
    // TODO: Implement full active downloads tracking
    // ...placeholders...
}
```

**Replace With**:
```csharp
private void OnTrackStateChanged(Events.TrackStateChangedEvent evt)
{
    Dispatcher.UIThread.InvokeAsync(() =>
    {
        // Update stats for all projects (or optimize to only affected project)
        foreach (var project in AllProjects)
        {
            project.RefreshStatusCounts(); // Database stats (Downloaded, Failed, Missing)
            
            // Query runtime state from DownloadManager
            project.ActiveDownloadsCount = _downloadManager.GetActiveDownloadsForProject(project.Id);
            
            var currentTrack = _downloadManager.GetCurrentlyDownloadingTrack(project.Id);
            project.CurrentDownloadingTrack = currentTrack != null
                ? $"{currentTrack.Artist} - {currentTrack.Title}"
                : null;
        }
    });
}
```

**Optimization** (optional):
- Instead of updating all projects, find which project the changed track belongs to
- Only update that specific project's stats
- Reduces unnecessary UI updates

---

## Data Flow

### Track Download Starts
```
DownloadManager.StartDownload(track)
  → Sets context.State = Downloading
  → EventBus publishes TrackStateChangedEvent
  → ProjectListViewModel.OnTrackStateChanged()
  → Calls GetActiveDownloadsForProject()
  → Returns 1 (or more if multiple active)
  → Calls GetCurrentlyDownloadingTrack()
  → Returns "Artist - Title"
  → UI updates: Card shows "↓ 1" and "↓ Artist - Title"
```

### Track Download Completes
```
DownloadManager.CompleteDownload(track)
  → Sets context.State = Completed
  → EventBus publishes TrackStateChangedEvent
  → ProjectListViewModel.OnTrackStateChanged()
  → Calls GetActiveDownloadsForProject()
  → Returns 0 (no more active)
  → Calls GetCurrentlyDownloadingTrack()
  → Returns null
  → UI updates: Card hides "↓ 0" and current track badge
```

---

## Edge Cases to Handle

### 1. Concurrency Limit
- Only count tracks **actively downloading**, not queued
- If limit is 2, max `ActiveDownloadsCount` should be 2
- `Queued` tracks should not increment the count

### 2. Project with No Downloads
- `GetActiveDownloadsForProject()` returns 0
- `GetCurrentlyDownloadingTrack()` returns null
- Card stats hide (using `IsVisible` binding)

### 3. Multiple Projects Downloading
- Each project tracks independently
- `ActiveDownloadsCount` is per-project, not global
- Card for Project A shows its active downloads
- Card for Project B shows its active downloads

### 4. Track State Transitions
- `Searching` → `Queued` → `Downloading` → `Completed`
- Only `Downloading` should increment active count
- Badge should appear when state reaches `Downloading`
- Badge should disappear when state leaves `Downloading`

---

## Testing Checklist

### Unit Tests
- [ ] `GetActiveDownloadsForProject()` returns 0 when no downloads
- [ ] `GetActiveDownloadsForProject()` counts only Downloading state
- [ ] `GetActiveDownloadsForProject()` excludes Queued tracks
- [ ] `GetCurrentlyDownloadingTrack()` returns null when no downloads
- [ ] `GetCurrentlyDownloadingTrack()` returns correct track when downloading

### Integration Tests
- [ ] Import playlist with 10 tracks
- [ ] Click search on 5 tracks
- [ ] Verify card shows active downloads incrementing
- [ ] Verify "Currently downloading:" badge appears
- [ ] Verify badge updates to next track when one completes
- [ ] Verify badge disappears when all complete

### Manual Tests
- [ ] Import Spotify playlist
- [ ] Initiate downloads
- [ ] Watch playlist card update in real-time
- [ ] Verify stats match actual download state
- [ ] Pause/resume downloads → Verify counts update
- [ ] Navigate between projects → Verify each has correct stats

---

## Files to Modify

1. **`Services/DownloadManager.cs`**
   - Add `GetActiveDownloadsForProject(Guid projectId)`
   - Add `GetCurrentlyDownloadingTrack(Guid projectId)`

2. **`ViewModels/Library/ProjectListViewModel.cs`**
   - Update `OnTrackStateChanged()` method (lines 140-158)
   - Remove placeholder logic
   - Call new DownloadManager methods

---

## Estimated Effort
- ⏱️ **2-3 hours** for implementation
- ⏱️ **1 hour** for testing
- ⏱️ **Total: 3-4 hours**

---

## Dependencies
- Requires access to `DownloadManager._downloadContexts` (currently private)
- May need to expose via public methods or properties
- Ensure thread-safety (use locks if accessing from UI thread)

---

## Success Criteria
- ✅ Playlist cards show real active download counts
- ✅ "Currently downloading" badge appears with track name
- ✅ Stats update in real-time as downloads progress
- ✅ Performance impact is minimal (< 50ms per update)
- ✅ No race conditions or thread safety issues
- ✅ Build succeeds with 0 warnings

---

## Related Issues
- Playlist cards enhancement (Phase 11)
- Library-first design philosophy
- Real-time download monitoring

---

**Created**: December 21, 2025  
**Status**: Pending Implementation  
**Priority**: High (completes 40% of Phase 11)
