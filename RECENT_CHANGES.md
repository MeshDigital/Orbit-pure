# ORBIT-Pure — Recent Changes

---

## [Unreleased] — 2026-04-09

### 🎛️ Studio Mode — Session Persistence

The Workstation page now saves and restores its state across app launches and unexpected crashes.

**New files:**
- `Models/Stem/WorkstationSession.cs` — JSON-serialisable snapshot: active mode, timeline offset/zoom, per-deck loaded track (file path, hash, title, artist, BPM, key, playback position).
- `Services/WorkstationSessionService.cs` — Atomic save (temp-file swap, never corrupts the last good snapshot on crash) and load. Written to `%APPDATA%\Antigravity\workstation-session.json`.

**WorkstationViewModel changes:**
- Injected `WorkstationSessionService` (registered as DI singleton).
- `RestoreSessionAsync()` — runs on startup; reloads tracks into Deck A/B by file path; cue points and stem preferences re-fetched from SQLite/file automatically via existing services.
- `SaveSessionAsync()` — autosaves on every track load into any deck and on every mode switch (Waveform / Flow / Stems / Export).
- `Dispose()` — synchronous flush so the session is written even when the OS terminates the process after the window closes.
- `OnTrackLoaded` callback on `WorkstationDeckViewModel` (both paths: `LoadTrackCommand` + `LoadPlaylistTrackCommand`) triggers parent autosave.
- `AddDeckCommand` attaches the callback to dynamically-added decks C/D.

---

### 🗂️ Library — Context Menu & Selection Bar Fixes

All right-click context menu actions in the track DataGrid are now fully functional and consistent.

**Root cause:** In Avalonia, `ContextMenu` renders in a separate popup visual tree so element references like `CommandParameter="{Binding #TrackGrid.SelectedItem}"` always resolve to `null`, silently breaking every command that received the parameter. All five affected items have been fixed.

**Changes:**
- `Views/Avalonia/TrackListView.axaml` — Removed 5 broken `CommandParameter="{Binding #TrackGrid.SelectedItem}"` bindings. Added new **🔬 Analyse Track** menu item (`Operations.AnalyseTrackCommand`). Separator grouping cleaned up.
- `ViewModels/Library/TrackOperationsViewModel.cs`:
  - `ExecutePlayTrack` / `ExecuteHardRetry` / `ExecuteOpenFolder` / `ExecuteRemoveTrack` — fall back to `LibraryViewModel?.Tracks.LeadSelectedTrack` when called with a null parameter (the normal Avalonia context menu case).
  - `ExecuteAddToQueue(null)` — now delegates to `ExecuteAddSelectedToQueue()` rather than silently returning.
  - New `AnalyseTrackCommand` / `ExecuteAnalyseTrack` — publishes `TrackAnalysisRequestedEvent` for the right-clicked track (falls back to lead selection).

**Bottom selection FAB (LibraryPage):** Added **▶️ Play** (`Tracks.Operations.PlayTrackCommand`) and **⊕ Add to Queue** (`Tracks.Operations.AddSelectedToQueueCommand`) buttons — now all selection actions (play, queue, analyse, tag edit, rekordbox export, clear) are accessible without opening the context menu.

---

### 🎵 Queue Panel — Playing Indicator Fix

`Views/Avalonia/QueuePanel.axaml` — The now-playing `▶️` indicator in the queue list was using `ConverterParameter={Binding}`, which is **not valid Avalonia AXAML** (ConverterParameter does not accept binding expressions). The indicator never highlighted the current track.

**Fix:** Replaced with `IsVisible="{Binding $parent[ListBoxItem].IsSelected}"` — works correctly because `SelectedIndex` is already bound to `CurrentQueueIndex`, so the selected list item is always the currently-playing track.

---

### ⚡ Workstation — Phase 2: Full Stem & Flow Builder Implementation

This milestone completes the Workstation page from a ~40 % skeleton to a
fully-wired creative canvas with four functional modes.

#### New ViewModels — Stem Mixer (`ViewModels/Workstation/`)

| File | Purpose |
|---|---|
| `StemChannelViewModel.cs` | Single fader strip (Vocals / Drums / Bass / Other). Delegates `GainDb`, `Pan`, `IsMuted`, `IsSoloed` directly to `StemMixerService`. Exposes `MuteCommand`, `SoloCommand`, `ResetCommand`. |
| `StemMixerViewModel.cs` | Owns the NAudio `StemMixerService` (44 100 Hz stereo). Wires `LoadAndSeparateCommand : ReactiveCommand<string, Unit>` consumed by `WorkstationDeckViewModel`. Manages ONNX separation progress, cancellation, and exposes per-stem WAV paths (`VocalsWavPath`, …). |
| `StemWaveformRowViewModel.cs` | Thin row VM (`WaveformData?`, `Progress`, `IsLoading`) bound to a `WaveformControl` per stem. |
| `StemWaveformViewModel.cs` | Aggregates four `StemWaveformRowViewModel` instances plus `SharedZoomLevel` / `SharedViewOffset`. `LoadStemWaveformsAsync(vocals, drums, bass, other)` runs four parallel inline NAudio extractions — reads PCM floats, bins 1 000-bucket RMS/peak arrays, wraps results in `WaveformAnalysisData`. |
| `CueEditorViewModel.cs` | Manages hot-cue CRUD for a loaded deck track. |
| `ExportDialogViewModel.cs` | Drives the export dialog — output path, format (WAV / MP3 / FLAC), normalisation, dithering, export progress. `SetDecks()` populates from active Workstation decks. |
| `WorkstationDeckViewModel.cs` | Thin wrapper over `DeckSlotViewModel`. **Added `StemWaveforms : StemWaveformViewModel` property**. After separation completes, calls `StemWaveforms.LoadStemWaveformsAsync(…)` to populate the visual waveform strip. |
| `WorkstationViewModel.cs` | Root page VM. **Added `ExportPanel : ExportDialogViewModel` property** — the same instance powers both the inline Export mode panel and the toolbar Export popup, disposed together with the page. |

#### New ViewModels — Flow Builder (`ViewModels/`)

| File | Purpose |
|---|---|
| `FlowTrackCardViewModel.cs` | Represents a single track card on the Flow Builder timeline. Exposes `Artist`, `Title`, `BpmDisplay`, `KeyDisplay`, `DurationDisplay`, `EnergyCurvePoints` (RMS-sampled from `WaveformData` bytes, 8 buckets), `HasBridge`, `Bridge : FlowBridgeInfo?`. Contains `SetBridgeTo(next)` which computes Camelot-wheel distance → colour-coded `ScoreBrush` (teal ≤ 1 step, yellow ≤ 2, orange ≤ 4, red ≥ 5) plus BPM-delta display string. Exposes `MoveLeftCommand`, `MoveRightCommand`, `RemoveCommand` (callbacks into parent). Also declares the `FlowBridgeInfo` record (BpmDeltaDisplay, ScoreBrush, KeyLabel, Tooltip). |
| `FlowBuilderViewModel.cs` | Drives the Flow Builder mode. Constructor: `(ILibraryService, PlaylistOptimizer)`. Loads playlists via `LoadPlaylistsCommand` auto-fired on construction. `LoadSelectedPlaylistCommand` fetches up to 500 tracks, calls `PlaylistOptimizer.OptimizeAsync` for AI-ordered BPM+Camelot sequencing (unanalysed tracks appended at end), builds `ObservableCollection<FlowTrackCardViewModel>`, runs `RefreshBridges()`. `SuggestNextCommand` fetches all remaining candidates, calls optimizer with `StartTrackHash = Tracks.Last()` to pick the single best next track. `ClearCommand` clears the set. Transition bridges are recalculated after every structural mutation (move/remove). |

#### WorkstationPage AXAML — Mode Wiring (`Views/Avalonia/WorkstationPage.axaml`)

**Stems mode** — replaced "coming in Epic #144" placeholder with:
- Header bar: deck label, track title, `⚡ Separate Stems` button, live separation progress `%`.
- Two-column body: `StemMixerView` (300 px fixed, dark-bordered left panel) + `StemWaveformView` (fill right).
- Both views bind to `FocusedDeck.Stems` / `FocusedDeck.StemWaveforms` respectively.

**Export mode** — replaced "coming in Epic #143" placeholder with a full inline form bound to `WorkstationViewModel.ExportPanel`:
- Output file path text-box (editable).
- Format combo (WAV / MP3 / FLAC).
- Normalise / Dither checkboxes.
- Scrollable `ProgressBar` + `StatusMessage` (visible only while exporting).
- `Export Mix` button + `Cancel` button (visible only while exporting).

---

### 🖥️ MainWindow Navigation Consolidation (`Views/Avalonia/MainWindow.axaml`)

- **Merged** the three separate sidebar items (Decks, Timeline, Stems) into a single **Workstation** nav button (`NavigateWorkstationCommand`, `PageType.Workstation`).
- Added `Ctrl+Z` → `UndoPlaylistCommand` and `Ctrl+Y` → `RedoPlaylistCommand` global keybindings.
- Added right-panel tab button styles (`Button.tab`, `.tab:pointerover`, `.tab.selected`) for the new right-panel multi-tab header.
- Rewired right-panel header and body to support tabbed inspector layout with tab-selector row.

---

### 🌊 WaveformControl Enhancements (`Views/Avalonia/Controls/WaveformControl.cs`)

- **`FrequencyColorMode : bool` (StyledProperty)** — when `true`, renders using tri-band colour encoding: Low band → hot-pink/red, Mid band → neon-green, High band → cyan/blue. Falls back to synthetic approximation from peak data when no `LowBand` is bound. Registered in `AffectsRender`.
- **Hover cursor tracking** — `_hoverX` field updated on `PointerMoved`, `PointerEntered`, cleared on `PointerExited`. Triggers `InvalidateVisual()` for live time-position indicator rendering.

---

### 🔗 SimilarTracksPanel — Bridge Mode & Batch Add (`Views/Avalonia/SimilarTracksPanel.axaml`)

- **Bridge Mode UI** — alternate header label "Bridge Mode" (teal) shown when `IsBridgeMode` is active.
- **`Add All` button** — adds all current results to a project; visible when `Results.Count > 0`; uses `NumericConverters.IsNotZero`.
- **`bridgeRow` style class** — distinct dark-green tinted row for bridge-compatible suggestions with hover highlight.
- `Button.addBtn` opacity fade-in on `pointerover` for both `row` and `bridgeRow` selectors.
- Layout extended from `RowDefinitions="Auto,Auto,*"` to `RowDefinitions="Auto,Auto,*,Auto,Auto"` (space for bridge header + bulk-action footer).

---

### 📊 LibraryTrackInspector Upgrade (`Views/Avalonia/Controls/LibraryTrackInspector.axaml`)

- Added **inline WaveformControl strip** (64 px, read-only, bound to `WaveformData` + `Cues`) above the Status section.
- Added **Sonic Profile section** — Energy, Valence, Danceability as labelled `ProgressBar` rows (0–1 range, colour-coded).
- Inspector `DesignHeight` updated 800 → 900 to fit new content.
- `xmlns:controls` namespace added for WaveformControl reference.

---

### 📈 AnalysisPage Queue Metrics (`Views/Avalonia/AnalysisPage.axaml`)

- **Real-time queue telemetry strip** (visible when `HasQueueMetrics`): three labelled stats — `AvgAnalysisTimeDisplay`, `ThroughputDisplay`, `ElapsedTimeDisplay`.
- Track list panel switched to **`VirtualizingStackPanel`** for large-library performance.
- Queue pane header/start-button grid redesigned; analysis queue visual improvements.

---

### ⬇️ Downloads Improvements

#### `ViewModels/Downloads/DownloadCenterViewModel.cs` (+39 lines)
- New per-download preference persistence wired to `DownloadPreferenceEntity`.
- Improved group-level progress aggregation.

#### `ViewModels/Downloads/DownloadGroupViewModel.cs` (+14 lines)
- Per-group status text and completion ratio properties surfaced.

#### `ViewModels/Downloads/UnifiedTrackViewModel.cs` (±14 lines)
- Download preference binding: remember per-track format preference.

#### `Views/Avalonia/Controls/DownloadGroupRow.axaml` (+6 lines)
- Group progress bar + individual track count label additions.

#### `Views/Avalonia/DownloadsPage.axaml` (+20 lines)
- Downloads page header statistics strip.

---

### 🎵 New Services

| File | Purpose |
|---|---|
| `Services/Audio/MixdownService.cs` | Offline mixdown renderer. Accepts a list of `DeckSource` (file path, volume, mute flag), sums NAudio sample providers, writes the result to a WAV/MP3/FLAC file. Used by `ExportDialogViewModel`. |
| `Services/AudioAnalysis/AudioAnalysisService.cs` | Unified analysis orchestrator — coordinates `BpmAnalyzer`, `KeyDetector`, `WaveformExtractionService`, `EssentiaRunner`, and embedding extraction for a single track. |
| `Services/AudioAnalysis/EssentiaRunner.cs` | Thin wrapper that shells out to the bundled Essentia CLI binary; parses JSON output into `AudioFeaturesEntity` fields (energy, danceability, valence, spectral data). |

---

### 🗃️ New Data Entities

| File | Purpose |
|---|---|
| `Data/Entities/DownloadPreferenceEntity.cs` | Persists per-track download preferences (preferred format, bit-rate, auto-download flag). |
| `Data/Entities/PlaylistHistoryEntryEntity.cs` | Records playlist mutation events for undo/redo and activity log; foreign key to `PlaylistJobEntity`. |

---

### 🧹 Legacy Pages Moved

The three retired standalone pages have been relocated to `Views/Avalonia/Legacy/` to keep the primary `Views/Avalonia/` directory clean:
- `Legacy/DecksPage.axaml` — old dual-deck page (superseded by `WorkstationPage` Waveform mode)
- `Legacy/StemsPage.axaml` — old stems page (superseded by `WorkstationPage` Stems mode)
- `Legacy/TimelinePage.axaml` — old timeline page (superseded by `WorkstationPage` Waveform mode timeline)

---

### 🆕 New UI Controls

| File | Purpose |
|---|---|
| `Views/Avalonia/Controls/SparklineControl.cs` | Lightweight Avalonia `Control` that renders a normalised float array as a filled sparkline polyline. Used by Flow Builder track cards (`EnergyCurvePoints`). |
| `Views/Avalonia/Controls/PlaylistMergeDialog.axaml/.cs` | Dialog for merging two playlists with conflict-resolution options. |

---

### 🆕 New Views — Flow Builder & Workstation

| File | Purpose |
|---|---|
| `Views/Avalonia/FlowBuilderView.axaml/.cs` | Horizontal drag-and-drop track timeline. Resolves `FlowBuilderViewModel` from DI. Hosts playlist selector, horizontal `ItemsControl` of `FlowTrackCard` templates, status bar, and the `SuggestNext` / `Clear` action buttons. |
| `Views/Avalonia/WorkstationPage.axaml/.cs` | Unified Workstation 4-mode page (Waveform / Flow / Stems / Export). Resolves `WorkstationViewModel` from DI. |
| `Views/Avalonia/Workstation/WorkstationDeckRow.axaml/.cs` | Compact deck row component for the Waveform-mode timeline: deck label, track title, waveform strip, transport micro-controls, cue markers. |
| `Views/Avalonia/Workstation/ExportDialog.axaml/.cs` | Pop-up export dialog window; also surfaces its `ExportDialogViewModel` inline in WorkstationPage's Export mode. |

---

### 🔬 Similarity Service (`Services/Similarity/SimilarityIndex.cs`)

- Minor refactor: +19 lines improving embedding vector cosine normalisation and concurrency safety.

### 📡 Miscellaneous AXAML

| File | Change |
|---|---|
| `Views/Avalonia/TrackListView.axaml` | +18 lines — context menu actions, improved row layout |
| `Views/Avalonia/HorizontalPlayerControl.axaml` | +1 line — minor binding fix |
| `Views/Avalonia/LibraryPage.axaml` | +5 lines — filter bar spacing correction |
| `Views/Avalonia/NowPlayingPage.axaml` | +1 line — layout adjustment |
| `Views/Avalonia/PlayerControl.axaml` | +6 lines — secondary playback controls |
| `Views/Avalonia/Controls/StandardTrackRow.axaml` | +4 lines — right-click menu wiring |
| `Views/Avalonia/Controls/TrackInspector.axaml` | ±12 lines — panel widths and label sizing |

---

## Previous Entries

### [Prior] 2026-04-07
- `feat(epic-12)`: Responsive multi-pane layouts (#110–113)
- `fix(tests)`: subscribe to `ReactiveCommand.Execute()` observables in StemChannelViewModelTests
- `feat(task-2-1)`: Discogs-Effnet 2048-D embedding extractor (#70)
- `feat(task-3-4)`: `AutomixConfigViewModel` + dialog modal (#77)
- `feat(task-4-2)`: `WaveformControl` magnetic beat-grid snapping (#50)
- `feat(task-49)`: `TieredTrackComparer` two-stage gatekeeper + search pipeline (#49)
- `test(task-63)`: loop/phase-align deck tests + Analysis→Playlist→Timeline E2E (#63)
- `chore`: clean up TODO.md — mark critical items done, add keyboard mapping epic (#119)
- `docs`: comprehensive architectural deep-dive (16 subsystems) in `ARCHITECTURE_DEEPDIVE.md`
