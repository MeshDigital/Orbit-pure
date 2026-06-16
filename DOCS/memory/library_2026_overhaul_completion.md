# Library 2026 Overhaul (Hybrid Track State Model) Completion Report

Date: 2026-06-12  
Status: Completed  

## Executive Summary

The **Library 2026 Overhaul** introduced the **Hybrid Track State Model** to ORBIT. This model decouples track representation from physical existence on disk by supporting five states:
1. `Ghost` (metadata-only)
2. `QueuedForDownload` (accepted, waiting in Soulseek queue)
3. `Downloading` (active Soulseek download)
4. `LocalUnanalyzed` (downloaded successfully, awaiting analysis)
5. `Ready` (analyzed, cues & stems prepared, ready for mix play)

All aspects of the implementation plan have been completed and validated.

---

## Deliverables

### 1. UI/UX Modernization
* **Acquire Missing Tracks Command**:
  - Implemented `AcquireMissingTracksCommand` in [LibraryViewModel.Commands.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/LibraryViewModel.Commands.cs).
  - This allows the user to manually trigger download acquisition for all `Ghost` and `OnHold` tracks in a playlist, resetting search retry counters and queuing them bulk-style via the `DownloadManager`.
* **StandardTrackRow UI/UX Upgrade**:
  - Updated [StandardTrackRow.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Controls/StandardTrackRow.axaml) styling to support `Ghost` state.
  - Added **0.45 Opacity Dimming** to easily distinguish `Ghost` tracks from local files, with hover transition to **0.75 Opacity**.
  - Added a circular **Spotify Branding Badge** overlay on the artwork panel to demarcate streaming source provenance.
  - Added an **Acquire Button** in the actions hub using `cloud_download_regular` stream geometry, permanently visible on `Ghost` rows to allow immediate single-click downloads.

### 2. Workstation Guards & Interception
* **Deck Guard**:
  - Updated `LoadPlaylistTrackAsync` inside [WorkstationDeckViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Workstation/WorkstationDeckViewModel.cs) to block loading `Ghost` tracks into workstation decks.
  - Dragging or double-clicking a `Ghost` track is intercepted safely, triggering a toast notification (`"Track not local. Queuing for download..."`) and auto-queueing the track in the `DownloadManager` for Soulseek acquisition.

### 3. Verification & Tests
* **Unit Tests**:
  - Added the `LoadPlaylistTrackCommand_InterceptsGhostTrack_QueuesDownloadAndFailsLoad` fact in [WorkstationDeckViewModelTests.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Tests/SLSKDONET.Tests/ViewModels/WorkstationDeckViewModelTests.cs) to assert that:
    1. Interception successfully handles `Ghost` availability states.
    2. No audio loading/playback crashes occur.
    3. Proper error message is reported via `TrackLoadError`.
