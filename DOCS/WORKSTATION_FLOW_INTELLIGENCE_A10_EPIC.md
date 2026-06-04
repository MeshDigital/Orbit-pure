# EPIC: Workstation Flow Intelligence & Similarity Engine (A10)

Status: Active (parallel sidequest)
Owner: Orbit Intelligence + Workstation
Date: 2026-05-11

## Arc Boundary

A9 is complete and stable; A10 builds on top of it without changing its guarantees.

A10 is a new intelligence layer focused on similarity, segment awareness, and playlist optimization. It must be additive to current Workstation behavior.

## Goal

Deliver a Mixed-In-Key-grade similarity and playlist intelligence stack that can:

- compare tracks using multi-vector fingerprints
- compare tracks by segment role (intro/build/drop/breakdown/outro)
- optimize playlist order for coherence and energy shape
- insert candidate tracks between two anchors with explainable scoring
- improve harmonic intelligence beyond single-key compatibility
- expose drop map and waveform intelligence in Flow surfaces

## Non-Goals

- No regression of A9 phrase-region merge/provenance/snap behavior
- No replacement of existing A9 structures or docs
- No shell-level UI expansion that reintroduces panel clutter

## Current Milestone Snapshot

Completed backend foundation slices:

- A10.1 fingerprint schema + extraction scaffolding
- A10.2 harmonic enhancement engine
- A10.3 similarity service
- A10.4 playlist intelligence engine

Current state of the arc:

- fingerprints are persisted, versioned, and backward-compatible
- harmonic intelligence is deterministic and fail-open in the analysis pipeline
- similarity is now computed as whole-track score + segment-role score + blended final score
- playlist intelligence now supports anchored suggest-next, insert-between scoring, and reorder scoring
- explainability is now a first-class output of the intelligence stack, including playlist decision outputs
- the first A10.5 cockpit-visible UI surfaces are now live in existing FlowBuilder and bridge suggestion workflows
- Library inspector now shows a compact pairwise A10 snapshot row when a selected playlist track has a real adjacent neighbor context

Immediate consequence:

- A10 now has a real backend intelligence layer wired into the FlowBuilder suggest-next path and ready for broader Flow-facing recommendation surfaces
- A10 is no longer backend-only: the cockpit now exposes transition quality and bridge-fit reasoning from the shared A10 scoring stack

## A10 Inspiration & Competitive Analysis

External benchmark reference: Mixed In Key 11 Pro video analysis.

This benchmark is used to validate product direction and workflow fit, not to mirror another product's UI or naming.

Observed benchmark patterns relevant to Orbit:

- mashup mode demonstrates a future segment-aware mashup ideation surface
- DJ mix mode demonstrates a sequential playlist-building workflow anchored on the current deck/track
- segment-aware suggestions validate A10.3 role-based similarity output
- harmonic + BPM + energy blending validates the A10 multi-vector model
- cue-point-aware auditioning confirms the value of tight integration with existing A9 cue/timeline context
- exportable mashup pairs suggest a future pairing/output workflow beyond the current A10 slices

Orbit slice mapping:

- A10.2 harmonic intelligence: richer compatibility than primary-key-only matching
- A10.3 similarity core: segment-aware comparison and explainable fit scoring
- A10.4 playlist intelligence: anchored next-track suggestion loop, insert-between scoring, and reorder graph
- A10.5 UI integration: audition-driven recommendation surfaces inside existing Flow UI
- A10.7 future mashup idea engine: segment-aware mashup pairing and preview workflow

Benchmark boundaries:

- no change to A9 guarantees or provenance contracts
- no MIK-specific terminology in product surfaces
- no premature commitment to a mashup mode in A10.1-A10.6

## Intelligence Architecture

### 1. Multi-Vector Fingerprint Per Track

Each analyzed track receives a normalized fingerprint:

1. Harmonic vector
- primary Camelot key
- secondary key candidates + confidence
- modulation score
- key stability score

2. Energy vector
- global energy
- segment energy curve
- drop intensity
- breakdown depth

3. Rhythm vector
- BPM
- swing/groove score
- beat histogram signature
- percussive density

4. Timbre vector
- MFCC mean/std/percentiles
- spectral centroid profile
- brightness/warmth indicators

5. Structure vector
- phrase map density
- intro/build/drop/breakdown/outro boundaries
- intro/outro length
- build-up slope

6. Mood vector
- danceability
- aggressiveness
- acousticness
- tonal vs percussive balance

### 2. Similarity Function

Base formula:

S(trackA, trackB) =
  w_h * H +
  w_e * E +
  w_r * R +
  w_t * T +
  w_s * St +
  w_m * M

Initial global weights (sum = 1.00):

- Harmonic w_h = 0.24
- Energy w_e = 0.20
- Rhythm w_r = 0.18
- Timbre w_t = 0.14
- Structure w_s = 0.14
- Mood w_m = 0.10

Mode-specific weight profiles:

- Blend-safe profile: boost harmonic + structure
- Energy-drive profile: boost energy + rhythm
- Genre-cohesion profile: boost timbre + mood + structure

Implementation note:

- A10.3 now computes these profile variants through a pure backend similarity service over persisted fingerprints with confidence-aware weighting fallback

### 3. Segment-Level Similarity

Segments are modeled explicitly:

- intro
- build
- drop
- breakdown
- outro

For each segment role R:

SegSim_R(A,B) = weighted role-specific similarity score

Track-level segment similarity:

SegSim(A,B) = sum(roleWeight_R * SegSim_R(A,B))

Initial role weights:

- intro 0.15
- build 0.20
- drop 0.30
- breakdown 0.20
- outro 0.15

Final blended similarity for A10 engine:

FinalSim = alpha * WholeTrackSim + (1 - alpha) * SegSim

Initial alpha:

- alpha = 0.55

Implementation note:

- A10.3 currently blends persisted fingerprint similarity with section-role similarity from existing section vectors when available, falling back cleanly when section data is sparse

## Harmonic Intelligence Upgrade (Camelot+)

### Output Model

For each track:

- primary key + confidence
- secondary key candidates (top N, initial N=3)
- modulation score (intra-track key movement)
- key stability score (0..1)

### Pairwise Harmonic Compatibility

Compute HarmonicCompat(A,B) from:

- primary-primary distance
- primary-secondary nearest distance
- mode compatibility (major/minor side)
- stability penalty for volatile tracks
- modulation transition bonus/penalty

Harmonic compatibility feeds:

- harmonic vector similarity term
- insert-between ranking
- reorder edge weighting

## Playlist Intelligence Layer

Model playlist as weighted graph G(V,E):

- node V: track fingerprint + segment map
- edge E(A,B): directional compatibility from A -> B

Edge score:

Edge(A,B) =
  p1 * FinalSim(A,B) +
  p2 * HarmonicCompat(A,B) +
  p3 * TransitionFeasibility(A,B)

Initial p-weights:

- p1 = 0.50
- p2 = 0.30
- p3 = 0.20

### A. Maximum-Coherence Reorder (TSP-like heuristic)

Plan:

1. Build complete directed graph for active candidate set
2. Seed start node by user pin or best anchor
3. Construct path with nearest-neighbor on edge score
4. Apply 2-opt/3-opt local improvement iterations
5. Apply energy-profile constraint correction pass

Output:

- ordered list
- score deltas per transition
- explanation tags for major reorder moves

Workflow note:

- optimizer should support a sequential anchor loop where the currently accepted track becomes the next anchor for recommendation
- this enables a deck-A to suggest-next workflow alongside full-playlist reorder mode

Implementation note:

- A10.4 now ships as PlaylistIntelligenceService over TrackFingerprintStore, TrackSimilarityService, HarmonicCompatibilityService, and SectionVectorService
- the first production consumer is the existing FlowBuilder suggest-next path, which now anchors on the accepted tail track when one exists
- legacy PlaylistOptimizer remains available for existing non-A10 consumers and fallback behavior

### B. Insert Track Between A and B

For candidate X:

InsertScore(X|A,B) =

Implementation note:

- A10.4 now computes insert-between recommendations with shared similarity, harmonic, transition, and energy-fit reasoning rather than a section-only heuristic

## A10.4 Completion Notes

What is now true:

- anchored suggest-next is now driven by the A10 fingerprint, harmonic, similarity, and section-aware reasoning stack
- insert-between scoring is implemented as a real backend service surface, not just a planning concept
- reorder scoring exists as a deterministic greedy backend path with energy-curve shaping support
- playlist decisions now emit explainable recommendation outputs with score components and reason tags

Integration points:

- PlaylistIntelligenceService is registered in application DI
- FlowBuilderViewModel suggest-next now calls A10.4 when a current tail anchor exists
- A10.4 reuses TrackSimilarityService and HarmonicCompatibilityService instead of duplicating scoring logic
- A10.4 reuses SectionVectorService for transition feasibility and role-aware context

What did not change:

- no A9 guarantees changed
- no new UI surface was introduced in A10.4
- legacy optimizer behavior was not removed for existing non-A10 paths

What this unlocks next:

- A10.5 can surface reason tags, transition quality, and insert-between candidates in Flow UI without inventing a second scoring model
- A10.6 can focus on caching, large-playlist behavior, and golden-fixture regression coverage over a stable backend stack

## A10.5 Initial UI Notes

First live surfaces:

- FlowBuilder suggest-next status now surfaces the top A10 reason tag when a recommendation is accepted
- FlowBuilder transition bridges now expose compact A10 percentage breakdowns for similarity, harmonic fit, and energy fit
- FlowBuilder now exposes a non-destructive A10 suggested-flow banner with preview, average flow score, and apply/dismiss actions
- the bridge suggestion sidebar now ranks insert-between candidates through A10.4 when available and reuses the existing insert-confirm workflow
- bridge suggestion rows now expose A10 reason labels using the existing row presentation model
- SimilarTracks inspector rows now show an explicit compact A10 snapshot line (overall, harmonic, similarity, energy percentages) in both similar and bridge candidate modes
- Library track inspector now conditionally renders a compact A10 pair snapshot (overall/harmonic/beat/drop plus reason tags) when selection context provides a real adjacent track
- Player now-playing inspector now conditionally renders the same compact A10 pair snapshot when current queue context provides a real adjacent neighbor
- FlowBuilder transition bridge selection now opens track inspector context with the same compact pairwise A10 snapshot sourced from the selected adjacent transition pair (A->B)

Boundary preserved:

- no new shell surface or panel was introduced
- no second scoring model was created in the UI layer
- legacy fallback behavior remains available when A10 scoring inputs are unavailable

Next A10.5 target:

- complete regression sweep and interaction polish for inspector-open transition selection behavior before closing A10.5 and moving full focus to A10.6 hardening

## A10.6 Hardening — COMPLETE

All A10.6 hardening targets delivered and validated (30/30 tests green).

### TrackFingerprintStore — In-memory cache + concurrent reads
- Added `ConcurrentDictionary<string, TrackFingerprint> _memoryCache` — cache hits bypass disk entirely (no I/O, no lock)
- Write lock (`_writeLock SemaphoreSlim(1,1)`) now guards writes only; reads are lock-free (`FileShare.Read` is set, concurrent file reads are safe)
- `SaveAsync` updates memory cache after a successful disk write — next reader sees new fingerprint from cache
- New `Invalidate(hash)` method evicts a single entry after re-analysis
- New `InvalidateAll()` for bulk re-analysis passes

### TrackSimilarityService — Result cache for ScoreAsync
- Added `ConcurrentDictionary<(left, right, profile), TrackSimilarityResult> _resultCache` (cap 256 entries)
- `ScoreAsync` check cache first — inspector reopens for the same track pair return instantly from memory
- When cap reached, entire cache is cleared (simple, no partial-state risk)
- New `InvalidateResultCache(hash)` evicts all pairs involving a re-analysed track

### PlaylistIntelligenceService — Concurrent fingerprint loading + guardrails
- `LoadFingerprintLookupAsync` now uses `Task.WhenAll` — 64 fingerprints loaded in parallel instead of sequential foreach behind a global lock
- `ReorderAsync` throws `ArgumentException` if input exceeds `MaxReorderTracks` (512) — protects O(n²) greedy loop
- `ScorePathTransitionsAsync` throws `ArgumentException` if edges exceed `MaxPathEdges` (1024)

### SectionVectorService — Pre-existing invalidation confirmed
- `Invalidate(hash)` and `InvalidateAll()` were already present; confirmed compatible with A10.6 cache lifecycle

### New A10.6 test coverage (7 new tests — 30/30 total)
- `SuggestNextAsync_WithEmptyStore_ReturnsEmptyAndDoesNotThrow` — cold-start guard
- `FingerprintStore_MemoryCache_ServesRepeatGetWithoutDiskRead` — cache hit identity check
- `FingerprintStore_Invalidate_ForcesNextGetToRehit_Disk` — invalidation → new instance
- `ReorderAsync_OverLimit_ThrowsArgumentException` — input-size guardrail
- `LargeSetFixture_SuggestNextAsync_128Tracks_CompletesWithinBudget` — 128-track stress (< 3000 ms)
- `SimilarityResultCache_ReturnsIdenticalInstance_OnRepeatScore` — result cache identity
- `SimilarityResultCache_InvalidateResultCache_ClearsStaleEntries` — result cache invalidation

### Memory footprint note
- Fingerprint cache: session-scoped, ~2 KB/entry. A 10K-track library ≈ 20 MB — acceptable for a desktop DAW process.
- Section cache: unchanged (ConcurrentDictionary, unbounded per session).
- Result cache: capped at 256 entries.

## A10.7 Slice 1 — Transition-Style Classifier

First additive A10.7 surface delivered and validated (44/44 relevant tests green).

### New model surface
- `TransitionStyle` enum added with six deterministic labels: `SmoothBlend`, `EnergyLift`, `DropSwap`, `BreakdownReset`, `TensionBridge`, `RiskyClash`
- `TransitionStyleResult` added as a small immutable label + reason payload

### New pure classifier
- `TransitionStyleClassifier` added as a stateless, deterministic service
- consumes existing A10 pairwise substrate only: fingerprints, section vectors, and `TrackSimilarityResult`
- performs no I/O, no caching, and no async work

### FlowBuilder transition-selection inspector
- FlowBuilder now builds a full pairwise snapshot for the selected adjacent transition
- inspector row now shows `STYLE` + one-line explanation alongside the existing A10 snapshot
- no new inspector surface created; the existing pairwise row was extended in place

### Bridge panel
- bridge candidate rows now show a compact transition-style label inline with the existing reason tags
- each bridge candidate also gets a one-line classifier reason without changing the A10 percentages or insert flow

### Validation
- new `TransitionStyleClassifierTests` adds six deterministic tests, one per style
- targeted UI-adjacent validations stayed green: `FlowBuilderBridgeInsertionTests`, `SimilarTracksBridgeEventFlowTests`
- expanded validation sweep passed: `44/44`

## A10.7 FlowBuilder Follow-Ups

Two additive FlowBuilder-only follow-ups now sit on top of the classifier without changing A10 scoring.

### Transition-style filter
- FlowBuilder bridge rows can now be filtered by style label (`All`, `Smooth Blend`, `Energy Lift`, `Drop Swap`, `Breakdown Reset`, `Tension Bridge`, `Risky Clash`)
- filtering is presentation-only; bridge recommendation/scoring remains unchanged

### Suggested-flow storytelling summary
- the existing suggested-flow banner now adds a style-delta line for reorder proposals
- summary compares the current staged order to the proposed order and emits compact deltas such as `+3 smooth blends · -2 risky clashes · +1 energy lift`
- classifier reuse stays deterministic by building adjacent pair snapshots from the existing A10 substrate; no new caching or optimizer branch was introduced
- preview is now feature-flagged via FlowBuilder config, with deterministic local rollout bucketing per install
- opening the preview uses a read-only impact dialog that shows before/after style counts plus affected transition edges
- local-only activity logging now records `suggested_flow_shown`, `suggested_flow_applied`, `suggested_flow_dismissed`, and `suggested_flow_summary_click` through the existing playlist activity log path

### Validation
- `FlowBuilderBridgeInsertionTests` now covers style-delta formatting, impact aggregation, preview rollout gating, and impact-viewmodel shaping
- targeted follow-up sweep passed: `23/23`

Next focus: A10.7 Slice 2 creative surfaces on top of the classifier (transition archetypes, mashup ideas, or multi-track blend suggestions).
  q1 * FinalSim(A,X) +
  q2 * FinalSim(X,B) +
  q3 * HarmonicCompat(A,X) +
  q4 * HarmonicCompat(X,B) +
  q5 * EnergyProfileFit(A,X,B)

Initial q-weights:

- q1 = 0.25
- q2 = 0.25
- q3 = 0.20
- q4 = 0.20
- q5 = 0.10

Return top K candidates with reason codes.

Audition loop note:

- insert-between and next-track suggestions should be usable as an audition-driven workflow, not only as a batch ranking surface
- once a candidate is accepted, the system should be able to promote it to the new live anchor and recompute suggestions incrementally

### C. Energy-Curve Constraints

Supported profiles:

- Rising energy
- Wave energy
- User-defined keyframe profile

Constraint handling:

- hard constraints for disallowed jumps
- soft penalties for minor deviations
- post-optimization smoothing pass

## UI/UX Integration Plan

A10 must integrate into existing Workstation Flow surfaces, not create a new cockpit mode.

### Flow Inspector Additions

- Similarity confidence row (whole-track + segment)
- Harmonic intelligence row (primary/secondary/stability)
- Segment fit row (intro/build/drop/breakdown/outro mini scores)
- Insert-between recommendation panel (A->X->B)
- Energy profile selector (rising/wave/custom)
- Current-anchor recommendation row for suggest-next workflow

### Flow Lane/Overlay Additions

- Transition edge quality badges from A10 edge score
- Drop-map markers aligned with existing timeline context
- Segment-aware hint chips on selected transitions
- audition-aware recommendation hints aligned with existing cue/timeline context from A9

### Drawer Additions

- Reorder suggestions list with explainability tags
- Insert-between candidate list for selected pair
- Quick apply actions with undo integration
- sequential next-track candidate list for playlist-building loop

## Data + Pipeline Fit

A10 uses Essentia and existing analysis outputs as baseline.

Additive pipeline stages:

1. Feature extraction extension from current analysis artifacts
2. Fingerprint builder + persisted vector schema
3. Similarity service (whole-track + segment)
4. Harmonic enhancement service (secondary keys, stability, modulation)
5. Playlist optimizer service (reorder + insert-between + energy constraints)

Current implementation anchors:

- fingerprint persistence: TrackFingerprintStore
- harmonic scoring: HarmonicAnalysisService + HarmonicCompatibilityService
- similarity scoring: TrackSimilarityService
- section-aware inputs: existing SectionVectorService

## Proposed Implementation Slices

A10.1 Fingerprint schema + extraction scaffolding
- Status: complete
- Define fingerprint DTO/entity and persistence shape
- Populate from current analysis data where available

A10.2 Harmonic enhancement engine
- Status: complete
- Secondary keys, modulation, stability, pairwise harmonic score

A10.3 Similarity service (whole-track + segment)
- Status: complete
- Weighted blend and profile support
- Segment-role similarity outputs
- Explainable reason tags and Orbit-native result contract
- Hash-backed entry point over persisted fingerprints

A10.4 Playlist optimizer
- Status: next active slice
- Graph construction, reorder heuristic, insert-between scoring
- Energy-profile constrained rerank
- Sequential suggest-next loop and anchor promotion flow

A10.5 Flow UI integration
- Status: unlocked by A10.3 and A10.4
- Inspector + drawer + overlay surfaces using A10 scores
- Explainable labels and reason tags

A10.6 Validation + hardening
- Unit tests for scoring/weights/profiles
- Regression checks against A9 guarantees
- Performance checks for medium/large playlists

A10.7 Future mashup idea engine
- Segment-aware mashup pair suggestions
- Cue-point-aware audition and preview hooks
- Exportable pair/set concepts once the optimizer stack is mature

## Success Criteria

1. Similarity output is explainable and stable under reruns
2. Insert-between candidates feel musically sensible
3. Reorder output improves average transition quality score
4. Harmonic compatibility quality exceeds primary-key-only baseline
5. A9 phrasing guarantees remain unchanged
6. Similarity output remains deterministic and explainable for the same analyzed inputs

## Validation Strategy

- Deterministic tests for similarity math
- Golden test fixtures for insert-between behavior
- Property-based tests for score bounds and monotonicity
- Regression suite ensuring A9 docs/contracts still hold
- Runtime QA pass in Workstation Flow inspector and drawer

## Risks and Mitigations

1. Overfitting weights
- Mitigation: profile presets + offline benchmark corpus

2. Noisy feature quality on sparse metadata tracks
- Mitigation: confidence-aware weighting fallback

3. UX overload in inspector
- Mitigation: progressive disclosure and compact summaries

4. Performance cost on large playlists
- Mitigation: caching, incremental recompute, bounded candidate windows

## Governance Note

This epic is parallel and additive.

A9 docs remain the source of truth for phrase-region behavior:

- docs/memory/workstation_cockpit.md
- DOCS/WORKSTATION_COCKPIT_EPIC.md
- DOCS/workstation/flow-intelligence-design-note.md
