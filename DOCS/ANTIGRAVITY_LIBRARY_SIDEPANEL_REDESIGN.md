# Antigravity — Library Sidepanel Redesign

> Status: Draft
>
> Created: 2026-06-17
>
> Scope: Inspector panel UX overhaul · track removal bug · meta-edit filename gap
>
> See also: [DOCS/memory/MEMORY_INDEX.md](memory/MEMORY_INDEX.md)

---

## 0. Problem Statement

The library sidepanel (`LibraryTrackInspector.axaml`, 407 lines) dumps every data category into one continuous scroll: artwork, waveform, status badges, track info, musical analysis, Camelot wheel, four progress bars, vibe chips, cue points, energy curve, vocal analysis — all visible at once. It is cognitively overwhelming and makes the three things a user actually wants to do (understand, edit, act) all equally prominent.

Two additional defects compound this:

1. **Removing a track does nothing in the UI.** `TrackRemovedEvent` is published by `DownloadManager` but `LibraryViewModel` has no subscriber; the card stays visible after deletion.
2. **Meta-edit cannot rename a track.** `BatchTagEditViewModel` offers Artist / Album / Genre / Year but not Title and not FileName, so there is no in-app path to fix a wrongly-named file.

---

## 1. Current Architecture (as-is)

```
LibraryPage.axaml
└── [right panel] LibraryTrackInspector.axaml  ← single giant scroll
    ├── Artwork + primary info
    ├── Waveform strip + phrase strip
    ├── Status badges
    ├── Track Info  (Expander, collapsed by default)
    ├── Musical Analysis  ← always shown
    │   ├── BPM / Key / Energy / Loudness / True Peak / Dynamic Range
    │   ├── Camelot wheel (160 px)
    │   └── Energy / Arousal / Valence / Danceability bars
    ├── Vibe & Context  (Expander, collapsed by default)
    ├── Cue Points  ← always shown
    └── Deep Analysis  (Expander, collapsed by default)
        ├── Beat grid confidence
        ├── Audio profile radar
        ├── Energy curve
        └── Vocal analysis

Bottom bar:
    [WORKSTATION]  [REVEAL IN EXPLORER]
```

**Bottom bar gap:** No Remove button. No edit button. The only action that handles the track as a library object is buried in a right-click context menu.

---

## 2. Redesign Goals

| Goal | Detail |
|---|---|
| **Progressive disclosure** | Show identity instantly; surface analysis on demand; never show 10 sections simultaneously |
| **Three-tab model** | Separate _what it is_ (Identity) from _what the numbers say_ (Analysis) from _what you can do_ (Actions) |
| **First-class actions** | Remove and Rename must be reachable in one click, not buried in a context menu |
| **Fix both bugs** | Track removal propagates to the library collection; meta-edit covers Title + FileName |
| **Compact by default** | Default panel width 320 px; tab content fits without horizontal scroll |

---

## 3. New Inspector Layout

### 3.1 Header (always visible, ~140 px)

```
┌─────────────────────────────────────────────────┐
│  [120×120 artwork]   Artist Name (muted)        │
│                      Track Title  (bold, 18px)  │
│                      Album Name   (dim)         │
│                      [FLAC] [1042 kbps]  [LOSSLESS] │
└─────────────────────────────────────────────────┘
```

The integrity badge and format chips move into the header row so they're instantly scannable.

### 3.2 Waveform strip (always visible, 72 px)

Compact waveform with cue markers. Phrase strip below it. No section header needed — it's a visual strip, not a table.

### 3.3 Tab bar

Three tabs. The active tab label is the only interactive element between the waveform and the tab content.

```
[ IDENTITY ]  [ ANALYSIS ]  [ ACTIONS ]
```

Tab state persists per session via a `SelectedInspectorTab` int on `LibraryViewModel` (0/1/2).

---

### 3.4 Tab 0 — IDENTITY

Primary facts a user needs to orient: what is this track, where did it come from, what are its cue points.

```
STATUS
  [Completed]  [Lossless]  [DJ TOOL?]

TRACK DETAILS (always visible, 2×3 grid)
  Duration   Bitrate   Format
  Sample rate  File size  Year

CUE POINTS
  [● Drop]  0:32.4
  [● Break] 1:04.1
  ...

SOURCE
  Soulseek / peer-name / date
```

Wipe the `Track Info` Expander — that data is always shown here. Wipe the `Vibe & Context` Expander from this tab; it moves to Analysis.

---

### 3.5 Tab 1 — ANALYSIS

All DSP-derived data. This is the tab a mixing-prep user opens; a casual browsing user never needs it.

```
MUSICAL
  BPM ·  KEY ·  ENERGY
  Loudness · True Peak · Dynamic Range

CAMELOT WHEEL  (160 px, click-to-filter)

SONIC PROFILE
  Energy      ████████░░  82%
  Arousal     ██████░░░░  61%
  Valence     ████░░░░░░  44%
  Danceability██████████  97%

VIBE
  [house] [melodic] [hypnotic]  genre text

A10 PAIR SNAPSHOT  (only when neighbor context exists)
  ...

DEEP ANALYSIS  (collapsible)
  BPM confidence · Audio radar · Energy curve · Vocal analysis
```

---

### 3.6 Tab 2 — ACTIONS

Replaces the thin bottom bar and adds the two missing operations.

```
FILE OPERATIONS
  [ 📂 Reveal in Explorer ]
  [ 🎚 Open in Workstation ]
  [ ✏️ Edit Metadata       ]   ← opens meta-edit sheet
  [ 🗑  Remove Track        ]   ← styled destructive red

ANALYSIS
  [ 🔬 Re-analyse          ]

GHOST FILE (only when file is missing)
  [ 🔍 Search Again        ]
  [ 📁 Locate File         ]
```

The Remove button is styled with a red accent (`#C0392B`) and requires a single confirm (inline toggle: first click shows "Are you sure? [Confirm Remove]", second click executes).

---

## 4. Bug 1 — Track Removal Not Reflected in Library

### Root cause

`DownloadManager.DeleteTrackFromDiskAndHistoryAsync` publishes `TrackRemovedEvent` (line 1062), but `LibraryViewModel` never subscribes to it. Only `DownloadCenterViewModel` listens.

### Fix

**File:** `ViewModels/LibraryViewModel.cs` (constructor / `Initialize`)

```csharp
_eventBus.GetEvent<TrackRemovedEvent>()
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(evt => OnTrackRemoved(evt.TrackGlobalId))
    .DisposeWith(_disposables);
```

```csharp
private void OnTrackRemoved(string globalId)
{
    // Remove from the virtualised track collection (all active playlists)
    var toRemove = Tracks.Items
        .Where(t => t.GlobalId == globalId)
        .ToList();
    foreach (var t in toRemove)
        Tracks.Remove(t);

    // If the removed track is the current inspector subject, close the panel
    if (InspectorTrack?.GlobalId == globalId)
        InspectorTrack = null;
}
```

`TrackRemovedEvent` is already in `SLSKDONET.Models` (declared in `Events/DownloadEvents.cs`). No new event needed.

Also add a null/empty guard in `ExecuteRemoveTrack` in `TrackOperationsViewModel` so that trying to remove a ghost track (no file on disk) still clears the DB entry without crashing:

```csharp
private async Task ExecuteRemoveTrack(PlaylistTrackViewModel? track)
{
    track ??= LibraryViewModel?.Tracks.LeadSelectedTrack;
    if (track == null) return;

    // Confirm
    bool confirmed = await _dialogService.ConfirmAsync(
        "Remove Track",
        $"Remove \"{track.TrackTitle}\" from your library and delete the file?");
    if (!confirmed) return;

    try
    {
        _logger.LogInformation("Removing track: {Title}", track.TrackTitle);
        await _downloadManager.DeleteTrackFromDiskAndHistoryAsync(track.GlobalId);
        // TrackRemovedEvent will drive the LibraryViewModel collection update
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to remove track");
        _eventBus.Publish(new NotificationEvent("Remove Track", "Failed to remove track.", NotificationType.Error));
    }
}
```

---

## 5. Bug 2 — Meta Edit Cannot Edit Title or FileName

### Root cause

`BatchTagEditViewModel` / `BatchTagEditResult` only have `Artist`, `Album`, `Genre`, `Year`. No `Title`. No `NewFileName`.

File renaming is entirely absent from the codebase — there is no service method that renames a file and propagates the new path through `PlaylistTrack.ResolvedFilePath`, `LibraryEntry.LocalFilePath`, and the DB.

### Fix — Part A: Add Title to BatchTagEditViewModel

**File:** `ViewModels/Library/BatchTagEditViewModel.cs`

```csharp
public class BatchTagEditResult
{
    public bool IsConfirmed { get; set; }
    public string? Artist   { get; set; }
    public string? Title    { get; set; }   // ← new
    public string? Album    { get; set; }
    public string? Genre    { get; set; }
    public string? Year     { get; set; }
    public string? NewFileName { get; set; } // ← new (without extension)
}
```

Add `Title` and `NewFileName` properties to `BatchTagEditViewModel` following the same `RaiseAndSetIfChanged` pattern. `CanSave` must also check `!string.IsNullOrWhiteSpace(Title)` and `!string.IsNullOrWhiteSpace(NewFileName)`.

**File:** `Views/Avalonia/Dialogs/BatchTagEditDialog.axaml`

Add two `TextBox` rows: "Track Title" and "File Name (without extension)". The FileName field should show the current filename as placeholder text.

### Fix — Part B: File Rename in ExecuteBatchTagEditAsync

**File:** `ViewModels/LibraryViewModel.Commands.cs` — `ExecuteBatchTagEditAsync`

After writing the TagLib tags, if `result.NewFileName` is non-empty and exactly one track is selected:

```csharp
if (!string.IsNullOrWhiteSpace(result.NewFileName) && selected.Count == 1)
{
    var track = selected[0];
    var sourcePath = track.Model.ResolvedFilePath;
    if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
    {
        var dir  = Path.GetDirectoryName(sourcePath)!;
        var ext  = Path.GetExtension(sourcePath);
        // Sanitise: strip path separators so the user can't escape the directory
        var safeName = result.NewFileName
            .Replace('/', '_').Replace('\\', '_')
            .Replace(':', '_').Trim();
        var destPath = Path.Combine(dir, safeName + ext);

        if (!File.Exists(destPath))
        {
            File.Move(sourcePath, destPath);

            // Propagate new path through DB
            await _libraryService.UpdateTrackFilePathAsync(track.GlobalId, destPath);

            // Update the in-memory model so the UI reflects the new path immediately
            track.Model.ResolvedFilePath = destPath;
            track.RaisePropertyChanged(nameof(track.FileName));
        }
        else
        {
            _notificationService.Show("Rename", $"A file named '{safeName + ext}' already exists.", NotificationType.Warning);
        }
    }
}
```

### Fix — Part C: ILibraryService.UpdateTrackFilePathAsync

**File:** `Services/ILibraryService.cs` — add:

```csharp
Task UpdateTrackFilePathAsync(string globalId, string newFilePath);
```

**File:** `Services/LibraryService.cs` — implement:

```csharp
public async Task UpdateTrackFilePathAsync(string globalId, string newFilePath)
{
    await using var context = _dbFactory.CreateDbContext();
    var track = await context.PlaylistTracks
        .FirstOrDefaultAsync(t => t.UniqueHash == globalId);
    if (track != null)
    {
        track.ResolvedFilePath = newFilePath;
        await context.SaveChangesAsync();
    }
    // Also update LibraryEntry if it exists
    var entry = await context.LibraryEntries
        .FirstOrDefaultAsync(e => e.TrackUniqueHash == globalId);
    if (entry != null)
    {
        entry.LocalFilePath = newFilePath;
        await context.SaveChangesAsync();
    }
}
```

**Note:** FileName editing is intentionally single-track only. Batch rename on multiple tracks with an arbitrary string would produce collisions. The dialog should grey out the FileName field and show "(N tracks selected — filename editing not available for multiple tracks)" when more than 1 track is selected.

---

## 6. Inspector ViewModel Changes

A new `SelectedInspectorTab` property on `PlaylistTrackViewModel` (or on `LibraryViewModel`) persists the active tab between inspector re-opens within a session.

```csharp
// ViewModels/LibraryViewModel.cs
private int _selectedInspectorTab;
public int SelectedInspectorTab
{
    get => _selectedInspectorTab;
    set => this.RaiseAndSetIfChanged(ref _selectedInspectorTab, value);
}
```

Bind `TabControl.SelectedIndex` to this property. Default: 0 (Identity).

---

## 7. AXAML Structural Change

`LibraryTrackInspector.axaml` becomes:

```xml
<Border Background="#1A1B1C" ...>
  <Grid RowDefinitions="Auto, Auto, Auto, *, Auto">

    <!-- Row 0: Header — artwork + identity chips -->
    <controls:InspectorHeader Grid.Row="0" .../>

    <!-- Row 1: Waveform strip (always) -->
    <controls:InspectorWaveformStrip Grid.Row="1" .../>

    <!-- Row 2: Tab bar -->
    <TabControl Grid.Row="3" SelectedIndex="{Binding SelectedInspectorTab, ...}">
      <TabItem Header="IDENTITY">
        <controls:InspectorIdentityTab/>
      </TabItem>
      <TabItem Header="ANALYSIS">
        <controls:InspectorAnalysisTab/>
      </TabItem>
      <TabItem Header="ACTIONS">
        <controls:InspectorActionsTab/>
      </TabItem>
    </TabControl>

  </Grid>
</Border>
```

Each tab content is extracted into its own `UserControl` to keep file sizes manageable. The main inspector file drops from ~407 lines to ~60.

---

## 8. Delivery Sequence

| Step | Scope | Risk |
|---|---|---|
| **Step 1** — Bug: track removal | `LibraryViewModel.cs` subscribe to `TrackRemovedEvent`; add confirm to `TrackOperationsViewModel` | Low — additive subscription |
| **Step 2** — Bug: meta-edit title + filename | `BatchTagEditViewModel`, `BatchTagEditDialog.axaml`, `LibraryViewModel.Commands.cs`, `ILibraryService`, `LibraryService` | Medium — new file rename path |
| **Step 3** — Inspector tab scaffold | New tab structure in `LibraryTrackInspector.axaml`, `SelectedInspectorTab` property | Low — structural only, no data changes |
| **Step 4** — Identity tab | Extract Track Details + Status + Cue Points from current inspector | Low |
| **Step 5** — Analysis tab | Move Musical Analysis + Camelot + bars + Vibe + Deep Analysis | Low |
| **Step 6** — Actions tab | Add Remove button with inline confirm, re-analyse, ghost-file actions | Medium — new UI interactions |
| **Step 7** — Header refinement | Merge format/integrity/ghost chips into header, remove from tab bodies | Low |

Steps 1 and 2 are standalone bug fixes and can ship before the visual redesign. Steps 3–7 are purely presentational changes that don't touch service or DB layers.

---

## 9. Out of Scope

- **Inline tag editing in the inspector panel itself** (not in this initiative; meta-edit dialog covers the use case)
- **Bulk rename with template patterns** (e.g. `{artist} - {title}`) — parked; too much edge-case surface
- **Inspector panel resizing** — the panel width stays fixed at 320 px for this pass
