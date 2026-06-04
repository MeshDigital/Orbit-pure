# Flow Slice 4 Blueprint: Transition Presets

Status: proposed implementation blueprint
Status date: 2026-05-10
Scope: musical intelligence layer on top of Flow slices 1-3

## Goal

Add transition presets that apply musically meaningful behavior to selected Flow transitions, with phrase-aware and beat-aware suggestions.

## Preconditions

- Flow Slice 1 overlays are present
- Flow Slice 2 selection and inspector coupling are present
- Flow Slice 3 handle drag, snap, and undo are present
- Runtime QA gate from DOCS/workstation/runtime-qa-cockpit-gate.md has a recent PASS entry

## Preset Catalog

Initial preset set:

- Crossfade: balanced overlap and gain curve
- Bass Swap: staged low-frequency handoff during overlap
- Full: full-spectrum overlap for energetic lifts
- None: hard handoff with minimal overlap
- Custom: user-defined parameters derived from selected transition state

## Preset Data Model

Add a transition preset record:

- PresetId
- DisplayName
- Description
- CurveType
- DefaultLengthSeconds
- LowBandStrategy
- MidBandStrategy
- HighBandStrategy
- MinCompatibilityScore
- RequiresPhraseAlignment
- SuggestedEnergyDeltaRange

Extend Flow transition state:

- AppliedPresetId
- SuggestedPresetIds
- HarmonicCompatibilityScore
- EnergyCompatibilityScore
- CombinedCompatibilityScore
- WarningFlags

## Harmonic Compatibility Model

Inputs:

- source Camelot key
- target Camelot key
- semitone shift

Output:

- score from 0 to 100
- label: lock, safe, stretch, risky

Scoring baseline:

- distance 0 or 1: 90-100
- distance 2: 70-89
- distance 3-4: 45-69
- distance > 4: 0-44

## Energy Curve Alignment Model

Inputs:

- source energy value
- target energy value
- transition length

Output:

- score from 0 to 100
- label: smooth, lift, aggressive, mismatch

Scoring baseline:

- delta <= 0.10: smooth
- delta 0.11-0.20: lift
- delta 0.21-0.35: aggressive
- delta > 0.35: mismatch

## Preset Suggestion Strategy

For selected transition:

1. Compute harmonic score
2. Compute energy score
3. Blend into combined score
4. Filter presets by compatibility constraints
5. Rank suggested presets
6. Surface top 3 in inspector

Rule examples:

- If harmonic is risky, de-prioritize Full
- If energy delta is high, prefer Crossfade or Bass Swap
- If phrase aligned and harmonic lock, prioritize Bass Swap and Full

## Inspector UI Plan

Flow inspector additions for selected transition:

- Preset selector dropdown
- Suggested presets chips
- Harmonic score row
- Energy score row
- Combined score row
- Warnings panel
- Apply preset button
- Reset preset button

UI constraints:

- No duplicate preset data in header
- No preset explanation panel in drawer
- Inspector remains the canonical detail surface

## Application Engine

Add commands:

- ApplyFlowPresetCommand
- ResetFlowPresetCommand
- CycleFlowPresetCommand

Apply flow:

1. Validate selected transition
2. Resolve preset definition
3. Apply overlap and curve parameters
4. Update transition fields
5. Recompute compatibility and warnings
6. Push undo checkpoint

Undo behavior:

- one operation per apply or reset
- redo restores exact preset and parameters

## ViewModel Changes

Primary target:

- ViewModels/Workstation/WorkstationViewModel.cs

Additions:

- Preset catalog source
- suggestion computation helpers
- selected transition preset fields
- inspector summary properties for scores and warnings

## XAML Changes

Primary target:

- Views/Avalonia/WorkstationPage.axaml

Additions:

- Flow inspector preset selector
- score rows and warning surface
- preset chips for quick apply

No changes to:

- header transport density
- drawer primary role

## Test Plan

### VM tests

- harmonic score mapping returns expected bucket labels
- energy score mapping returns expected bucket labels
- preset suggestion ranking prefers compatible presets
- apply preset mutates selected transition state
- reset preset restores neutral state
- undo/redo restore preset application exactly

### Architecture guards

- Flow inspector contains preset selector binding
- Flow inspector contains harmonic and energy score bindings
- drawer does not contain preset detail bindings

### Focused runtime checks

- selecting transition updates preset suggestions
- applying preset updates inspector and overlay summaries
- undo and redo behave deterministically
- resizing after preset application keeps snap and warnings consistent

## Delivery Sequence

Slice 4.1:

- add preset model and static catalog
- add harmonic and energy score computation helpers

Slice 4.2:

- add inspector preset UI and suggestion list
- wire apply and reset commands

Slice 4.3:

- add undo operations for preset apply and reset
- add warning flags and compatibility badges

Slice 4.4:

- runtime QA pass and doc updates

## Exit Criteria

- selected transitions support preset apply and reset
- harmonic and energy scores are visible and test-backed
- undo/redo checkpoints work for preset operations
- no duplicate preset state surfaces across shell regions
- build and focused tests pass
