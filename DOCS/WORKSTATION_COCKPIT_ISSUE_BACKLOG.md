# Workstation Cockpit Issue Backlog

Status date: 2026-05-09
Scope: deduplicated backlog derived from the current Workstation implementation, the active cockpit epic, and the latest UI review.

This file is intentionally not a blind copy of the 20-item master list. Several items from that list are already implemented or substantially underway in this repository, so creating all of them as fresh GitHub issues would create noise and duplicate work.

## How To Use This Backlog

- `Create now`: issue should be created as an active engineering task.
- `Partial`: implementation exists, but a focused follow-up issue is still warranted.
- `Closed in current epic`: do not create a new issue unless regression or scope expansion appears.

## Recent Implementation Progress

The following backlog items are actively being burned down in the current repo state, but are not yet complete enough to close:

- `A5 / A6 / B1`: removed the global-header `ActivePlaylistFlowSummary` repeat, removed the waveform-lane `FocusedDeckActionSummary` repeat, removed the flow-inspector `WorkstationEligibilitySummary` repeat, removed the samples-inspector `FlowCtaStateSummary` repeat, removed the automation-inspector `AnalysisQueueSummary` repeat, and removed duplicate inspector copies of `AutomationModeSummary` and `SamplesModeSummary`.
- `A2 / B3`: added an explicit empty timeline canvas scaffold that remains visible when no decks are loaded, with guidance text tied to current playlist/readiness state instead of replacing the timeline workspace.
- `A2 / B3`: updated timeline empty-state layering so scaffold lanes persist as a background surface while empty-state guidance renders as a lightweight card, preserving the cockpit timeline mental model without masking deck rows.
- `A3 / B4`: reduced deck-first shell chrome in `WorkstationPage` by removing header master/transport chip stack, removing deck-labeled inspector headings/actions, and retitling remaining transport copy to timeline/track language.
- `A6 / B1`: removed duplicate header `ActiveToolSummary` surface so tool detail remains canonical in the inspector while header stays action-forward.
- `A6 / B1`: removed duplicate flow inspector `FlowWindowSummary` chip so viewport context remains canonical in the lane.
- `A2 / B3`: added architecture regression guards in `Tests/SLSKDONET.Tests/Architecture/WorkstationTimelineLayoutGuardTests.cs` to enforce persistent timeline scaffold rendering and non-replacement empty-state overlay behavior.
- `A4 / B5`: implemented Flow Slice 1 passive timeline overlays in `WorkstationPage` and `WorkstationViewModel` (transition blocks, phrase guides, beat guides, compatibility badges) with no interactivity yet.
- `A4 / B5`: extended architecture guards to assert passive Flow overlay layer presence and non-interactive timeline binding in `WorkstationTimelineLayoutGuardTests`.
- `A4 / B5`: implemented Flow Slice 2 selection + inspector coupling by adding selectable Flow transition blocks, selected-transition highlight state, inspector detail bindings, and clear-selection action.
- `A4 / B5`: extended test coverage with selection-state assertions in `WorkstationDeckViewModelTests` and updated architecture guards for selection command and inspector coupling bindings.
- `A4 / B5`: implemented Flow Slice 3 interactive transition-length handles with drag preview, phrase/beat snap behavior, and undo checkpoints wired through `IUndoService`.
- `A4 / B5`: expanded tests for length override/snap state rendering and architecture guards for handle drag hook bindings.
- `A4 / B8`: prepared Flow Slice 4 implementation blueprint in `DOCS/workstation/flow-slice-4-transition-presets-blueprint.md` covering transition presets, harmonic/energy compatibility scoring, inspector preset coupling, and apply/reset undo strategy.
- `A4 / B8`: implemented Flow Slice 4.1 VM scaffolding in `ViewModels/Workstation/WorkstationViewModel.cs` (preset catalog model, harmonic/energy scoring helpers, combined compatibility score, ranking helpers, warning flags, and inspector score helper properties).
- `A4 / B8`: extended `Tests/SLSKDONET.Tests/ViewModels/WorkstationDeckViewModelTests.cs` with Slice 4.1 coverage for harmonic scoring buckets, energy scoring buckets, and preset ranking behavior.
- `B1-B12`: materialized the create-now cockpit backlog into live GitHub issues (`#153-#164`) via `Tools/create-workstation-cockpit-issues.ps1`, including canonical labels and full issue bodies (why/acceptance/plan).
- These cuts keep detailed workflow state in the inspector when it is tool-specific and in the drawer when it is bulk-workflow-specific.
- Validation status: `dotnet build ORBIT-Pure.sln -nologo` and `dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~WorkstationTimelineLayoutGuardTests|FullyQualifiedName~BuildFlowTransitions_"` passed on 2026-05-10 after these declutter and Flow overlay slices.

## GitHub Materialization Snapshot (2026-05-10)

Create-now issues were created on `MeshDigital/Orbit-pure` and are now operational work items:

- B1: https://github.com/MeshDigital/Orbit-pure/issues/153
- B2: https://github.com/MeshDigital/Orbit-pure/issues/154
- B3: https://github.com/MeshDigital/Orbit-pure/issues/155
- B4: https://github.com/MeshDigital/Orbit-pure/issues/156
- B5: https://github.com/MeshDigital/Orbit-pure/issues/157
- B6: https://github.com/MeshDigital/Orbit-pure/issues/158
- B7: https://github.com/MeshDigital/Orbit-pure/issues/159
- B8: https://github.com/MeshDigital/Orbit-pure/issues/160
- B9: https://github.com/MeshDigital/Orbit-pure/issues/161
- B10: https://github.com/MeshDigital/Orbit-pure/issues/162
- B11: https://github.com/MeshDigital/Orbit-pure/issues/163
- B12: https://github.com/MeshDigital/Orbit-pure/issues/164

Automation source:
- `Tools/create-workstation-cockpit-issues.ps1`

## Reconciled Status

### Closed in current epic

1. Create DPI token system
2. Replace workstation shell icons with SVG/resource-backed vectors
3. Implement tool toggle system
4. Replace empty states with actionable CTAs
5. Update navigation semantics so Workstation is primary playback/edit destination
6. Build first-pass contextual inspector

### Partial, issue still warranted

1. Replace Workstation with a true cockpit grid
2. Make timeline always visible in all states, including empty state
3. Remove deck-first UI clutter from visible shell chrome
4. Integrate Flow Builder more deeply into the timeline surface
5. Simplify header layout further
6. Remove fragmented panels and repeated status surfaces
7. Add automation lane system beyond the current placeholder/summary state
8. Add stems lanes beyond the current first-pass controls
9. Implement phrase-aware snapping and phrase-aware editing workflow
10. Fix remaining low-contrast text and readability issues

## Partial Follow-Up Issue Drafts

These items have partial implementation in the current epic, but they still warrant focused GitHub issues. The intent is to capture the remaining work without reopening already completed foundation slices.

### A1. Replace Workstation With A True Cockpit Grid

Labels: `workstation`, `ui`, `cockpit`, `refactor`

Why it matters:
The current Workstation is materially closer to the target cockpit than before, but it still behaves too much like a stack of panels around a timeline rather than a single cohesive editing surface.

Description:
Push the layout from "improved workstation shell" to a true cockpit grid where the timeline is the primary canvas and all supporting surfaces read as contextual satellites rather than separate panels.

Acceptance criteria:
- Timeline clearly dominates the visual hierarchy.
- Inspector and drawer read as supporting surfaces rather than peer panels.
- Leftover panel-like framing is removed or materially reduced.
- No workflow regressions are introduced for transport, tool switching, or track prep.

Implementation plan:
1. Audit top-level workstation layout containers and framing in `Views/Avalonia/WorkstationPage.axaml`.
2. Convert the shell toward a clearer cockpit grid with the timeline as the visual center.
3. Compress or remove stacked panel sections that still behave like mini-pages.
4. Re-run runtime smoke and screenshot-based visual review.

### A2. Make Timeline Always Visible In All States

Labels: `workstation`, `ui`, `cockpit`, `timeline`

Why it matters:
The cockpit model breaks if the timeline disappears or becomes visually subordinate whenever no tracks are loaded.

Description:
Keep the timeline visible in all workstation states, including empty states, with the empty-state messaging layered around the canvas rather than replacing it.

Acceptance criteria:
- Empty timeline canvas remains visible with zero loaded tracks.
- Empty-state copy does not replace the canvas.
- Lane scaffolding remains visible enough to preserve the workstation mental model.
- Load/import/analyze affordances remain accessible.

Implementation plan:
1. Audit timeline visibility conditions and empty-state branching.
2. Add persistent empty-canvas rendering and lightweight lane scaffolding.
3. Reposition empty-state CTAs so they support, rather than replace, the timeline.
4. Validate visually at multiple viewport sizes.

### A3. Remove Deck-First UI Clutter From Visible Shell Chrome

Labels: `workstation`, `ui`, `cockpit`, `refactor`

Why it matters:
Decks should remain operational primitives, not the dominant mental model of the workstation shell.

Description:
Reduce deck-first chrome in visible workstation surfaces while preserving precise deck targeting where it is still operationally necessary.

Acceptance criteria:
- Top-level workstation chrome no longer centers the experience around A/B/C/D deck affordances.
- Deck targeting is still available in contextual surfaces where needed.
- Header and inspector emphasize timeline workflow over deck workflow.
- Track-row or contextual inspector actions own the remaining deck-specific actions.

Implementation plan:
1. Audit deck-focused controls in header, inspector, drawer, and lane regions.
2. Remove or demote top-level deck chrome that is informational rather than actionable.
3. Move remaining necessary deck actions into contextual surfaces.
4. Validate that deck-loading workflows still remain discoverable.

### A4. Integrate Flow Builder More Deeply Into The Timeline Surface

Labels: `workstation`, `ui`, `cockpit`, `flow`

Why it matters:
Flow still behaves partly like an attached subsystem instead of a first-class timeline-native experience.

Description:
Move key Flow context into the timeline and lane model so shaping, readiness, and browsing feel embedded in the cockpit rather than appended through separate explanatory surfaces.

Acceptance criteria:
- Flow context is visible directly in the timeline region.
- Flow no longer depends on repeated side summaries for core comprehension.
- Drawer remains for bulk browsing or playlist operations rather than primary flow understanding.
- Flow mode feels native to the cockpit instead of panel-driven.

Implementation plan:
1. Audit which Flow concepts still render outside the timeline context.
2. Move the most important Flow state into canvas or lane representation.
3. Simplify or remove redundant Flow-specific explanatory surfaces.
4. Retest discoverability of overlay and playlist actions.

### A5. Simplify Header Layout Further

Labels: `workstation`, `ui`, `cockpit`, `refactor`

Why it matters:
The header still risks carrying too much state, which increases density and competes with the timeline for attention.

Description:
Reduce the header to essential transport, tool selection, and minimal session context, with supporting detail moved into inspector or drawer surfaces.

Acceptance criteria:
- Header contains only essential transport, tool toggles, and minimal session context.
- Repeated summaries and debug-like text are removed.
- Spacing and density feel materially lighter.
- Critical actions remain directly accessible.

Implementation plan:
1. Audit all current header bindings and controls.
2. Remove or relocate nonessential informational text.
3. Tighten spacing and hierarchy around transport and tool controls.
4. Validate that no critical workflow entry point was lost.

### A6. Remove Fragmented Panels And Repeated Status Surfaces

Labels: `workstation`, `ui`, `cockpit`, `refactor`

Why it matters:
Repeated status surfaces remain one of the main reasons the workstation still feels crowded after prior cleanup slices.

Description:
Continue collapsing fragmented status and summary UI so each concept has one canonical home in the shell.

Acceptance criteria:
- No major state appears in more than one adjacent surface without a clear reason.
- Inspector is the primary detailed information surface.
- Drawer is the primary bulk-workflow surface.
- Header remains minimal and action-forward.

Implementation plan:
1. Audit summary/status bindings across header, lanes, inspector, and drawer.
2. Remove duplicate informational surfaces first.
3. Consolidate remaining detail into inspector or drawer.
4. Run runtime smoke and visual review after each slice.

### A7. Add Automation Lane System Beyond The Current Placeholder State

Labels: `workstation`, `ui`, `automation`, `cockpit`

Why it matters:
Automation currently reads more like a mode summary than a real editing workflow, which leaves a large gap between the current state and the target workstation bar.

Description:
Expand automation mode into a lane-driven editing experience with visible automation data and selection-aware inspector support.

Acceptance criteria:
- Automation mode exposes lane content rather than only summaries or toggles.
- Editable automation points, regions, or curves are visible in timeline context.
- Inspector reacts to automation selection.
- Workflow supports at least one concrete automation editing path end-to-end.

Implementation plan:
1. Define automation lane data model and editing primitives.
2. Render automation content in the timeline region.
3. Bind inspector to selected automation entities.
4. Add focused validation around automation summaries and editing state.

### A8. Add Stems Lanes Beyond The Current First-Pass Controls

Labels: `workstation`, `ui`, `stems`, `cockpit`

Why it matters:
Stems support exists, but it is not yet represented as a full lane-driven cockpit workflow.

Description:
Expand stems mode from lightweight controls into a lane-aware editing and visibility model that still avoids reintroducing clutter.

Acceptance criteria:
- Stems state is represented in timeline context.
- Per-stem visibility or control surfaces are available without taking over the header.
- Inspector remains the primary detailed stems surface.
- Quick lane controls stay minimal and contextual.

Implementation plan:
1. Define stems lane/state representation.
2. Render a first usable stems lane view in the timeline.
3. Keep detailed control in the inspector.
4. Validate that stems mode remains legible and uncluttered.

### A9. Implement Phrase-Aware Snapping And Phrase-Aware Editing Workflow

Labels: `workstation`, `ui`, `timeline`, `analysis`

Why it matters:
Phrase awareness is part of the target editing quality bar and is critical for credible timeline-first workflow.

Description:
Add phrase-aware snapping and phrase-aware editing aids based on the existing analysis and cue infrastructure.

Acceptance criteria:
- Phrase boundaries are visible in timeline context.
- Snapping can target phrase boundaries.
- Active phrase snapping has visible feedback.
- Phrase-aware behavior works in the relevant editing modes.

Implementation plan:
1. Audit existing phrase, cue, and analysis data.
2. Add phrase boundary visualization.
3. Introduce phrase snapping state and interaction logic.
4. Validate behavior in waveform and flow-related workflows.

### A10. Fix Remaining Low-Contrast Text And Readability Issues

Labels: `workstation`, `ui`, `accessibility`

Why it matters:
Secondary text, chips, and helper surfaces still risk poor readability under the current cockpit density and dark-theme presentation.

Description:
Finish a focused contrast and readability pass across the workstation so secondary information remains readable without adding more visual clutter.

Acceptance criteria:
- Secondary text meets acceptable contrast against its background.
- Chips and muted helper text remain readable at workstation density.
- Manual screenshot-based review confirms improved legibility.
- Changes use tokenized colors where possible.

Implementation plan:
1. Audit the main workstation text and chip color pairs.
2. Adjust theme tokens or local styles where needed.
3. Re-run visual review in the most crowded workstation states.
4. Capture any remaining gaps as smaller follow-up items.

### Create now

1. Reduce repeated state surfaces in Workstation shell
2. Finish cockpit-first layout reduction
3. Make timeline persist as empty canvas when no tracks are loaded
4. Remove remaining visible deck-first affordances from workstation chrome
5. Deepen Flow integration into timeline canvas
6. Add real automation lane editing
7. Add real stems lane editing
8. Add transition preset system
9. Add phrase-aware snapping
10. Implement timeline virtualization and waveform/lane rendering strategy
11. Create unified workstation state model
12. Finish accessibility contrast pass

## Issue Drafts

### 1. Reduce Repeated State Surfaces In Workstation Shell

Labels: `workstation`, `ui`, `cockpit`, `refactor`

Why it matters:
The current Workstation still repeats the same state across header, lane, inspector, and drawer surfaces. This makes the cockpit feel crowded even after recent declutter slices and weakens the “single surface” mental model.

Description:
Continue reducing repeated summary/status UI in `Views/Avalonia/WorkstationPage.axaml` so that each piece of information appears in one primary place. Prioritize removing duplicate informational text before removing actionable controls.

Acceptance criteria:
- Header contains only transport, essential tool selection, and minimal session context.
- Flow/stems/export/tool summaries are not repeated in multiple adjacent regions.
- Inspector remains the primary detail surface.
- Drawer remains the primary bulk-workflow surface.
- Runtime QA confirms discoverability did not regress.

Implementation plan:
1. Audit remaining repeated summary bindings in `WorkstationPage.axaml`.
2. Remove duplicate informational surfaces first.
3. Re-run runtime smoke and manual cockpit review.
4. Update cockpit memory and epic.

### 2. Finish Cockpit-First Layout Reduction

Labels: `workstation`, `ui`, `cockpit`, `refactor`

Why it matters:
The current shell is closer to a cockpit than before, but it still reads like stacked panels around a timeline rather than one cohesive workstation.

Description:
Push the shell further toward a true cockpit by reducing panel-like framing and making the timeline region visually dominant.

Acceptance criteria:
- Timeline region is visually dominant at first glance.
- Header and drawer feel secondary to canvas work.
- Inspector reads as contextual support, not a competing panel system.
- No new workflow dead ends are introduced.

Implementation plan:
1. Reduce panel framing where it competes with the timeline.
2. Remove or compress any leftover shell sections that behave like separate mini-pages.
3. Validate with screenshot-based before/after review.

### 3. Make Timeline Persist As Empty Canvas

Labels: `workstation`, `ui`, `cockpit`

Why it matters:
The timeline is supposed to be the center of gravity. If it disappears or becomes visually secondary when no tracks are loaded, the cockpit model breaks.

Description:
Render an explicit empty timeline canvas and lane scaffolding even when no tracks are currently loaded.

Acceptance criteria:
- Timeline remains visible when zero tracks are loaded.
- Empty-state messaging is subordinate to the canvas, not replacing it.
- Load/import/analyze actions still remain accessible.

Implementation plan:
1. Audit visibility rules for the timeline and lane surfaces.
2. Render empty lane structure with lightweight empty-state affordances.
3. Validate visually at multiple sizes.

### 4. Remove Remaining Visible Deck-First Affordances

Labels: `workstation`, `ui`, `cockpit`, `refactor`

Why it matters:
Decks should remain engine primitives, not the dominant interaction model of the workstation shell.

Description:
Reduce visible deck-first controls in top-level workstation chrome while preserving explicit deck targeting where it is operationally necessary.

Acceptance criteria:
- Deck targeting remains possible where needed.
- Top-level shell no longer centers the workflow around A/B/C/D controls.
- Inspector or track-row actions own the remaining deck-specific operations.

Implementation plan:
1. Audit deck-focused controls in header, inspector, and lane regions.
2. Keep only controls that are necessary for direct manipulation.
3. Move detail and prep controls into contextual surfaces.

### 5. Deepen Flow Integration Into Timeline Canvas

Labels: `workstation`, `ui`, `cockpit`, `flow`

Why it matters:
Flow still behaves partially like an attached subsystem instead of a first-class timeline overlay.

Description:
Move Flow behavior further into the timeline canvas so that shaping, readiness, and browsing feel embedded instead of appended.

Acceptance criteria:
- Flow context is visible directly in the timeline region.
- Flow no longer depends on stacked explanatory surfaces.
- Drawer remains for bulk browsing, not for core flow context.

Implementation plan:
1. Audit what Flow still renders outside the timeline context.
2. Move the most important flow state into the canvas/lane representation.
3. Retest discoverability of track overlay and playlist actions.

### 6. Add Real Automation Lane Editing

Labels: `workstation`, `ui`, `automation`, `cockpit`

Why it matters:
Automation currently reads more like state summary than editable automation workflow.

Description:
Implement real automation lane interaction for volume/filter/EQ/FX automation rather than only summaries and toggles.

Acceptance criteria:
- Automation mode exposes editable lane content.
- Automation points or curves are visible in timeline context.
- Inspector supports selected automation detail.

Implementation plan:
1. Define automation lane data model.
2. Render lane content in the timeline region.
3. Bind inspector to automation selection.

### 7. Add Real Stems Lane Editing

Labels: `workstation`, `ui`, `stems`, `cockpit`

Why it matters:
Stems mode currently provides useful controls, but not a full lane-driven editing model comparable to the target cockpit quality.

Description:
Expand Stems mode from quick toggles and inspector actions into a lane-aware editing surface.

Acceptance criteria:
- Stems have visible lane/state representation in timeline context.
- Inspector remains the primary detailed stems surface.
- Lane controls support quick operational edits without becoming crowded again.

Implementation plan:
1. Define lane representation for stems state.
2. Render lane-level stems visualization.
3. Keep quick toggles minimal and contextual.

### 8. Add Transition Preset System

Labels: `workstation`, `ui`, `transitions`, `cockpit`

Why it matters:
Transition shaping is core to the DJ.Studio-style workstation target and is currently underrepresented in the UI model.

Description:
Add a transition preset system with selectable blend behaviors and inspector support.

Acceptance criteria:
- Presets such as Crossfade, Bass Swap, Full, None, and Custom are available.
- Selected preset affects transition planning surfaces.
- Inspector exposes preset parameters.

Implementation plan:
1. Define transition preset model.
2. Add preset selection UI in contextual surfaces.
3. Bind summaries and transition guidance to selected preset.

### 9. Add Phrase-Aware Snapping

Labels: `workstation`, `ui`, `timeline`, `analysis`

Why it matters:
Phrase awareness is part of the target product bar and critical for credible timeline-first editing.

Description:
Add phrase-aware snapping and editing aids based on existing analysis/cue infrastructure.

Acceptance criteria:
- Phrase boundaries are represented in the timeline.
- Snapping can target phrase boundaries.
- Visual feedback appears when phrase snapping is active.

Implementation plan:
1. Audit existing phrase/cue analysis data.
2. Introduce phrase snapping state and visualization.
3. Validate interaction in flow and waveform modes.

### 10. Implement Timeline Virtualization Strategy

Labels: `workstation`, `performance`, `timeline`, `refactor`

Why it matters:
The target cockpit density and lane complexity will not scale without a deliberate rendering/virtualization approach.

Description:
Implement a clear timeline virtualization strategy for waveform and lane rendering so richer cockpit features do not degrade responsiveness.

Acceptance criteria:
- Rendering strategy is documented and implemented for large track/lane counts.
- Waveform/lane surfaces do not perform naive full-surface redraws.
- Runtime interaction remains responsive under expected workstation load.

Implementation plan:
1. Define virtualization/rendering architecture.
2. Choose retained/cached rendering boundaries.
3. Add targeted performance checks.

### 11. Create Unified Workstation State Model

Labels: `workstation`, `architecture`, `state`, `refactor`

Why it matters:
As the cockpit becomes more contextual, state can no longer be spread ad hoc across view bindings without increasing fragility.

Description:
Create a more explicit workstation state model to unify active tool, selection, zoom, inspector mode, and major workflow context.

Acceptance criteria:
- Active tool, focus, zoom, and inspector context are centrally represented.
- Cross-surface binding logic becomes simpler, not more fragmented.
- New modes/features can plug into the same state model.

Implementation plan:
1. Audit current state dispersion in `WorkstationViewModel`.
2. Define a normalized workstation state object or layer.
3. Migrate the highest-friction bindings first.

### 12. Finish Accessibility Contrast Pass

Labels: `workstation`, `ui`, `accessibility`

Why it matters:
Several surfaces still risk low readability under dark-theme density, especially supporting text and secondary status rows.

Description:
Perform a deliberate contrast pass across cockpit text, chips, and secondary surfaces.

Acceptance criteria:
- Secondary text meets acceptable contrast against its background.
- Status chips and muted helper text remain readable at workstation density.
- Manual visual review confirms improvement.

Implementation plan:
1. Audit color pairs across Workstation surfaces.
2. Adjust tokenized colors where possible.
3. Re-run screenshot/manual contrast review.

## Issues That Should Not Be Created As New Standalone Items

These are already implemented or already represented by the active epic and recent slices:

- Create DPI Token System
- Replace All Icons with SVG
- Implement Tool Toggle System
- Replace Empty States with Actionable CTAs
- Remove Redundant Player View

These can still receive follow-up issues if regressions or scope expansion appear, but they should not be recreated as fresh “missing feature” tickets.

## Authoritative Issue Generation List

This is the final, merged issue-generation source for VS Code or any agent workflow.

Create GitHub issues for:

### Partial follow-up issues

- A1. Replace Workstation With A True Cockpit Grid
- A2. Make Timeline Always Visible In All States
- A3. Remove Deck-First UI Clutter From Visible Shell Chrome
- A4. Integrate Flow Builder More Deeply Into The Timeline Surface
- A5. Simplify Header Layout Further
- A6. Remove Fragmented Panels And Repeated Status Surfaces
- A7. Add Automation Lane System Beyond The Current Placeholder State
- A8. Add Stems Lanes Beyond The Current First-Pass Controls
- A9. Implement Phrase-Aware Snapping And Phrase-Aware Editing Workflow
- A10. Fix Remaining Low-Contrast Text And Readability Issues

### Create-now issues

- B1. Reduce Repeated State Surfaces In Workstation Shell
- B2. Finish Cockpit-First Layout Reduction
- B3. Make Timeline Persist As Empty Canvas
- B4. Remove Remaining Visible Deck-First Affordances
- B5. Deepen Flow Integration Into Timeline Canvas
- B6. Add Real Automation Lane Editing
- B7. Add Real Stems Lane Editing
- B8. Add Transition Preset System
- B9. Add Phrase-Aware Snapping
- B10. Implement Timeline Virtualization Strategy
- B11. Create Unified Workstation State Model
- B12. Finish Accessibility Contrast Pass

Do not create GitHub issues for anything listed under `Closed in current epic` unless the issue is explicitly about regression, rollback, or major scope expansion.

## VS Code Issue Generation Instructions

Use the following block as the authoritative agent prompt when generating GitHub issues from this file:

```text
You are the Orbit Issue Generation Agent.

Use the authoritative backlog in:
DOCS/WORKSTATION_COCKPIT_ISSUE_BACKLOG.md

Create GitHub issues ONLY for items under:
- Partial follow-up issue drafts (A1-A10)
- Create now (B1-B12)

Do NOT create issues for items listed under “Closed in current epic”.

For each issue:
- Use the title exactly as listed
- Include Why it matters
- Include Description
- Include Acceptance criteria
- Include Implementation plan
- Add labels: workstation, ui, cockpit, refactor where relevant, plus any mode-specific labels already listed in the issue draft

After generating the issues:
- Update the workstation epic with issue links
- Record the issue creation pass in docs/memory/workstation_cockpit.md
- Report completion
```