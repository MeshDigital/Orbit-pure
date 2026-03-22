# Reactive Search Runtime Technical Guide (Mar 22, 2026)

## Purpose

This document explains the new search runtime systems added across ORBIT’s search, discovery, adapter, and UI layers.

It covers the production architecture now used to safely handle high-volume Soulseek result streams without UI rebuild storms, unbounded session growth, or opaque winner selection.

This is the implementation companion to the planning document in [DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md](DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md).

---

## Why this work exists

Soulseek search behaves like a broadcast-style firehose:

- peers reply asynchronously,
- replies can trickle long after the first visible result,
- broad queries can generate thousands of accepted candidates,
- UI-bound per-item updates become expensive fast,
- naive search cancellation often only stops presentation state, not the underlying stream.

ORBIT already had streaming primitives, but the new runtime hardening closes four specific failure modes:

1. **Session cancellation mismatch**
   - UI could stop looking busy while the network stream was still alive.
2. **Dispatcher pressure**
   - repeated grid rebuild behavior could make sustained inflow expensive.
3. **Pathological search growth**
   - accepted result volume needed an explicit circuit breaker.
4. **Low operator trust during long searches**
   - users needed explicit streaming state and rate/idle feedback.

---

## System map

The search stack is now split into distinct responsibility layers:

1. **Query planning and lane generation**
   - `SearchNormalizationService`
   - `SearchPlanningModels`

2. **Lane execution and winner production**
   - `SearchOrchestrationService`

3. **Network callback isolation and hard-cap protection**
   - `SoulseekAdapter`

4. **Discovery-specific candidate reasoning**
   - `DownloadDiscoveryService`

5. **Reactive UI ingestion and session lifecycle control**
   - `SearchViewModel`
   - `SearchPage.axaml`

6. **Explainability surfaces**
   - `SearchBlendReasonFormatter`
   - `SearchSelectionAudit`
   - `SearchResult`, `AnalyzedSearchResultViewModel`, discovery DTOs

---

## 1. Query planning: strict, standard, desperate

### New planning model

ORBIT now represents query planning explicitly with:

- `TargetMetadata`
- `SearchPlan`
- `SearchQueryLane`
- `PlannedSearchLane`

This turns query generation from “a bag of string variations” into an ordered intent model.

### Why this matters

Different search phases have different goals:

- **Strict**: preserve musical identity and match exact track intent.
- **Standard**: remove noisy suffixes and common packaging terms.
- **Desperate**: fall back to artist/title/album rescue mode when earlier lanes miss.

### Key behavior

`SearchNormalizationService` now:

- extracts structured target metadata from raw text, `SearchQuery`, or `PlaylistTrack`,
- preserves artist/title identity while stripping search noise for relaxed lanes,
- avoids low-signal artist stop words like “Various Artists” in desperate fallback,
- emits deduplicated lane order instead of uncontrolled query fan-out.

### Net effect

Search planning is now deterministic, metadata-aware, and easier to test.

---

## 2. Shared scoring model: fit + ranking blend

### New primitives

Two reusable scoring components were introduced:

- `SearchCandidateFitScorer`
- `SearchCandidateRankingPolicy`

### Fit scoring responsibilities

`SearchCandidateFitScorer` estimates how well a candidate fits the requested musical target using:

- artist/title token containment,
- album hints,
- duration proximity,
- queue/access convenience,
- lossless/format preference,
- bitrate floor compliance,
- format filter compatibility.

The output is a bounded score in the $0$–$100$ range.

### Final ranking responsibilities

`SearchCandidateRankingPolicy` combines:

- base match score,
- fit score,
- peer reliability,
- queue penalty,

into a single final ranking score.

The current blend is:

$$
\text{final} = 0.60 \cdot \text{match} + 0.40 \cdot \text{fit} + \text{reliability bonus} - \text{queue penalty}
$$

### Why this matters

Before this upgrade, some search and discovery paths applied similar ideas differently. Now the same vocabulary is shared across:

- orchestration,
- discovery selection,
- UI reasoning,
- audit telemetry,
- tests.

That makes winner selection more predictable and easier to explain.

---

## 3. Orchestration runtime: lane execution and bounded accumulation

`SearchOrchestrationService` now operates on a planned search model instead of only loose text variations.

### Core changes

#### Lane-aware execution

The orchestrator now understands whether it is running:

- `Strict`
- `Standard`
- `Desperate`

and can change behavior by lane.

#### Controlled escalation

Desperate fallback is no longer treated like just another generic retry.

It is now:

- delayed deliberately,
- skipped for non-album searches when earlier lanes already produced accepted results,
- buffered using a bounded channel so broad searches do not accumulate unbounded pressure.

#### Accumulator short-circuiting

For non-desperate lanes, the orchestrator can stop early when it sees a near-ideal candidate that already meets fast-lane quality/availability expectations.

This reduces unnecessary waiting while still keeping correctness-first behavior.

### Ranking output

After accumulation, candidates are ranked with the shared blend model and stamped with explainability telemetry:

- `BlendMatchScore`
- `BlendFitScore`
- `BlendReliability`
- `BlendFinalScore`

These values are attached to candidate metadata and copied into selection audit payloads.

---

## 4. Adapter hardening: callback isolation, serialized search safety, and hard caps

`SoulseekAdapter` is the network edge where Soulseek callback behavior is converted into ORBIT’s async-stream model.

### Serialized search correctness

The adapter now forces outbound search concurrency to `1` for correctness.

That change is deliberate. Under concurrent outbound searches, callback payloads can be interleaved in ways that make per-query reasoning unreliable. Until true callback isolation exists per active search ID, serialized dispatch is safer than speculative concurrency.

### Callback drain synchronization

The adapter now tracks:

- pending callbacks,
- dispatch completion,
- callback drain completion.

This prevents a search from being declared “complete” before the last accepted callback batch has actually been processed.

### Hard-cap circuit breaker

Two absolute safeguards now exist in `AppConfig`:

- `SearchHardResultCap`
- `SearchHardFileCap`

When the accepted result count or inbound file count exceeds those ceilings, the adapter now:

1. creates a `SearchLimitExceededException`,
2. publishes `SearchHardCapTriggeredEvent`,
3. cancels the linked search lifetime token,
4. completes the stream with the failure,
5. returns bounded final counts in logs/telemetry.

### Why this matters

The soft load-shedding knobs (`responseLimit`, `fileLimit`, token bucket pacing) are useful, but they are not enough for runaway broad queries. The hard cap is the final safety net.

---

## 5. Search view-model runtime: true sessions, buffered UI ingestion, and idle telemetry

`SearchViewModel` now owns an explicit search session lifecycle instead of relying on a method-local cancellation token.

### Session ownership model

Each active search session now has:

- `_activeSearchCts`
- `_currentSearchSessionId`
- `_searchSubscription`
- `_searchIdleMonitor`
- `IsListening`

### Why `SerialDisposable` is used

Only one live stream subscription should own the UI ingestion path at a time. `SerialDisposable` guarantees that replacing an active subscription disposes the previous one immediately and centrally.

### Reactive ingestion pipeline

Incoming `Track` items are pushed into a subject and then processed as follows:

1. background scheduler boundary,
2. map `Track` → `SearchResult` → `AnalyzedSearchResultViewModel`,
3. buffer by time-or-count (`250ms` or `50` items),
4. marshal batches to the UI scheduler,
5. append via `_searchResults.AddRange(batch)`.

This avoids per-item UI churn and keeps projection work off the dispatcher.

### DynamicData binding model

The visible grid is now bound through DynamicData rather than rebuilt manually.

Important consequences:

- no more `Clear()` + full re-add for every filter update,
- filter visibility and hidden reasons stay attached to the same row view-models,
- selection stability is improved under sustained inflow,
- the grid receives chunked incremental updates instead of rebuild storms.

### Telemetry now exposed

The view-model now tracks:

- `ResultsPerSecond`
- `TotalResultsReceived`
- `LastResultAtUtc`
- `CurrentSearchSessionId`

User-facing state now differentiates between:

- active streaming,
- explicit stop-listening cancellation,
- idle stream after arrivals settle,
- hard-cap truncation.

---

## 6. A subtle production bug that the new tests exposed

One of the important outcomes of the new regression work was discovery of a real completion-order bug.

### The bug

On normal search completion, the final buffered batch could be dropped if session cleanup disposed the UI ingestion subscription before the Rx pipeline finished draining.

### The fix

`SearchViewModel` now waits for the stream-drain completion task before final session cleanup on the successful completion path.

### Why this matters

Without that fix, small searches could intermittently show too few final results even though the network/orchestration layers had already emitted them.

---

## 7. UI changes: explicit stop-listening semantics

The search page now includes an explicit **STOP LISTENING** control.

This is not a cosmetic pause button.

It means:

- cancel the active session token,
- dispose the active stream subscription,
- stop further result arrivals for the current search,
- keep already visible results intact.

That gives the operator a reliable “I have enough results, stop the firehose” action.

---

## 8. Explainability upgrades across the stack

The new runtime work did not stop at performance and cancellation. It also strengthened explainability.

### New human-readable reasoning path

`SearchBlendReasonFormatter` produces compact labels like:

- `strong fit • trusted peer • score 95`

These are now preferred over raw internal score dumps when available.

### Preferred reason propagation

Preferred-reason behavior now flows consistently through:

- `SearchResult`
- `AnalyzedSearchResultViewModel`
- discovery DTOs
- download persistence reasoning

That means search rows, discovery cards, and persisted download reasoning all speak the same language.

---

## 9. Validation strategy and regression coverage

The runtime upgrades are protected by focused tests across multiple layers.

### Query planning and ranking

- `SearchNormalizationServiceTests`
- `SearchCandidateFitScorerTests`
- `SearchCandidateRankingPolicyTests`
- `SearchBlendReasonFormatterTests`

### Orchestration behavior

- variation cap behavior,
- desperate-lane escalation,
- desperate-lane skip when early lanes succeed,
- duration-aware winner preference,
- fast-clearance protection,
- hard-cap exception propagation.

### Search UI runtime behavior

`SearchViewModelTests` now cover:

- cancel stopping further visible additions,
- batched UI updates,
- idle telemetry behavior after stream completion.

### Why these tests matter

The most important reactive bugs in this area are timing/order bugs, not syntax bugs. Focused behavior tests are the only reliable way to catch them.

---

## 10. Operational expectations after this upgrade

With the new systems in place, the search runtime should now exhibit the following properties:

### Under broad or popular searches

- the app remains responsive,
- UI updates arrive in chunks,
- accepted-result growth is bounded,
- users can stop listening explicitly.

### Under long-tail trickle responses

- telemetry shows whether results are still actively arriving,
- idle state becomes visible,
- results remain usable without forcing a restart or full refresh.

### Under adverse network behavior

- the adapter prevents unbounded accepted growth,
- late callback completion is handled safely,
- hard-cap termination is explicit rather than silent.

---

## 11. Relationship to adjacent systems

These search runtime upgrades complement, but do not replace, adjacent hardening already present in the repo:

- token-bucket load shedding,
- search lane orchestration,
- discovery ranking and fit scoring,
- download-side fallback validation,
- queue-system investigations and bulk UI event batching.

Together they move ORBIT toward a more workstation-style model:

- bounded inflow,
- deterministic ranking,
- explicit operator controls,
- auditable decision reasons.

---

## 12. Key files

### Planning and documentation

- `DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md`
- `DOCS/REACTIVE_SEARCH_RUNTIME_TECHNICAL_2026-03-22.md`

### Runtime implementation

- `ViewModels/SearchViewModel.cs`
- `Views/Avalonia/SearchPage.axaml`
- `Services/SearchNormalizationService.cs`
- `Services/InputParsers/SearchPlanningModels.cs`
- `Services/SearchOrchestrationService.cs`
- `Services/SoulseekAdapter.cs`
- `Services/SearchCandidateFitScorer.cs`
- `Services/SearchCandidateRankingPolicy.cs`
- `Services/SearchBlendReasonFormatter.cs`

### Explainability and DTO surfaces

- `Models/SearchSelectionAudit.cs`
- `ViewModels/SearchResult.cs`
- `ViewModels/AnalyzedSearchResultViewModel.cs`
- `Models/Discovery/DiscoveryDtos.cs`

### Tests

- `Tests/SLSKDONET.Tests/ViewModels/SearchViewModelTests.cs`
- `Tests/SLSKDONET.Tests/Services/SearchOrchestrationServiceTests.cs`
- `Tests/SLSKDONET.Tests/Services/SearchNormalizationServiceTests.cs`
- `Tests/SLSKDONET.Tests/Services/SearchCandidateFitScorerTests.cs`
- `Tests/SLSKDONET.Tests/Services/SearchCandidateRankingPolicyTests.cs`
- `Tests/SLSKDONET.Tests/Services/SearchBlendReasonFormatterTests.cs`

---

## Summary

The new ORBIT search runtime is no longer just “async search plus a grid.”

It is now a layered system with:

- explicit query planning,
- shared ranking semantics,
- bounded adapter streaming,
- real session ownership,
- buffered incremental UI ingestion,
- explainable reasoning surfaces,
- targeted regression coverage.

That is the core technical foundation for handling Soulseek’s firehose-style search behavior safely in a desktop workstation UI.