# Unified Implementation Plan & Phased Backlog

> Status: Historical phased backlog snapshot (Phase 1 and Phase 2 completed in this stream; Phase 3 parked)
>
> See also: [Download Filtering Phase 2 Completion Report](download_filtering_phase2_completion_report.md) for current operational state.
>
> Last reviewed: 2026-05-26

This document organizes ORBIT-Pure's development roadmap into actionable slices and parked architectural epics, enabling safe, incremental, and build-gated commits.

---

## 🗺️ Backlog Index

```mermaid
graph TD
    subgraph Phase 1: Near-Term Actionable Slices (BPM & Rendering)
        S12[Slice 12: Waveform Rendering & Blob Unpacking] --> S13[Slice 13: Analysis Status Bar & Event Pipelines]
        S13 --> S14[Slice 14: Automix Optimizer Integration]
        S14 --> S15[Slice 15: Theme Alignment & Token Cleanups]
    end

    subgraph Phase 2: Safety & Quality Hardening Slices (Downloads)
        S16[Slice 16: Strict Fallback Gate & N-Format Token ORing] --> S17[Slice 17: Track Duration Proximity Filters & Scoring]
        S17 --> S18[Slice 18: MatchScorer Format/Bitrate & Fake-FLAC Hard-Fails]
        S18 --> S19[Slice 19: PrefetchVerifier Integration & Verification States]
    end

    subgraph Phase 3: Parked Epics (Future Architecture)
        PE1[Epic: Sidebar Layout Simplification] --> PE2[Epic: Global Inspector Rewiring]
    end

    S15 -.-> S16
    S19 -.-> PE1
```

---

## ⚡ Phase 1: Waveform & Automix Integration (Actionable Slices)

### Slice 12: Waveform Rendering & Blob Unpacking
* **Goal**: Fix analysis waveform visualizer.
* **Proposed Changes**:
  - **[MODIFY] [LibraryService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/LibraryService.cs)**: Update mapping from `AudioFeatures` entities to unpack the 3000-byte packed blob (`entity.AudioFeatures.WaveformBlob`) into 1000-byte Low, Mid, and High band arrays. Synthesize visual `PeakData` and `RmsData` using RMS formulas.
  - **[MODIFY] [AnalysisPageViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/AnalysisPageViewModel.cs)**: Remove the `readonly` keyword from waveform and metadata backing fields in `AnalysisTrackItem`. Add an `UpdateFrom(AnalysisTrackItem source)` method to update all fields in-place and call `RaisePropertyChanged(string.Empty)`.
  - **[MODIFY] [AnalysisPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/AnalysisPage.axaml)**: Bind the visual `WaveformControl` using `WaveformData="{Binding WaveformData}"` instead of binding bands individually.

### Slice 13: Analysis Status Bar & Event Pipelines
* **Goal**: Replace simulated delay logic with real background queue events.
* **Proposed Changes**:
  - **[MODIFY] [AnalysisQueueService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/AnalysisQueueService.cs)**: Instantiate `Progress<(int Percent, string Step)>` inside `DispatchAnalysisJobAsync` and pass progress callbacks to the compiler. Ensure `_queuedCount` is decremented in a `finally` block to fix stuck status readouts.
  - **[MODIFY] [AnalysisPageViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/AnalysisPageViewModel.cs)**: Expose real subscriptions to `TrackAnalysisCompletedEvent` and `TrackAnalysisFailedEvent` instead of simulating delays. Update selection rows in-place on completion.

### Slice 14: Automix Optimizer Integration
* **Goal**: Integrate the external `PlaylistOptimizer` into the Automix creator.
* **Proposed Changes**:
  - **[MODIFY] [AnalysisPageViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/AnalysisPageViewModel.cs)**: Inject `PlaylistOptimizer?` into constructor. Convert the `CreateAutomixPlaylist` command to run asynchronously, parsing UI constraints (MatchKey, energy weights) to the optimizer. Sort the track collection based on optimizer sequence.

### Slice 15: Theme Alignment & Token Cleanups
* **Goal**: Align the general UI with workstation themes and clean up deprecated variables.
* **Proposed Changes**:
  - **[MODIFY] [AnalysisPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/AnalysisPage.axaml)**: Replace all hardcoded green hex colors (`#1DB954`) with DynamicResource references to unified cockpit theme tokens (`BrushAccent`, `BrushAccentSubtle`, `BrushBg1`).

---

## ⚡ Phase 2: Download Filtering & Strict Mode Hardening (Actionable Slices)

### Slice 16: Strict Fallback Gate & N-Format Token ORing
* **Goal**: Prevent soft fallback bypasses and search protocol query errors.
* **Proposed Changes**:
  - **[MODIFY] [AppConfig.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Configuration/AppConfig.cs)**: Add `bool AutoDownloadAllowFuzzyFallback = false`.
  - **[MODIFY] [SettingsViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/SettingsViewModel.cs)** & **[SettingsPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/SettingsPage.axaml)** Expose and bind the toggle switch.
  - **[MODIFY] [DownloadManager.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DownloadManager.cs)**: Refactor `ResolveDiscoveryWithStrictGateAsync` to return `null` results directly if strict search fails, unless `AutoDownloadAllowFuzzyFallback` is explicitly enabled.
  - **[MODIFY] [SoulseekSearchHelper.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/AutoDownload/SoulseekSearchHelper.cs)**: Update `BuildFilterTokens` to only append `ext:FORMAT` when exactly one format is allowed.

### Slice 17: Track Duration Proximity Filters & Scoring
* **Goal**: Block DJ sets and wrong edits by enforcing duration tolerance gates.
* **Proposed Changes**:
  - **[MODIFY] [AppConfig.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Configuration/AppConfig.cs)**: Add `int AutoDownloadDurationToleranceSeconds = 3`.
  - **[MODIFY] [SettingsViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/SettingsViewModel.cs)** & **[SettingsPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/SettingsPage.axaml)**: Bind the new numeric up-down controls.
  - **[MODIFY] [SoulseekSearchHelper.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/AutoDownload/SoulseekSearchHelper.cs)**: In `FilterCandidates`, evaluate `targetTrack.CanonicalDuration`. Reject candidates deviating by more than the configured tolerance seconds.
  - **[MODIFY] [MatchScorer.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/AutoDownload/MatchScorer.cs)**: Re-weight scoring parameters to allocate 20% to duration matching, calculating proximity on a sliding scale.

### Slice 18: MatchScorer Format/Bitrate & Fake-FLAC Hard-Fails
* **Goal**: Tighten scoring constraints and reject invalid formats or upscaled files.
* **Proposed Changes**:
  - **[MODIFY] [MatchScorer.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/AutoDownload/MatchScorer.cs)**: Update `ScoreFormat` to return `0.0` for any unallowed formats. Update `ScoreBitrate` to flag FLACs under 400kbps and trigger a hard fail (returning `0.0` for the overall candidate score).
  - **[MODIFY] [AppConfig.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Configuration/AppConfig.cs)**: Add `int AutoDownloadMinMatchScore = 75`.
  - **[MODIFY] [SettingsViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/SettingsViewModel.cs)** & **[SettingsPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/SettingsPage.axaml)** Expose the minimum score threshold.
  - **[MODIFY] [AutoSearchService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/AutoDownload/AutoSearchService.cs)**: In `SelectBestCandidateAsync`, reject candidates scoring below `AutoDownloadMinMatchScore` (returning `null`).

### Slice 19: PrefetchVerifier Integration & Verification States
* **Goal**: Wire the post-download verifier to run Essentia and check staging sizes.
* **Proposed Changes**:
  - **[MODIFY] [DownloadManager.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DownloadManager.cs)**: Inject `PrefetchVerifier` into the constructor. In the download completion rename block, invoke `VerifyDownloadAsync`. If verification fails, delete the file and transition the track to `Failed` with `DownloadFailureReason.FileVerificationFailed`.

---

## ⏸️ Phase 3: Sidebar Unification & Layout Redesign (Parked Epic)

* **Goal**: Simplify workspace aesthetics, remove double sidebars, and direct panel states (Single Inspector, Double Inspector, and Playlist Intelligence) through the global Sliding Right Panel.
* **Scope**:
  - Remove grid splitters and local panel grids from `LibraryPage.axaml`.
  - Introduce `LibraryDoubleInspectorViewModel` and `PlaylistIntelligenceViewModel`.
  - Create the custom `PlaylistIntelligencePanel.axaml` and register bindings on `MainWindow.axaml`.
  - Re-route Selection and Project change actions to publish Sidebar open/close commands.

---

## 🧪 Verification Plan

### Automated Test Suites
For each actionable slice, we will un-stub or create unit tests:
```powershell
# Execute Phase 1 tests
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~Analysis"

# Execute Phase 2 tests
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~AutoDownload"
```

### Manual QA Checkpoints
1. Verify waveforms render cleanly on the track detail cards.
2. Confirm the Automix reorders tracks according to BPM and Harmonic keys without locking the UI thread.
3. Verify that strict mode search does **not** perform fallback searches when no exact formats match the criteria.
4. Verify that DJ sets and transcoded FLACs are rejected during candidate collection.
