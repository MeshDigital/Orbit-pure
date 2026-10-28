# ORBIT Heuristic Search Upgrade Plan

**Date:** March 21, 2026  
**Status:** In Progress  
**Owner:** Search / Discovery stack

---

## Goal

Upgrade ORBIT's Soulseek search engine from a plain cascade search into a metadata-driven, heuristic librarian that:

- sanitizes and plans queries before hitting the network,
- escalates to broader searches only when needed,
- filters broad-result floods off the UI thread,
- ranks by both match quality and downloadability,
- short-circuits when a truly ideal file appears.

This plan is designed for ORBIT's long-running Avalonia desktop architecture, not a one-shot CLI flow.

---

## Current Foundation

The repo already contains useful building blocks:

- Query variation generation in [Services/SearchNormalizationService.cs](Services/SearchNormalizationService.cs)
- Sequential search cascade in [Services/SearchOrchestrationService.cs](Services/SearchOrchestrationService.cs)
- Background result streaming in [Services/SoulseekAdapter.cs](Services/SoulseekAdapter.cs)
- Candidate matching in [Services/SearchResultMatcher.cs](Services/SearchResultMatcher.cs)
- Queue/reliability heuristics in [Services/DownloadDiscoveryService.cs](Services/DownloadDiscoveryService.cs)
- Global ranking in [Services/ResultSorter.cs](Services/ResultSorter.cs)
- Rx support already referenced in [SLSKDONET.csproj](SLSKDONET.csproj)

The main gaps are:

1. no explicit metadata-first query plan object,
2. no true desperate-mode state machine,
3. matcher still mixes hard gates and soft scoring loosely,
4. broad-result filtering is not fully isolated from UI pressure,
5. ranking logic is split between matcher, sorter, and discovery code.

---

## Implementation Phases

### Phase 1 — Ingestion and Query Optimizer
**Targets:** `Services/InputParsers/`, `Services/SearchNormalizationService.cs`

Create a thin planning layer that turns raw input into a stable metadata contract.

#### Deliverables
- Add a `TargetMetadata` model with:
  - `Artist`
  - `Title`
  - `Album`
  - `DurationSeconds`
  - `PreferredFormats`
  - `MinimumBitrate`
- Add a `SearchPlan` model with ordered query lanes:
  - `Strict`
  - `Standard`
  - `Desperate`
- Build those plans from existing `SearchQuery` / `PlaylistTrack` data instead of replacing those models.

#### Rules
- Never fire every lane at once.
- Keep sequential dispatch to avoid server flood / ban pressure.
- Preserve musical identity such as `Original Mix`, `VIP`, `Remix`, and featured artists when appropriate.

---

### Phase 2 — Strict Local Filtering
**Targets:** `Services/SearchResultMatcher.cs`

Turn the matcher into a true gate-and-score component.

#### Deliverables
- Hard reject candidates whose duration differs by more than $\pm 3s$.
- Add tier-aware format handling:
  - strict lossless tiers reject lossy files,
  - MP3 fallback tiers allow lossy files.
- Keep artist/title/path matching as a soft score, not the primary gate.
- Improve diagnostic rejection reasons so discovery logs stay actionable.

#### Current Sprint
- This is the first implementation sprint.
- Initial work begins here because it de-risks every later phase.

---

### Phase 3 — Desperate Mode State Machine
**Targets:** `Services/SearchOrchestrationService.cs`

Add explicit query-lane transitions.

#### Deliverables
- Start with `Strict` / `Standard` query.
- Wait for configured search window.
- If no accepted results appear, escalate to `Desperate` query.
- Keep pressure-aware pacing between lane transitions.

#### Rules
- Desperate mode must be opt-in by state transition, not default behavior.
- Result acceptance must still flow through the strict matcher.

---

### Phase 4 — Headless Flood Control for Broad Searches
**Targets:** `Services/SearchOrchestrationService.cs`, `Services/SoulseekAdapter.cs`

Protect the UI from desperate-query result floods.

#### Deliverables
- Route desperate-mode results through a bounded background channel.
- Filter and score in the background before surfacing anything to Avalonia.
- Replace weak artist/title dedup with stable result fingerprinting.

#### Rules
- Never push raw desperate-query floods directly into UI-bound collections.
- Prefer bounded buffering over unbounded accumulation.

---

### Phase 5 — 30-Second Accumulator and Early Exit
**Targets:** `Services/SearchOrchestrationService.cs`, `Services/DownloadDiscoveryService.cs`

Add a stale-time accumulator around accepted candidates.

#### Deliverables
- Accumulate accepted candidates for up to 30 seconds.
- Rank over the full accepted set rather than first-hit wins.
- Short-circuit immediately for perfect candidates:
  - exact or near-exact duration,
  - desired format / bitrate,
  - queue length of 0 or free slot,
  - strong metadata confidence.

#### Tech Note
- Use Rx for timer / cutoff orchestration.
- Avoid ad-hoc `Task.Delay` state tangles where observables are a better fit.

---

### Phase 6 — Ranking Unification
**Targets:** `Services/ResultSorter.cs`, `Services/DownloadDiscoveryService.cs`

Consolidate ranking so interactive search and automated discovery use the same ordering logic.

#### Deliverables
- Primary ranking factors:
  1. queue length / availability,
  2. bitrate / format quality,
  3. peer reliability,
  4. upload speed,
  5. metadata confidence.
- Move discovery-specific bonuses and penalties into shared ranking utilities where possible.

---

## Delivery Order

### Sprint A — Matcher Hardening
- strict duration gate,
- tier-aware lossy fallback support,
- new tests for strict reject and fallback accept behavior.

### Sprint B — Metadata Query Planning
- `TargetMetadata`,
- `SearchPlan`,
- better strict / standard / desperate query generation.

### Sprint C — Orchestrator State Machine
- desperate mode escalation,
- pressure-aware sequencing,
- hidden flood-control lane.

### Sprint D — Accumulator + Unified Ranking
- 30-second stale-time collector,
- early exit for perfect winners,
- ranking logic consolidation.

### Sprint E — UI / Telemetry Polish
- richer status messages,
- clearer rejection diagnostics,
- visibility into search lane transitions.

---

## Risks and Constraints

- **Soulseek flood pressure:** no parallel broad query bursts.
- **Avalonia UI pressure:** never let large raw result sets hit UI collections directly.
- **Memory safety:** prefer bounded channels and filtered accumulation.
- **Backward compatibility:** integrate through existing `PlaylistTrack`, `SearchQuery`, and `Track` models where possible.

---

## Definition of Done

The upgrade is complete when ORBIT can:

1. plan strict-to-desperate searches from metadata,
2. reject duration-mismatched junk locally,
3. keep broad-result floods off the UI thread,
4. rank candidates by match quality and immediate downloadability,
5. fast-exit on ideal candidates without waiting the full stale window.

---

## Immediate Next Step

Begin with **Sprint A: Matcher Hardening** in [Services/SearchResultMatcher.cs](Services/SearchResultMatcher.cs), then expand its test coverage in [Tests/SLSKDONET.Tests/Services/SearchResultMatcherTests.cs](Tests/SLSKDONET.Tests/Services/SearchResultMatcherTests.cs).