# Flow Intelligence Design Note

Status: active implementation contract
Status date: 2026-05-11
Scope: A9 phrase-aware flow intelligence after A9.4-C completion

## Purpose

Capture the current phrasing contract for Workstation Flow so future slices do not have to re-derive the model from `WorkstationViewModel.cs` and tests.

This note describes what the subsystem guarantees today, not speculative future behavior.

## What A9 Now Guarantees

The Flow phrasing subsystem is now:

- visible in the timeline
- directly editable
- undoable
- provenance-aware
- confidence-aware
- inspector-aware
- snap-prioritized by musical intent

The system no longer treats phrase context as a single marker or a single region. A transition can now carry a merged ordered region set with explicit and inferred semantics.

## Core Entities

### FlowPhraseRegion

`FlowPhraseRegion` is the canonical phrase-region model for a selected transition.

It carries:

- `TransitionKey`
- `StartSeconds`
- `EndSeconds`
- `IsExplicit`
- `Confidence`
- `SourceCueIds`
- `Provenance`

Derived display fields such as `CanvasLeft`, `CanvasWidth`, `SpanLabel`, and `Tooltip` are built from the canonical timing and provenance state.

### FlowPhraseRegionProvenance

Current provenance values:

- `ExplicitUser`
- `Inferred`
- `Mixed`

Interpretation:

- `ExplicitUser`: region exists because the user placed or shaped it directly.
- `Inferred`: region exists only because the system synthesized it from cue and anchor context.
- `Mixed`: region survives after an explicit edit reshaped an inferred span.

`Mixed` is not a fallback label. It specifically means a user edit altered the structure of previously inferred material without making the surviving remainder fully explicit.

## Phrase-Region Pipeline

The final region list for a transition is built by `BuildMergedPhraseRegions(...)`.

The pipeline is deterministic and runs in five stages.

### 1. Inferred region construction

`BuildInferredPhraseRegionsFromCues(...)` seeds a baseline inferred span for the transition range, then expands around phrase-relevant cues and the source and target anchors.

Current cue roles used as phrase candidates:

- `PhraseStart`
- `Intro`
- `Build`
- `Drop`
- `Breakdown`
- `Outro`

The seed region ensures explicit edits always have a stable inferred baseline to shape against.

### 2. Normalization

`NormalizePhraseRegions(...)` clamps regions to the active transition range, orders them by start time, and removes degenerate spans smaller than the minimum visible duration.

Current normalization rules:

- clamp start and end to transition range
- enforce `end >= start`
- discard spans smaller than `0.05s`
- return ordered list by start time

### 3. Inferred merge

`MergeAdjacentOrOverlappingInferredRegions(...)` merges inferred spans that overlap or are separated only by a small adjacency gap.

Current adjacency tolerance:

- `0.1s`

When inferred regions merge:

- timing is widened to the combined span
- cue ids are unioned
- confidence is averaged before later recomputation
- provenance remains `Inferred`

### 4. Explicit override shaping

`ApplyExplicitPhraseRegionOverrides(...)` introduces the explicit user region, then reshapes any overlapping inferred material.

Behavior:

- non-overlapping inferred regions survive unchanged
- overlapping inferred regions are trimmed or split around the explicit region
- surviving inferred fragments become `Mixed`
- the explicit region is normalized and inserted as `ExplicitUser`

This is the key rule that prevents explicit edits from being treated as a simple replacement toggle. The final list preserves both user intent and any still-valid inferred context.

### 5. Provenance and confidence recomputation

`ComputeProvenanceAndConfidence(...)` is the final authority for region metadata.

Behavior:

- explicit regions are always rewritten as `ExplicitUser` with confidence `1.0`
- mixed and inferred regions are re-evaluated from cues inside the final span
- up to four cue ids are retained for traceability in tooltips and summaries

`ComputeInferredRegionConfidence(...)` applies the current confidence rules:

- no cues in span: `0.45` for inferred, `0.55` for mixed
- with cues: average cue confidence, with a small mixed-region uplift
- clamp final confidence to `0.35 - 0.95`

## Selection and Display Contract

`FlowTransitionOverlayViewModel` now owns the final merged phrase-region list for a transition.

It guarantees:

- `PhraseRegions` is the final ordered list after inference, merge, override shaping, and metadata recomputation
- `ActivePhraseRegion` prefers explicit regions first, then falls back to the first remaining region
- `PhraseRegionSpanLabel` reports region count, total span, and rollup provenance
- `PhraseRegionTooltip` reflects the active region and current region summary

This means the inspector and overlay surface now report the same phrasing truth.

## Inspector Contract

The Flow inspector now reports phrase-region structure through:

- `FlowInspectorPhraseRegionCount`
- `FlowInspectorPhraseRegionSpanSeconds`
- `FlowInspectorPhraseRegionProvenanceLabel`
- `FlowInspectorPhraseRegionSpanLabel`

Current rollup behavior:

- all explicit -> `Explicit`
- all inferred -> `Inferred`
- any combination -> `Mixed`

User-facing copy currently maps these semantics to:

- `Explicit` -> `Manual`
- `Inferred` -> `Suggested`
- `Mixed` -> `Hybrid`

The compact display shape is:

- `N region(s) · X.Ys · Provenance`

This is intentionally compact because it appears inside the same inspector row that already carries phrase, beat, snap, and marker state.

## Snap Priority Contract

`ComputePhraseAwareSnapEndSeconds(...)` is the canonical snapping rule for phrase-aware transition length edits.

Current priority order:

1. explicit region boundaries
2. mixed region boundaries
3. inferred region boundaries
4. phrase snap candidates
5. beat-guide fallback

This priority order exists so the system prefers the most intentional musical boundary first.

Interpretation:

- if the user made a boundary explicit, it wins
- if an explicit edit reshaped inferred material, the mixed remainder still outranks fully inferred spans
- phrase markers and cue-derived candidates remain useful, but they are not allowed to override stronger region evidence
- beat fallback is the safety net, not the primary phrasing authority

## Editing Contract

Phrase interaction in A9 now covers three editable surfaces:

### Phrase markers

- marker preview and commit are supported
- marker explicitness can be toggled
- marker edits are undoable

### Phrase regions

- explicit region creation is supported per transition
- explicit region removal is supported
- start and end handles are draggable
- region boundary edits are undoable

### Transition lengths

- transition resize remains phrase-aware and beat-aware
- phrase-region boundaries now participate directly in snapping decisions

## What The System Does Not Guarantee Yet

The subsystem is structurally complete, but some behavior is still a tuning surface rather than a locked product decision.

Examples:

- exact drag deadzone feel for phrase-region handle edits
- exact snap threshold feel at different zoom levels
- final DJ-facing wording for provenance labels
- visual density of inferred regions in cue-heavy transitions

These are safe tuning targets because they sit on top of a tested structural model.

## Safe Next Changes

Changes that are low-risk now:

- provenance UX copy improvements
- drag deadzone tuning
- snap threshold tuning
- opacity and emphasis tuning for inferred vs mixed vs explicit regions
- targeted regression tests for wording and interaction thresholds

Changes that should not happen casually:

- changing the five-stage merge pipeline order
- collapsing `Mixed` back into `Inferred`
- letting phrase markers outrank explicit region boundaries
- treating explicit edits as destructive replacement of all inferred context

## Validation Baseline

The A9 phrasing contract is currently backed by focused tests covering:

- phrase candidate snapping
- beat fallback
- phrase marker override behavior
- inferred merge behavior
- explicit-over-inferred shaping
- mixed provenance behavior
- confidence recomputation
- explicit snap priority
- inspector summary labels

Post-A9 baseline:

- focused A9 flow validation green
- full suite green: `756/756`

## Practical Rule For Future Work

When extending Flow phrasing behavior, treat this subsystem in the following order:

1. preserve musical priority
2. preserve explicit user intent
3. preserve traceable provenance
4. preserve inspector truthfulness
5. only then tune feel and wording

That order matches the current implementation and should remain the default decision rule for future A9-adjacent work.