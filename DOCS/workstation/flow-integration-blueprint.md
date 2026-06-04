# Flow Integration Blueprint

Status: proposed implementation blueprint
Status date: 2026-05-09
Scope: timeline-native Flow integration after shell stabilization

## Goal

Embed Flow behavior directly in the timeline canvas and lane system so transition planning is visible and editable in place, without reintroducing shell clutter.

## Non-Goals

- No new deck-first shell controls
- No header density increase
- No secondary flow dashboard
- No replacement of inspector or drawer roles

## Canonical Surface Rules

- Header: transport, tool toggles, minimal tempo/session context
- Timeline and lanes: primary flow context and editing handles
- Inspector: selected transition detail and parameter editing
- Drawer: track pool, suggestions, bulk operations

## Feature Breakdown

### Phase 1: Passive Flow Overlay

Render non-interactive Flow context on timeline:

- Transition windows between adjacent tracks
- Phrase alignment guides
- Beat alignment guides
- Transition length badges
- Harmonic compatibility badges

Acceptance criteria:

- Flow context is visible in timeline and does not rely on inspector text summaries
- No overlap with existing row controls at 1080p
- No new duplicate status surfaces in header or inspector

### Phase 2: Interactive Transition Handles

Enable direct manipulation in timeline:

- Transition start handle
- Transition end handle
- Blend shape preset handle
- Snap-aware dragging to phrase and beat grid

Acceptance criteria:

- Handles drag smoothly and respect snap/quantize settings
- Inspector updates to selected transition entity
- Undo and redo work for transition edits

### Phase 3: Inspector Coupling

When a transition is selected, inspector provides:

- Preset selector
- Length and overlap controls
- Harmonic correction suggestions
- Per-transition notes and warnings

Acceptance criteria:

- Inspector state is driven only by timeline selection
- No duplicate transition detail in lane text blocks

### Phase 4: Drawer Integration

Use drawer only for supporting operations:

- Track suggestions for replacement or reorder
- Compatibility-ranked candidates
- Bulk analysis actions

Acceptance criteria:

- Flow comprehension does not require opening drawer
- Drawer remains optional for editing already-loaded transitions

## Proposed View Model Additions

Add transition entities in workstation VM:

- FlowTransitions: collection of transition lane view models
- SelectedFlowTransition: active transition selection
- CanEditFlowTransitions: gate for interactive mode

Flow transition VM fields:

- SourceTrackId
- TargetTrackId
- StartSeconds
- EndSeconds
- LengthSeconds
- PhraseAligned
- BeatAligned
- HarmonicCompatibilityScore
- PresetId
- WarningFlags

Commands:

- SelectFlowTransitionCommand
- MoveFlowTransitionStartCommand
- MoveFlowTransitionEndCommand
- ApplyFlowPresetCommand
- ResetFlowTransitionCommand

## Proposed View Changes

Primary target:

- Views/Avalonia/WorkstationPage.axaml

Potential extraction target after stabilization:

- Views/Avalonia/Workstation/FlowLaneOverlay.axaml

Add a dedicated flow overlay layer inside the timeline row stack:

- Layer order: scaffold -> deck rows -> flow overlays -> interaction handles
- Keep overlay hit-testing scoped to transition handles only

## State and Event Model

Selection flow:

1. User clicks transition overlay
2. SelectedFlowTransition changes
3. Inspector binds to selected transition
4. Lane highlights selected segment

Edit flow:

1. User drags handle
2. VM updates StartSeconds and EndSeconds
3. Snap and quantize rules apply
4. Summary fields recompute
5. Undo checkpoint recorded

## Telemetry and Diagnostics

Emit lightweight diagnostics for:

- Transition handle drag start and stop
- Snap events and fallback unsnapped edits
- Preset apply events
- Transition warning state changes

## Test Plan

Unit tests:

- Transition selection updates inspector state
- Handle edits obey snap and quantize
- Harmonic and phrase flags recompute correctly

Architecture tests:

- Flow summary is not duplicated across lane and inspector surfaces
- Timeline layer remains present regardless of loaded state

Manual QA gate:

- Use DOCS/workstation/runtime-qa-cockpit-gate.md

## Delivery Slices

Slice 1:

- Add transition VM model and passive overlay rendering
- Keep overlays read-only

Slice 2:

- Add selection and inspector coupling

Slice 3:

- Add handle drag editing with snap and undo

Slice 4:

- Add preset application and warning surfacing

## Exit Criteria

- Flow feels timeline-native and not panel-driven
- Inspector is detail-only and contextual
- Drawer remains bulk workflow surface
- Build and targeted tests pass
- Runtime QA gate passes
