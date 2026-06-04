# EPIC: Orbit Workstation Cockpit Refactor (DJ.Studio Pro-Grade UI Overhaul)

Status: Active
Owner: Orbit UX/Frontend
Benchmark Reference: DJ.Studio Pro

## Epic Goal
Transform Orbit Workstation into a single unified cockpit where:
- timeline is always visible
- all core tools live in one view
- tool toggles switch context without leaving the cockpit
- UI is DPI-aware, compact, and production-grade
- empty states are actionable
- structure is grid-based and timeline-first

## Memory Source of Truth
Primary memory file:
- docs/memory/workstation_cockpit.md

Canonical functional/UI foundation:
- DOCS/WORKSTATION_COCKPIT_FOUNDATION.md

Issue-ready backlog:
- DOCS/WORKSTATION_COCKPIT_ISSUE_BACKLOG.md

Use the foundation document as the canonical product and UX specification for cockpit work.
Use the backlog file as the authoritative source for GitHub issue generation. It now contains both partial follow-up issue drafts and create-now issue drafts, plus an agent-ready generation prompt.

The memory file must be updated after each implementation slice.

## Issue Link Registry

Use this section to record the GitHub issue URL for each backlog item after creation. Keep the title order aligned with `DOCS/WORKSTATION_COCKPIT_ISSUE_BACKLOG.md`.

### Partial follow-up issues

- A1. Replace Workstation With A True Cockpit Grid: pending
- A2. Make Timeline Always Visible In All States: pending
- A3. Remove Deck-First UI Clutter From Visible Shell Chrome: pending
- A4. Integrate Flow Builder More Deeply Into The Timeline Surface: pending
- A5. Simplify Header Layout Further: pending
- A6. Remove Fragmented Panels And Repeated Status Surfaces: pending
- A7. Add Automation Lane System Beyond The Current Placeholder State: pending
- A8. Add Stems Lanes Beyond The Current First-Pass Controls: pending
- A9. Implement Phrase-Aware Snapping And Phrase-Aware Editing Workflow: pending
- A10. Fix Remaining Low-Contrast Text And Readability Issues: pending

### Create-now issues

- B1. Reduce Repeated State Surfaces In Workstation Shell: https://github.com/MeshDigital/Orbit-pure/issues/153
- B2. Finish Cockpit-First Layout Reduction: https://github.com/MeshDigital/Orbit-pure/issues/154
- B3. Make Timeline Persist As Empty Canvas: https://github.com/MeshDigital/Orbit-pure/issues/155
- B4. Remove Remaining Visible Deck-First Affordances: https://github.com/MeshDigital/Orbit-pure/issues/156
- B5. Deepen Flow Integration Into Timeline Canvas: https://github.com/MeshDigital/Orbit-pure/issues/157
- B6. Add Real Automation Lane Editing: https://github.com/MeshDigital/Orbit-pure/issues/158
- B7. Add Real Stems Lane Editing: https://github.com/MeshDigital/Orbit-pure/issues/159
- B8. Add Transition Preset System: https://github.com/MeshDigital/Orbit-pure/issues/160
- B9. Add Phrase-Aware Snapping: https://github.com/MeshDigital/Orbit-pure/issues/161
- B10. Implement Timeline Virtualization Strategy: https://github.com/MeshDigital/Orbit-pure/issues/162
- B11. Create Unified Workstation State Model: https://github.com/MeshDigital/Orbit-pure/issues/163
- B12. Finish Accessibility Contrast Pass: https://github.com/MeshDigital/Orbit-pure/issues/164

## Learning Plan for Agent Work
Before major implementation slices, review and align with:
1. The canonical cockpit foundation spec in `DOCS/WORKSTATION_COCKPIT_FOUNDATION.md`.
2. The flow implementation blueprint in `DOCS/workstation/flow-integration-blueprint.md`.
3. The A9 phrasing contract in `DOCS/workstation/flow-intelligence-design-note.md`.
4. The runtime validation gate in `DOCS/workstation/runtime-qa-cockpit-gate.md`.
5. The transition preset blueprint in `DOCS/workstation/flow-slice-4-transition-presets-blueprint.md`.
6. Avalonia DPI scaling and render scaling patterns.
7. Avalonia MVVM and tool/context switching patterns.
8. Timeline virtualization and rendering constraints.
9. DJ.Studio Pro workflow benchmarks.
10. Existing Orbit Workstation code and migration boundaries.

## Target Cockpit Architecture
1. Header (tool selector + transport essentials).
2. Left navigation (Workstation as primary playback/edit destination).
3. Main canvas (timeline/waveforms always visible).
4. Right inspector (contextual controls by tool and selection).
5. Bottom drawer (track list, suggestions, drag pool).

## Tasks (Execution Order)

### Task 1: Create DPI Token System
Target:
- src/UI/Tokens/DpiTokens.axaml

Deliverables:
- size, padding, icon, and font tokens
- density modes: Compact, Normal, Touch
- workstation control migration to tokenized values

### Task 2: Replace All Icons with SVG
Target:
- assets/icons/svg/

Deliverables:
- migrate workstation-relevant raster icons to vector
- ensure scaling clarity at high DPI

Current implementation status:
- SVG asset pipeline established in `assets/icons/svg/`.
- Initial workstation icon pack added (play/stop/undo/redo/pan/zoom/close).
- Workstation shell core controls now use centralized resource-backed vector icons (no inline cockpit path literals remain in `WorkstationPage.axaml`).
- Remaining slices should finish direct SVG-asset consumption convergence beyond current `PathIcon` resource mapping.

### Task 3: Build Cockpit Grid Layout
Target:
- src/Workstation/CockpitView.axaml

Deliverables:
- stable grid with header, main canvas, inspector, bottom drawer
- remove tab/drawer dependency for primary timeline workflows

Current implementation status:
- Canonical cockpit surface: `Views/Avalonia/WorkstationPage.axaml` (active migration surface).
- Extraction to `src/Workstation/CockpitView.axaml` is deferred until post-epic stabilization.
- All new cockpit slices must land in `WorkstationPage` and corresponding workstation view-model layers.

### Task 4: Implement Tool Toggle System
Target:
- existing Workstation shell first (WorkstationViewModel + WorkstationPage)
- later extraction target: src/Workstation/Tools/

Deliverables:
- WaveformTool
- FlowTool
- StemsTool
- AutomationTool
- SamplesTool
- ExportTool
- tool registry and contextual inspector switching

Execution notes:
- start by replacing hardcoded mode buttons with a shared tool collection driven from the existing Workstation mode model
- Automation and Samples begin as staged placeholders, not fake-live tools
- do not fork a second cockpit implementation while the current Workstation shell is still the active migration surface

### Task 5: Integrate Timeline as Permanent Canvas
Deliverables:
- timeline always visible
- tool-specific lanes/overlays rendered in place
- no context loss when switching tools

### Task 6: Remove Deck-First UI Clutter
Deliverables:
- reduce/remove visible A/B/C/D deck-first interaction model in cockpit shell
- keep internal deck mechanics where required by engine/sync behavior

### Task 7: Replace Empty States with Actionable CTAs
Deliverables:
- Download Tracks
- Analyze Playlist
- Import Local Files
- Load into Workstation

### Task 8: Inspector Refactor
Deliverables:
- contextual inspector by tool and selection
- properties, stems, transitions, automation, export panels

### Task 9: Update Navigation Semantics
Deliverables:
- remove Player as separate cockpit-equivalent destination
- Workstation is primary playback/edit experience

### Task 10: Documentation Updates
Targets:
- docs/workstation/architecture.md
- docs/workstation/tools.md
- docs/workstation/timeline.md

## Agent Workflow
For each task:
1. Read docs/memory/workstation_cockpit.md.
2. Implement scoped slice with smallest viable diff.
3. Validate with build/tests relevant to slice.
4. Update memory file with decisions and follow-ups.
5. Commit with clear task-scoped message.

## Next Execution Plan (2026-05-11)
Completed in the prior wave:
- Workstation shell icon/resource consolidation
- Canonical cockpit surface decision (`WorkstationPage`)
- Navigation semantics remap + alias hardening tests
- QA matrix closure and runtime smoke validation

Completed in this wave:
- Remaining now-playing UX copy cleanup across queue/sidebar/full-view media surfaces
- Media-surface action deduplication across sidebar and bottom dock shells
- First inspector-first declutter slice: removed duplicate header export action in favor of export lane + inspector surfaces
- Continued inspector-first declutter: removed redundant export format quick-actions from export lane
- Continued inspector-first declutter: removed redundant stems separation action from stems lane
- Continued load/analyze declutter: removed redundant ready-track drawer `Analyze Playlist` CTA while preserving drawer-header and flow-lane entry points
- Continued load-affordance declutter: removed generic load CTAs from samples inspector and flow lane, leaving the drawer CTA and per-track deck loads as the primary entry points
- Heavy shell declutter: removed duplicate header diagnostics/tools panels, mode chips, redundant transport summary chips, and repeated flow analysis/readiness surfaces
- Post-structural runtime smoke: app launch remained active after declutter pass
- Declutter continuation: removed duplicate global-header flow summary, duplicate waveform-lane focused deck summary, duplicate flow-inspector workstation eligibility summary, duplicate samples-inspector CTA-state summary, duplicate automation-inspector analysis queue summary, and duplicate automation/samples inspector mode summaries
- Timeline-empty-state continuation: added a persistent empty timeline canvas scaffold so the main workspace remains visible even when no decks are loaded
- Timeline-empty-state layering pass: made the scaffold a persistent background surface and converted empty-state messaging to a lightweight overlay card so the timeline scaffold remains visible without replacing the workspace
- Deck-chrome reduction continuation: removed deck-centric shell wording from header chips/transport copy, replaced deck-labeled inspector headings with track/context labels, and removed explicit deck-add action from waveform inspector chrome
- Duplicate-surface sweep continuation: removed header copy of `ActiveToolSummary` so the inspector remains the canonical detail surface and header remains minimal
- Duplicate-surface sweep continuation: removed flow-inspector copy of `FlowWindowSummary` so viewport context remains canonical in the lane
- Empty-timeline regression coverage: added `WorkstationTimelineLayoutGuardTests` to guard persistent timeline scaffold layering and prevent empty-state overlay regressions
- Flow planning scaffold: added `DOCS/workstation/flow-integration-blueprint.md` as the implementation blueprint for timeline-native transition overlays, handles, and inspector coupling
- Runtime QA scaffold: added `DOCS/workstation/runtime-qa-cockpit-gate.md` as the manual gate checklist before deep cockpit feature slices
- Flow Slice 1 implementation: added passive Flow timeline overlays (transition regions, phrase guides, beat guides, compatibility badges) using VM-computed `FlowTransitions` rendered as non-interactive canvas layers
- Flow Slice 1 guardrail: expanded `WorkstationTimelineLayoutGuardTests` to assert passive overlay layer wiring and non-interactive behavior
- Flow Slice 2 implementation: added selectable Flow transition overlays with highlighted selection state and inspector-coupled Flow detail fields (transition label, compatibility, phrase/beat alignment, length)
- Flow Slice 2 guardrail: expanded architecture and VM tests to verify selection command bindings and selected-transition overlay state
- Flow Slice 3 implementation: added interactive transition-length drag handles with live preview, phrase/beat snap behavior, and undo checkpoints for reversible edits
- Flow Slice 3 guardrail: expanded architecture and VM tests to cover handle drag hook bindings and snapped-length override rendering
- Flow Slice 4 planning: added `DOCS/workstation/flow-slice-4-transition-presets-blueprint.md` covering preset catalog, harmonic/energy scoring, inspector preset UI, apply/reset engine, and test strategy
- Flow Slice 4.1 implementation: added preset/scoring/ranking VM scaffolding in `ViewModels/Workstation/WorkstationViewModel.cs` including static preset catalog, harmonic compatibility helper, energy compatibility helper, combined compatibility helper, warning flags, and inspector score helper labels
- Flow Slice 4.1 guardrail: extended `Tests/SLSKDONET.Tests/ViewModels/WorkstationDeckViewModelTests.cs` with focused assertions for harmonic bucket mapping, energy bucket mapping, and risky/mismatch preset ranking behavior
- Flow Slice 4.2 implementation: added `_flowTransitionPresetOverrides` dictionary, `ApplyFlowPresetCommand` (ReactiveCommand<string, Unit>), `ResetFlowPresetCommand`, `SetFlowTransitionPresetOverride` helper, and `FlowTransitionPresetOperation` (IUndoableOperation) to `WorkstationViewModel.cs`; extended `BuildFlowTransitions` to accept and apply preset overrides; added `FlowInspectorTopSuggestedPresetId` property
- Flow Slice 4.2 XAML: added harmonic score, energy score, combined score, warning, preset label, suggested presets chips, and Apply/Reset preset buttons to Flow inspector panel in `Views/Avalonia/WorkstationPage.axaml`
- Flow Slice 4.2 guardrail: extended `Tests/SLSKDONET.Tests/Architecture/WorkstationTimelineLayoutGuardTests.cs` with assertions for all 7 new inspector binding labels and 2 preset command bindings
- Post-slice 4.2 validation: build 0 errors; 13/13 targeted tests pass
- Flow Slice 4.3 implementation: preset-driven behavior — `CycleFlowPresetCommand` (cycles top suggestions with undo), extended `FlowTransitionPresetOperation` to store/restore length alongside preset, `ClearFlowTransitionLengthOverride` helper, `ApplyFlowPresetCommand` now snaps length to `preset.DefaultLengthSeconds` on apply, `FlowInspectorCurveLabel` and `FlowInspectorBandStrategyLabel` inspector properties with `ResolveCurveLabel`/`ResolveBandStrategyLabel` helpers, `IsPresetApplied` bool on `FlowTransitionOverlayViewModel`
- Flow Slice 4.3 XAML: Cycle preset button, curve chip, band strategy text, and `IsPresetApplied` indicator stripe (blue) on timeline overlay block
- Flow Slice 4.3 guardrail: 6 new guard assertions (`CycleFlowPresetCommand`, `IsPresetApplied`, `FlowInspectorCurveLabel`, `FlowInspectorBandStrategyLabel`, etc.); 4 new VM tests for preset overlay state and catalog defaults
- Post-slice 4.3 validation: build 0 errors; 17/17 targeted tests pass
- Flow Slice 4.4 closure: runtime QA/doc closure pass completed against the blueprint exit criteria (preset apply/reset/cycle, score visibility, undo/redo determinism, duplicate-surface avoidance, build/tests green)
- Flow Slice 5 implementation: playlist-native transition roadmap added (`FlowPlaylistTransitions`) so Flow mode now renders compatibility markers for consecutive playlist tracks, not only loaded-deck pairs
- Flow Slice 5 guardrail: architecture assertions for playlist overlay bindings and focused VM tests for `BuildPlaylistFlowTransitions` sequence + color behavior
- Flow Slice 5 validation: focused flow suite passed (19/19)
- A10 readability continuation: raised low-contrast text in timeline ruler, inspector secondary rows, drawer idle status copy, and track-pool helper text in `WorkstationPage.axaml`
- A1 cockpit-grid tightening continuation: reduced default drawer height and inspector rail width, converted inspector framing to a rounded translucent satellite card, and softened lane/rail framing to keep timeline visual priority
- A10.1 readability normalization: introduced local readable text brushes and drawer tab/grid readability styles, then applied them to flow drawer status text and both drawer track grids for consistent legibility
- A10.2 readability normalization: migrated remaining inspector and lane microcopy literals to readability tokens, preserved semantic accent colors, and added a low-contrast literal regression guard in `WorkstationTimelineLayoutGuardTests`
- A10.3 governance hardening: promoted workstation readability brushes into `Themes/AvaloniaTheme.axaml` for global theme ownership and upgraded readability guard policy to semantic foreground whitelist enforcement
- A9.1 phrase-aware snapping: added phrase-boundary snap candidate metadata to flow transitions and introduced phrase-priority drag snap resolution with beat fallback, plus focused VM test coverage for the new snap engine behavior
- A9.2 editable phrase markers: introduced draggable phrase guide markers on the Flow timeline, persisted phrase-marker overrides in VM state with undo support, and extended architecture/VM tests to guard the new interaction hooks and overlay override behavior
- A9.3 and A9.4 chain completed: explicit marker confidence, phrase-region editing scaffolding, interactive region editing behavior, and final region intelligence/provenance merge pass now align under one tested phrasing model
- Post-A9 cleanup: fixed `HierarchicalLibraryViewModel` disposal hygiene and cleared the previously unrelated disposal-guard red
- Full-suite closure: `dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj` passed 756/756, establishing a fully green baseline after the A9 wave
- A9 design-note closure: added `DOCS/workstation/flow-intelligence-design-note.md` to document the final phrasing contract, including merge stages, provenance semantics, inspector truth, and snap priority
- A9 provenance copy polish: shifted user-facing phrase-region provenance labels from raw engine terms to DJ-facing copy (`Manual`, `Suggested`, `Hybrid`) without changing A9 behavior or test coverage

Next concrete slices:
1. A9 interaction-feel tuning pass.
- Tune deadzone and snap-threshold feel for phrase-marker and phrase-region edits using the existing test net as guardrail.
- Exit criteria: small drag interactions feel intentional and snap behavior remains musically predictable.

2. A10 continuation after A9 feel locks.
- Resume the remaining cockpit readability/contrast cleanup only after phrasing terminology and interaction feel are stable enough not to churn labels twice.
- Exit criteria: no critical timeline, inspector, or drawer state text falls into low-contrast gray-on-dark traps.

3. Regression coverage expansion.
- Add focused tests whenever command routing, overlay wording, or interaction thresholds change.
- Exit criteria: behavior, interaction, and wording regressions are caught before runtime QA.

## Acceptance Criteria
1. Timeline-first cockpit with constant context.
2. Compact default desktop density with DPI-aware scaling.
3. Actionable empty states and reduced warning noise.
4. Contextual inspector replacing global clutter.
5. Tool toggles switch lanes without page-level mode fragmentation.
6. Workstation perceived as unified player/editor cockpit.

## QA Checklist
- 1080p, 1440p, 4K, and 5K visual checks
- Windows scaling: 100%, 125%, 150%, 200%
- keyboard flow sanity for transport/zoom/pan/tools
- loaded vs unloaded cue/track state behavior
- no clipped controls in compact mode
- baseline and regression checks for timeline interaction latency

### QA Matrix Snapshot (2026-05-08)
- Build validation: PASS (`dotnet build ORBIT-Pure.sln -nologo`)
- Runtime launch smoke: PASS (`dotnet run --project SLSKDONET.csproj`, startup reached ORBIT boot/service init logs without immediate exception)
- Cockpit regression tests (new): PASS
	- `WorkstationDeckViewModelTests.BuildAutomationModeSummary_ReportsSyncAndViewportState`
	- `WorkstationDeckViewModelTests.BuildSamplesModeSummary_ReportsFallbackWhenPlaylistOrDeckMissing`
	- `MainViewModelNavigationTests.ResolvePageType_MapsKnownViews` (includes `NowPlayingPage` mapping)
- Full targeted test file run: PASS (`WorkstationDeckViewModelTests` + `MainViewModelNavigationTests`)
- 1080p / 1440p / 4K / 5K visual checks: PASS (manual validation confirmed)
- Windows scaling 100% / 125% / 150% / 200%: PASS (manual validation confirmed)
- Keyboard flow sanity (transport/zoom/pan/tools): PASS (manual validation confirmed)
- Loaded vs unloaded cue/track state behavior: PASS (manual validation confirmed)
- No clipped controls in compact mode: PASS (manual validation confirmed)
- Timeline interaction latency regression: PASS (manual validation confirmed)

Follow-ups:
- Continue spot-checking after major cockpit UI slices; current matrix is closed as PASS.

## Definition of Done
Workstation delivers a cohesive DAW-grade cockpit that meets or exceeds DJ.Studio Pro workflow expectations for clarity, density, and continuity.

## Final Agent Prompt
You are the Orbit Workstation Refactor Agent.

Goal:
Transform Workstation into a unified cockpit modeled after DJ.Studio Pro.

Do:
- create DPI token system
- migrate to vector icons where needed
- build cockpit grid layout
- implement tool toggles and contextual inspector
- keep timeline always visible
- integrate stems/automation/transitions in timeline context
- replace dead-end empty states with actionable CTAs
- consolidate player/workstation navigation semantics
- document every slice

Process:
- read docs/memory/workstation_cockpit.md before each task
- implement one task slice at a time
- validate with build/tests
- update memory after each slice
- commit and proceed to next task
