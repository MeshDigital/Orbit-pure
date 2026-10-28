# Testing Plan: Batch Action FAB End-to-End Verification

This document outlines the testing, verification, and implementation roadmap to ensure full functionality for the floating batch actions bar at the bottom of the Library screen.

---

## 🔍 Current Implementation Analysis

We analyzed the ViewModels and found that while some operations are fully functional, others are placeholders that log intent. The plan covers both testing the active features and completing the placeholders.

| Option | Command | Current Status |
| :--- | :--- | :--- |
| **Play** | `Tracks.Operations.PlayTrackCommand` | **Fully Implemented** (Clears queue, plays selection) |
| **Add to Queue** | `Tracks.Operations.AddSelectedToQueueCommand` | **Fully Implemented** (Appends selection to active queue) |
| **Tag Edit** | `BatchTagEditCommand` | **Placeholder** (Logs intent only; dialog pending) |
| **Analyse** | `BatchQueueAnalysisCommand` | **Fully Implemented** (Queues analysis via `EventBus`) |
| **Add to Playlist** | `BatchAddToPlaylistCommand` | **Placeholder** (Logs intent only; dialog pending) |
| **Rekordbox** | `BatchExportRekordboxCommand` | **Fully Implemented** (Exports Rekordbox XML to Documents) |
| **Clear (✕)** | `BatchClearSelectionCommand` | **Fully Implemented** (Deselects tracks, hides FAB) |

---

## 🛠️ End-to-End Verification Protocols

### 1. Play & Add to Queue (Audio Integration)
* **Goal**: Verify that selection plays immediately or appends to the queue without stuttering.
* **Test Case**:
  1. Select 3 tracks in [TrackListView.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/TrackListView.axaml).
  2. Click **Play**: Verify the first track plays immediately in the workstation deck or main player, and the active queue has exactly the remaining 2 tracks.
  3. Select 2 different tracks, click **Add to Queue**: Verify they are appended to the tail of the play queue without interrupting the current playback.

### 2. Batch Audio Analysis (`🔬 Analyse`)
* **Goal**: Verify that batch analysis jobs run concurrently and update status indicators.
* **Test Case**:
  1. Select 5 unanalyzed tracks and click **Analyse**.
  2. Verify that a success notification toast appears saying `5 track(s) queued for audio analysis`.
  3. Navigate to the **Analysis** page: Verify all 5 tracks appear in the queue with a status of `Queued` or `Processing`.
  4. Verify that once completed, the BPM, key, and waveform are saved to the database and displayed inline in the grid.

### 3. Rekordbox XML Export (`📤 Rekordbox`)
* **Goal**: Verify that track metadata is exported to a valid Rekordbox XML file.
* **Test Case**:
  1. Select 4 tracks and click **Rekordbox**.
  2. Verify a notification appears confirming the export path in your `My Documents` folder (e.g. `orbit-rekordbox-2026xxxx-xxxxxx.xml`).
  3. Locate the file and open it in a text editor to verify the XML structure:
     - Check that `<TRACKS>` nodes are populated.
     - Validate that track properties (BPM, Artist, Title, MusicalKey, and Location paths) are correct.

### 4. Clear Selection (`✕` Close Affordance)
* **Goal**: Verify selection state resets cleanly.
* **Test Case**:
  1. Select multiple tracks to make the FAB appear.
  2. Click the `✕` button on the far right.
  3. Verify all tracks are visually deselected, selection counts reset, and the FAB transitions out.

---

## 🛠️ Placeholder Completion & Implementation Plan

To achieve **100% full functionality**, we propose implementing the dialog systems for the two missing features:

### A. Batch Tag Edit
* **Implementation Plan**:
  1. Create a `BatchTagEditDialog` showing common fields (Artist, Album, Genre, Year).
  2. Prompt the user for values. Leave fields blank to keep original track values, or fill them to overwrite.
  3. Save the edits to the metadata tags on the physical files using TagLib#, and update corresponding DB entries in `AppDbContext`.

### B. Add to Playlist
* **Implementation Plan**:
  1. Create a `PlaylistPickerDialog` that queries all playlists from the database.
  2. Allow selecting an existing playlist or entering a name to create a new one.
  3. Insert corresponding `PlaylistTrackEntity` entries linked to the selected tracks in `AppDbContext`.

---

## 🎨 UI & Readability Refinements (Overlapping Text Fix)

Your screenshots reveal that track list text from the row underneath the FAB is bleeding through, making the buttons difficult to read. We propose updating the floating border style in [LibraryPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/LibraryPage.axaml#L327-L332):

1. **Increase Background Opacity**:
   Change background from `#E01E1E2E` (semi-transparent) to a more opaque or solid dark theme brush `#F31E1E2E` or `#FF1E1E2E`.
2. **Add Background Blur / Acrylic effect**:
   Introduce a solid backdrop or use Avalonia's `ExperimentalAcrylicBorder` inside the FAB template to blur underlying text, ensuring the buttons pop out clearly.
3. **Drop Shadow Enhancements**:
   Enlarge the shadow wrapper to separate the floating bar from the list content.

---

## 📋 Actions Required

Please review this plan. If you approve, we will:
1. Fix the translucency overlapping issue on the FAB.
2. Implement the `PlaylistPickerDialog` for **Add to Playlist**.
3. Implement the `BatchTagEditDialog` for **Tag Edit**.
