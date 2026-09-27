# Library Waveform & Automix Plan (In-Depth Plan)

> Status: Historical execution blueprint (Phase 1 slices 12-15 landed in this stream)
>
> See also: [Unified Implementation Plan & Phased Backlog](download_filtering_implementation_plan.md), [Download Filtering Phase 2 Completion Report](download_filtering_phase2_completion_report.md)
>
> Last reviewed: 2026-05-26

Date: 2026-05-23
Status: Historical execution plan

## 1. Waveform Rendering Failure during Analysis

### Diagnosis
1. **Unpacked Waveform Disconnect**: The audio analysis pipeline (in `WaveformExtractionService.cs`) processes decoded audio and outputs a 3000-byte packed array representing three frequency bands (Low, Mid, High, each 1000 samples) stored inside `AudioFeaturesEntity.WaveformBlob`.
2. **Missing Database Mapping**: Legacy columns `LowData`, `MidData`, and `HighData` on `LibraryEntryEntity` and `TrackTechnicalEntity` remain null/empty. During EF Core entity-to-model mapping in `LibraryService.cs` (`EntityToLibraryEntry` and `EntityToPlaylistTrack`), these legacy columns are mapped directly. Because they are null, the resulting domain models contain empty arrays for these frequency bands.
3. **Control Early Exit**: `WaveformControl.cs` binds `LowBand`, `MidBand`, and `HighBand` in `AnalysisPage.axaml`. However, the control's `Render()` method checks `WaveformData` (specifically `data.IsEmpty` checking if `PeakData` and `RmsData` are empty). Since `WaveformData` is not bound in `AnalysisPage.axaml` and defaults to null/empty, the control immediately exits early and renders a flat gray line.

### Proposed Changes
1. **Service-Level Unpacking**: Modify `LibraryService.cs` mapping methods (`EntityToLibraryEntry` and `EntityToPlaylistTrack`) to check if `entity.AudioFeatures?.WaveformBlob` contains a valid packed array (3000 bytes). If so, unpack it into `LowData`, `MidData`, and `HighData` (1000 bytes each) and synthesize `WaveformData` (Peak) and `RmsData` on the fly:
   - Peak: `PeakData[i] = Math.Max(LowData[i], Math.Max(MidData[i], HighData[i]))`
   - RMS: `RmsData[i] = (byte)Math.Clamp(Math.Sqrt((LowData[i]*LowData[i] + MidData[i]*MidData[i] + HighData[i]*HighData[i]) / 3.0), 0, 255)`
2. **Expose Waveform to VM**: Update `AnalysisTrackItem` inside `AnalysisPageViewModel.cs` to expose a `WaveformData` property of type `WaveformAnalysisData` constructed from its bands and duration, ensuring it is non-empty.
3. **Bind WaveformData in XAML**: Update `AnalysisPage.axaml` to bind `WaveformData="{Binding WaveformData}"` on the `controls:WaveformControl` instance in the "Insight details" expander.
4. **Make VM Backing Fields Settable**: Remove the `readonly` modifier from `AnalysisTrackItem` backing fields (e.g. `_waveformLow`, `_waveformMid`, `_waveformHigh`, `_cueCount`, etc.) and add an `UpdateFrom(AnalysisTrackItem source)` method to allow in-place updates.

---

## 2. Incomplete / Mock Analysis Process

### Diagnosis
1. **Mock Execution**: Clicking "Start Analysis" on the Analysis page calls `StartAnalysisAsync()` in `AnalysisPageViewModel.cs`. This method loops through queued tracks, updates their status to `Processing`, calls `SimulateTrackAnalysisAsync` (which pauses 500ms per step to simulate progress), and calls `GenerateMockAnalysisData`.
2. **Disconnected Pipeline**: The real, fully functional background processing queue managed by `AnalysisQueueService` is resolved on startup but never gets triggered by the UI because `TrackAnalysisRequestedEvent` is never published.
3. **Unreported Progress**: The background service `AnalysisQueueService` calls `_audioAnalysisService.AnalyzeFileAsync` but passes a null progress reporter. Thus, progress states are never broadcast over the event bus.
4. **Queue Count Indicator Bug**: The `_queuedCount` field in `AnalysisQueueService.cs` is never decremented, causing the status bar readout to stay stuck on "Analyzing..." even after the queue is empty.

### Proposed Changes
1. **Publish Request Events**: Refactor `StartAnalysisAsync()` in `AnalysisPageViewModel.cs` to publish `TrackAnalysisRequestedEvent(track.TrackId, AnalysisTier.Tier2)` to the event bus for each queued track instead of executing mock delays.
2. **Publish Progress Events**: Update `AnalysisQueueService.cs` to instantiate and pass an `IProgress<(int Percent, string Step)>` reporter to `AnalyzeFileAsync` that publishes `AnalysisProgressEvent` to the event bus.
3. **Subscribe to Completion Events**: Modify the constructor of `AnalysisPageViewModel.cs` to subscribe to `TrackAnalysisCompletedEvent` and `TrackAnalysisFailedEvent`.
4. **Update View Model on Complete**: Implement event handlers in `AnalysisPageViewModel` to:
   - Update the processing track's state to `Completed` or `Failed`.
   - Reload the library entry using `_libraryService` on completion to fetch real unpacked waveform data, cue points, and structural analysis results, mapping them to the UI list item.
5. **Monitor Queue Status**: Subscribe `AnalysisPageViewModel` to `AnalysisQueueStatusChangedEvent` to track general queue progress and transition `ProcessingState` back to `Completed` or `Idle` when all items are processed.
6. **Fix Queue Count**: Decrement `_queuedCount` in the `finally` block of `DispatchAnalysisJobAsync` in `AnalysisQueueService.cs` to allow status indicators to reset cleanly.

---

## 3. Incomplete Automix Staging

### Diagnosis
1. **Simple BPM Sorting**: `CreateAutomixPlaylist()` in `AnalysisPageViewModel.cs` is a synchronous stub. It filters staged tracks by BPM, sorts them in simple ascending order, and truncates to `MaxTracks`.
2. **Ignored AI Constraints**: All advanced UI constraints (e.g., `MatchKey`, `MaxEnergyJump`, weights, and `EnergyCurve`) are bound to `AutomixConstraints` but ignored.
3. **Disconnected Service**: The sophisticated greedy nearest-neighbor graph solver `PlaylistOptimizer` is registered in services but is never injected or invoked.

### Proposed Changes
1. **Inject PlaylistOptimizer**: Add `Services.Playlist.PlaylistOptimizer? playlistOptimizer = null` as a constructor parameter in `AnalysisPageViewModel.cs` (DI will resolve this automatically).
2. **Refactor to Asynchronous**: Convert `CreateAutomixPlaylist` to `public async Task CreateAutomixPlaylistAsync()`, and update `CreateAutomixCommand` to use `ReactiveCommand.CreateFromTask`.
3. **Invoke Optimizer**:
   - Filter eligible tracks by BPM limits.
   - Map UI `AutomixConstraints` properties to a `PlaylistOptimizerOptions` instance (setting `HarmonicWeight = 0.0` if `MatchKey` is disabled).
   - Call `_playlistOptimizer.OptimizeAsync(hashes, options)` to perform nearest-neighbor scoring and energy-curve shaping.
   - Fall back to the legacy simple BPM sorting if `_playlistOptimizer` is null (e.g., in unit testing or mock modes).
   - Reorder `PlaylistTracks` in-place using the resulting optimized sequence and update the status banner with transition metrics.

---

## 4. UI Style & Color Alignment

### Diagnosis
1. **Hardcoded Aesthetics**: The styling in `AnalysisPage.axaml` uses hardcoded hex values (like Spotify Green `#1DB954`, light backgrounds `#242424`, and borders `#2A2A2A`).
2. **Accent Clashes**: The hardcoded green accents conflict with the premium Mint-Teal accent (`#4EC9B0`) set up in `Themes/DesignTokens.axaml`.

### Proposed Changes
1. **Tokenize Brushes**: Replace all hardcoded hex values with semantic brushes declared in `Themes/DesignTokens.axaml`:
   - Primary Accent: Replace `#1DB954` / `#1ED760` with `{DynamicResource BrushAccent}` or `{DynamicResource BrushAccentDim}`.
   - Backgrounds: Replace `#1A1A1A`, `#161616`, `#242424` with `{DynamicResource BrushBg1}`, `{DynamicResource BrushBg0}`, and `{DynamicResource BrushBg2}` respectively.
   - Borders: Replace `#2A2A2A` / `#3A3A3A` with `{DynamicResource BrushBorder1}` / `{DynamicResource BrushBorder2}`.
   - Accent subtle background: Replace `#1A2A1A` with `{DynamicResource BrushAccentSubtle}`.
   - Error highlights: Replace `#FF4444` / `#FF6666` with `{DynamicResource BrushError}`.

---

## 5. Verification Plan

### Automated Tests
- Run existing view model and integration tests:
  ```powershell
  dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj
  ```

### Manual Verification
- Deploy and verify in the GUI:
  - Add tracks to the analysis queue and click "Start Analysis". Check if progress percentage, status updates, and current steps display correctly in the track row.
  - Expand the "Insight details" on an analyzed track to confirm the multi-colored tri-band waveform renders successfully.
  - Stage tracks in the Automix list, adjust constraints, click "Create Automix Playlist", and verify the list reorders harmonically and displays the cost reduction in the status message.
  - Check the general layout styling of the page to ensure the Mint-Teal theme looks premium and matches the rest of the application.
