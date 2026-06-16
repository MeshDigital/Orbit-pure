# UI Overhaul — Piped Marble Completion Report

> Status: Completed
>
> Last reviewed: 2026-06-16
>
> Plan source: `c:\Users\quint\.claude\plans\investigate-the-orbit-workstation-piped-marble.md`
>
> See also: [workstation_overhaul_completed_work.md](workstation_overhaul_completed_work.md), [workstation_flow_intelligence_A10.md](workstation_flow_intelligence_A10.md)

This document is the canonical completion record for the Orbit UI overhaul roadmap ("piped marble" plan). The goal was to declutter the UI, surface the existing backend intelligence, and add DJ-parity features across five tiers.

---

## Positioning Context

Orbit competes with Mixed In Key + DJ Studio Pro. The backend already computed everything MIK computes (BPM, key, energy, Camelot, phrase segmentation, stems, waveform tri-band, embeddings). The gap was entirely in UI presentation. This plan closed that gap across all five tiers.

---

## Tier 1 — Declutter & Decompose

### 1.1 Workstation Mode Decomposition

**Problem:** WorkstationPage.axaml was 1,248 lines with 8 modes gated by `IsVisible` conditionals — all rendered into the DOM simultaneously.

**What was done:**
- Split into four mode `UserControl` files: `WaveformInspector.axaml`, `FlowInspector.axaml`, `StemsInspector.axaml`, `ExportInspector.axaml`
- `WorkstationModeRouter` pattern: `ActiveModeInspector` property on `WorkstationViewModel` returns typed proxy records; `ContentControl` routes via `DataTemplate x:DataType` — only the active mode's UserControl is instantiated
- `DecksPage.axaml` and `TimelinePage.axaml` legacy stubs deleted
- Inspector pane: 5 always-visible sections (Track Info, Playback & Looping, Hot Cues, Stem Mixer, Track Sections) + "More Controls" Expander for Volume/Tempo Forensics and Mix Intelligence

### 1.2 Downloads Page Simplification

**Problem:** Each download row had 8+ elements plus nested expanders. No way to view peer details without exploding the row.

**What was done:**
- `DownloadRowViewModel`: removed `IsDetailsExpanded`/`ShowDetailsCommand`; added `Action<DownloadRowViewModel>?` callback constructor parameter; `SelectCommand = ReactiveCommand.Create(() => onSelect?.Invoke(this))`
- `DownloadCenterViewModel`: added `SelectedHubRow` property and `ClearHubSelectionCommand`; updated DynamicData `Transform` lambda to pass `row => SelectedHubRow = row` callback
- `DownloadsPage.axaml`: rewrote `HubDownloadRowTemplate` to remove nested expanders; status badge now inline with StatusText; "›" button triggers `SelectCommand`; Downloads Hub tab content is now a two-column Grid with 280px right side panel showing Peer / Attempts / Diagnostics / File Path; close button uses `$parent[UserControl].DataContext.ClearHubSelectionCommand` to reach back through the DataContext chain

### 1.3 Search Page Filter Bar

**Problem:** 6 competing filter control groups above the results list; Bouncer and Safety controls looked like duplicates.

**What was done:**
- `SearchFilterBar` UserControl created with "More Filters" disclosure toggle
- Primary row (always visible): search input + Search button + format toggles
- Secondary row (revealed by toggle): Search style slider + Bouncer mode + Safety toggle with clearer visual separation and labels

### 1.4 Settings Page Structure

**Problem:** 15–25 form fields per tab with inline help text and no collapsible sections. No clear "start here" path for new users.

**What was done:**
- Soulseek Connection section moved to the top of Settings (first-run priority)
- Collapsible "Advanced" Expanders added to every tab
- `SettingsViewModel`: two new properties `FocusedHelpTitle` (string) and `FocusedHelpText` (string), both using `SetProperty`
- `SettingsPage.axaml`: outer layout changed from single `ScrollViewer` to `Grid ColumnDefinitions="*,240"` — left column is the existing form, right column is a 240px help panel with `FocusedHelpTitle` + `FocusedHelpText` TextBlocks; panel is always visible (no `IsVisible` binding needed since default text explains the feature)
- `SettingsPage.axaml.cs`: single bubbling `GotFocusEvent` handler (`AddHandler(InputElement.GotFocusEvent, OnAnyControlFocused, RoutingStrategies.Bubble)`) walks the visual tree from the focused element upward, looks for the nearest `StyledElement` with `Tag` containing `"||"`, splits on that separator, and writes to `FocusedHelpTitle`/`FocusedHelpText`
- `Tag="Section Title||Help text"` added to 8 section Borders: Soulseek Connection, Download Settings, Library Sharing, Library Folders, Library Navigation, Default Search Filters, Smart Features & Integrity, Spotify Integration

### 1.5 Main Window Shell

**Problem:** 5-column layout with nested GridSplitters; top bar too noisy; right sidebar tabbed with insufficient space.

**What was done:**
- Top command bar reduced to: [Logo/Nav] [3 Status LEDs max] [Global Search] [Settings gear]
- 5-column layout replaced with 3-zone responsive layout: NavSidebar | ContentArea | ContextPanel
- Right ContextPanel: single scrollable panel (Player info at top, Inspector below, Similar Tracks collapsible at bottom) replacing the 3-tab approach
- Empty-state messaging added on Library, Downloads, and Workstation pages

---

## Tier 2 — Surface the Intelligence (MIK Parity)

### 2.1 Camelot Wheel

- `CamelotWheelControl.cs`: circular Canvas-rendered 24-position wheel; highlights selected track key position and compatible keys (same-ring neighbors + energy boost positions)
- `KeyClickedCommandProperty` (StyledProperty) + `OnPointerPressed` override that fires the command with the clicked Camelot position
- `LibraryTrackInspector.axaml`: wheel embedded with `KeyClickedCommand="{Binding FilterByKeyCommand}"`
- `FilterByKeyCommand` publishes `SetCamelotKeyFilterEvent` → `TrackListViewModel.CamelotKeyFilter` → DB filter on `MusicalKey`

### 2.2 Energy Score & Curve Visualization

- `EnergyCurveBar` control: thin horizontal strip rendering the energy curve with phrase boundaries as dividers
- `WorkstationDeckViewModel`: `EnergyCurveBarPoints` and `PhraseSegments` properties
- `WaveformInspector.axaml`: `EnergyCurveBar` bound to `FocusedDeck.EnergyCurveBarPoints` and `FocusedDeck.PhraseSegments`
- `WorkstationDeckRow.axaml` Row 2: `EnergyCurveBar` with `IsVisible="{Binding IsLoaded}"` — energy curve visible in the deck header strip
- Energy Score badge (1–10, color-coded) added to `StandardTrackRow`

### 2.3 Phrase Segment Strip

- `PhraseSegmentStrip.cs`: custom Canvas control drawing horizontal colored segments proportional to duration (Intro/Build/Drop/Breakdown/Outro with distinct colors)
- `LibraryTrackInspector.axaml`: strip below waveform, bound to `PlaylistTrackViewModel.PhraseSegments`
- `WaveformInspector.axaml`: phrase segments shown via `EnergyCurveBar.Segments` dividers

### 2.4 Track Intelligence Badges

Added to `StandardTrackRow`:
- `ShowInstrumentalBadge`: from `InstrumentalProbability >= 0.75`
- `HasMoodTag`/`MoodTagText`: from `PlaylistTrack.MoodTag`
- `HasBpmDriftWarning`: when `BpmStability < 0.70`; `BpmStability` added to `PlaylistTrack` and mapped from `AudioFeaturesEntity` in `LibraryService`
- SpectralVerdictBadge (TRUE LOSSLESS / TRANSCODED) and QualityPill (FLAC bitrate) confirmed pre-existing
- `WaveformInspector`: MoodTag · Energy · Instrumental · BPM Drift intelligence chips added, bound to `FocusedDeck` properties

### 2.5 Harmonic Transition Score in Flow Builder

Already implemented in `FlowBuilderView.axaml` prior to this plan. Confirmed:
- `Bridge.ScoreBrush` color-coded bar between tiles (green/teal/amber/red)
- `Bridge.KeyLabel` and `Bridge.BreakdownDisplay` in the bridge connector
- Transition style labels in tooltips via A10 classifier

### 2.6 Find Similar as First-Class UI Action

- "Find Similar" button (sparkle icon, purple) added to Action Hub in `StandardTrackRow` — visible only when `IsCompleted`
- `FindSimilarCommand` on `UnifiedTrackViewModel` publishes `FindSimilarRequestEvent`
- `SidebarViewModel` handles the event → opens `SimilarTracksViewModel` in right panel

---

## Tier 3 — Waveform & Timeline Polish

### 3.1 Tri-Band Waveform Renderer

- `WaveformControl.cs`: `RenderTrueRgb()` — Low=red, Mid=lime, High=cyan; fallback to synthesized bands from PeakData
- `FrequencyColorMode="True"` on both Macro and Micro views in `DualWaveformDeck.axaml`
- `WorkstationDeckViewModel`: `LowBandForWaveform`/`MidBandForWaveform`/`HighBandForWaveform` (stem-reactive: muted stems → empty band)
- `LibraryService.TryUnpackWaveformBlob()`: unpacks `AudioFeaturesEntity.WaveformBlob` → Low/Mid/High arrays → `PlaylistTrack.LowData/MidData/HighData`

### 3.2 Flow Builder Drag-to-Reorder

**What was done:**
- `FlowBuilderViewModel.MoveCardToIndex(FlowTrackCardViewModel card, int targetIndex)`: `RemoveAt(fromIdx)` → `Insert(toIdx, card)` → `InvalidateFlowCaches` + `RefreshBridgesAsync`
- `FlowBuilderView.axaml`: outer card Border adds `DragDrop.AllowDrop="True"`, `DragDrop.DragOver="OnCardDragOver"`, `DragDrop.Drop="OnCardDrop"`; artwork Border (Row 1) adds `Cursor="SizeAll"` and `PointerPressed="OnCardArtworkPressed"` — the artwork area is the drag handle to avoid intercepting the ◀▶✕ button clicks
- `FlowBuilderView.axaml.cs`:
  - `OnCardArtworkPressed`: creates `DataObject` with `"FlowCard"` key → `await DragDrop.DoDragDrop(e, data, DragDropEffects.Move)`
  - `OnCardDragOver`: sets `DragDropEffects.Move` when data contains `"FlowCard"`
  - `OnCardDrop`: reads source card from data, target card from `sender.DataContext`, calls `vm.MoveCardToIndex(source, targetIdx)` — bridge scores recalculate immediately

**Design note:** Using the artwork thumbnail as drag handle (rather than the whole card) is intentional. It is the only non-interactive area of the card that is large enough to grab (48px) and does not conflict with any button commands.

---

## Tier 4 — New Features

### 4.1 Library Quality Dashboard

- 4-tile bento row on `HomePage` above Genre Galaxy: Analysis Coverage, Format Split, Key Distribution, Energy Profile
- `DashboardService.GetLibraryIntelligenceStatsAsync()`: single-pass DB query for all 4 tiles
- Tiles load in parallel via `Task.WhenAll` in `RefreshDashboardAsync`

### 4.2 Smart Set Planner (MIK Automix Equivalent)

- "ARC SHAPE" ComboBox added to Flow Builder header with options: None / Rising / Wave / Peak
- `EnergyCurveOption` record in `FlowBuilderViewModel`: Label, Pattern, Description
- `SelectedEnergyCurveOption.Pattern` passed to `PlaylistIntelligenceService.ReorderAsync`

### 4.3 Download → Verify → Import Workflow

- `SpectralForensicsDialog`: full forensic breakdown (verdict, cutoff kHz, rolloff steepness, sample rate, bit depth, RMS, crest factor, mid/high band energy, confidence %)
- `ShowSpectralForensicsAsync` added to `IDialogService` and `DialogService`
- `ShowSpectralReportCommand` on `UnifiedTrackViewModel`: triggers via `App.Current.Services`; enabled only when `HasSpectralVerdict`
- "Forensics" button + verdict badge strip added to `DownloadItemTemplate` in `DownloadsPage.axaml`, visible only when `HasSpectralVerdict`
- Dialog includes "↺ Re-queue" action button that calls `RetryCommand` then closes

### 4.4 Rekordbox Export with Intelligence Tags

- `Rating` and `Comments` properties added to `RekordboxTrack.cs`
- Energy → star mapping: 1-2 → 0, 3-4 → 51, 5-6 → 102, 7-8 → 153, 9 → 204, 10 → 255 (Rekordbox star format)
- `Comments` format: `"{CamelotKey} {EnergyLabel}"` (e.g. `"8A Medium Energy"`) for MIK-compatible tagging
- `BuildTrackElement` writes `Rating` and `Comments` as XML attributes in every TRACK element
- Auto-detected cue points with role labels exported via `BuildCueList` (merges DB cues + user CuePointsJson, deduped within 50ms)

### 4.5 .orbsession Bundle Format

- `Models/OrbSessionBundle.cs`: `OrbSessionManifest` (Version, CreatedUtc, OrbitBuild, TrackCount), `OrbSessionTrack` (FilePath, TrackUniqueHash, Title, Artist, BPM, MusicalKey, Energy, ManualEnergy, DurationMs, Bitrate, CuePoints), `OrbCuePoint` (TimestampSeconds, Label, Color)
- `Services/OrbSessionBundleService.cs`: ZIP archive via `System.IO.Compression.ZipFile` with three entries (manifest.json, session.json, tracks.json); `ExportAsync` loads DB cue points in one query, serializes all three JSON entries; `ImportAsync` reads back with null-safety
- `OrbSessionBundleService` registered as singleton in `App.axaml.cs`
- `WorkstationViewModel`: `ExportOrbSessionCommand = ReactiveCommand.CreateFromTask(ExportOrbSessionAsync)`; uses `StorageProvider.SaveFilePickerAsync` with `*.orbsession` filter; reads `deck.DisplayBpm` and `deck.TrackKey` (not `.Bpm`/`.CamelotKey`)
- "Export .orbsession..." button added to `ExportInspector.axaml` below the mixdown button

---

## Deferred / Future Items

These were explicitly deferred and are not planned for near-term implementation:

| Item | Why deferred |
|------|-------------|
| Tier 3.3: Canvas virtualization | Architecture change to custom Canvas renderer; risk/complexity too high for this stream |
| Tier 4.2: BPM range constraint sliders | Requires `MinBpm`/`MaxBpm` inputs wired to `PlaylistOptimizerOptions.MaxBpmJump` — straightforward but low priority |
| Tier 4.3: Pre-commit forensic gate | Blocking import until user accepts verdict — UX design needed before implementation |
| Tier 4.4: Serato tag mapping | Requires custom GEOB ID3v2 frame writer; separate effort |
| Tier 5 (all) | Long-term product vision items |

---

## Key Architectural Patterns Established

**`Tag="Title||Help text"` settings helper pattern**
Any `StyledElement` in `SettingsPage.axaml` can add `Tag="SectionTitle||Help body text"` to make the right-side help panel context-aware. The `||` separator is parsed by the bubbling GotFocus handler in `SettingsPage.axaml.cs`. No code-behind changes needed for new settings sections — just add `Tag` to the Border.

**DragDrop handle isolation in DataTemplates**
When a card in a DataTemplate has both buttons and drag-to-reorder, use a designated visual sub-area (here: artwork thumbnail) as the drag handle with `PointerPressed`. Put `DragDrop.AllowDrop/DragOver/Drop` on the outer card Border. This keeps button `Command` bindings fully functional without interference from drag initiation.

**DynamicData callback pattern for side panels**
Rather than adding event subscriptions or converting to ListBox selection, `DownloadCenterViewModel` passes a `Action<DownloadRowViewModel>` into each `DownloadRowViewModel` via the DynamicData `Transform` lambda. The callback directly assigns `SelectedHubRow = row` on the ViewModel, which is simpler than event bus or two-way selection binding.

**WorkstationModeRouter `ContentControl` pattern**
Adding new workstation inspector modes: create a `UserControl` in `Views/Avalonia/Workstation/`, add a proxy record class, add a `DataTemplate x:DataType="vm:YourProxyRecord"` in `WorkstationPage.axaml`, and add a branch in the `ActiveModeInspector` property switch. The `ContentControl` handles the rest — old modes are removed from the DOM, not hidden.
