# ORBIT Connection + Search Hardening Plan (Soulseek.NET Deep Dive)

## 1) Purpose
This document defines an implementation plan to harden ORBIT’s Soulseek connectivity and search behavior using Soulseek.NET best practices and the findings from recent production disconnect/search-flood incidents.

Primary outcomes:
- Eliminate login-timeout/reconnect storm behavior.
- Reduce network flood risk during discovery/search bursts.
- Improve transfer reliability and queue fairness.
- Add observability so failures are diagnosable in minutes, not days.

---

## 2) Current-state diagnosis (what we learned)

### 2.1 Confirmed failure patterns
- Login handshake can fail on short wait windows (historically seen as 5s timeout symptom).
- Search lanes can generate bursty cascades under concurrent discovery, stressing server/session state.
- Reconnect loops can re-enter too aggressively after forced disconnect or kick.

### 2.2 Soulseek.NET facts relevant to ORBIT
- `minorVersion` is required and should uniquely identify client behavior.
- `SoulseekClientOptions.messageTimeout` is critical for handshake/message waits.
- `SoulseekClientOptions.maximumConcurrentSearches` already provides robust internal gating.
- `SearchOptions` supports strong server/response-side filtering:
  - `maximumPeerQueueLength`
  - `filterResponses`
  - `minimumResponseFileCount`
  - `removeSingleCharacterSearchTerms`
  - `fileFilter` / `responseFilter`
- `KickedFromServer` event exists and should drive reconnect cooldown.
- Transfer semantics include duplicate token/duplicate transfer protections, enqueue APIs, and stream-based downloads.

---

## 3) Design principles
- **Single source of truth for concurrency:** rely on library-level search semaphore first; app-level gates only where they serve a distinct purpose (lane orchestration, not duplicate throttling).
- **Fail soft, recover predictably:** cooldown/backoff on kicks and repeated failures.
- **Filter early:** reject bad candidates at `SearchOptions` and protocol level before expensive scoring/allocation.
- **Observable by default:** key counters/events for state transitions, queue pressure, and filter reasons.
- **Safe rollout:** introduce in phases with measurable guardrails and rollback paths.

---

## 4) Implementation roadmap (phased)

### Execution status snapshot (2026-03-19)
- ✅ **Phase A — Stabilization baseline:** Complete
- ✅ **Phase B — Connection manager refactor:** Complete (`B1` explicit lifecycle state machine via `ConnectionLifecycleService`; `B2` quick-retry cap + kick cooldown + jittered reconnect backoff; `B3` runtime reconfiguration via `ReconfigureOptionsAsync` for connect-timeout + listen-port without reconnect — scoped to what Soulseek.NET 9.1.0 exposes in `SoulseekClientOptionsPatch`)
- ✅ **Phase C — Search pipeline hardening:** Complete (`C1` strict-first cascade with short-circuit on high-confidence match; `C2` unified `SearchFilterPolicy` — single source of truth for bitrate/format/queue/excluded-phrase filtering; `C3` pressure-aware load shedding across Normal/Elevated/Critical levels)
- ✅ **Phase D — Transfer and queue reliability:** Complete (`D1` enqueue-first semantics via `TransferLifecyclePhase` callback — `RemoteQueued` vs `Transferring` states surfaced to UI; `D2` already satisfied by existing `.part` file streaming + atomic rename path; `D3` five-class retry taxonomy via `ClassifyTransferFailure` — `RemoteAccessDenied`, `RemoteQueueDenied`, `NetworkError`, `Timeout`, `PeerRejected` with per-class retry/hedge/delay policy)
- 🟨 **Phase E — Observability + diagnostics:** Mostly complete (reliability counters + adaptive lane live UI + rolling decision history; pressure-level logging active; correlation ID flow wired across discovery/status/progress and surfaced in live console; E3 diagnostics snapshot copy delivered; E1 transfer-outcome counters delivered in `NetworkHealthService` via `RecordTransferOutcome`/`GetTransferCounters`; targeted tests added for transfer classification and transfer metrics; E5 automated stress-style telemetry validation tests added (high-volume counters + history slice ordering + transfer aggregation); structured warn/error field normalization now applied in `ConnectionLifecycleService` + transfer-critical `DownloadManager` paths; `DownloadManager` DI cleanup completed (`ISoulseekAdapter` injection); remaining work is the live 30-minute telemetry stress run and completing log-field normalization sweep across lower-priority call sites)

## Phase A — Stabilization baseline (Complete / in progress)
### Delivered
- Hardened `SoulseekClientOptions` usage:
  - `messageTimeout`
  - `maximumConcurrentSearches`
  - `maximumConcurrentDownloads`
- Added kick-aware reconnect cooldown path.
- Added search-side `SearchOptions` filtering and bounded variation fan-out.

### Verify now
- 24h run with no reconnect storms.
- No login timeout loops under concurrent discovery load.

---

## Phase B — Connection manager refactor (1 sprint)

### B1. Centralized connection state machine
Create explicit state machine wrapper around adapter states:
- States: `Disconnected`, `Connecting`, `LoggingIn`, `LoggedIn`, `CoolingDown`, `Disconnecting`.
- Transitions guarded by one serialized command queue.
- Explicit transition reasons (manual, network error, kicked, timeout).

**Deliverables**
- `ConnectionLifecycleService` with deterministic transition table.
- Replace ad-hoc reconnect triggers from multiple call sites.

**Acceptance criteria**
- Never more than one active connect attempt.
- No connect call executes while state is `CoolingDown`.
- State transition logs include previous/current/reason/correlation id.

### B2. Reconnect policy hardening
- Keep existing exponential backoff.
- Add policy dimensions:
  - immediate retry cap (e.g., max 3 quick retries)
  - hard cooldown after kick (already present)
  - jitter for distributed clients

**Acceptance criteria**
- Reconnect attempt cadence remains bounded even under repeated faults.

### B3. Dynamic reconfiguration path
Use Soulseek.NET `ReconfigureOptionsAsync` where applicable for runtime tuning (timeouts, speed caps, listener settings), avoiding full client recycle where safe.

---

## Phase C — Search pipeline hardening (1–2 sprints)

### Phase C execution plan (detailed)

#### C.0 Scope and guardrails
- Keep user-facing search UX unchanged unless explicitly needed for reliability.
- Prefer config-driven knobs over hardcoded thresholds.
- Ensure every search expansion decision is observable (reason + counters).

#### C.1 Workstream: query strategy control
**Implementation tasks**
1. Enforce `MaxSearchVariations` at a single orchestration boundary.
2. Add strict-first cascade contract:
   - run strict variation first
   - run relaxed variation only when strict result quality/volume is below threshold
3. Add short-circuit rule when a high-confidence match is found early.

**Deliverables**
- Deterministic variation planner with explicit decision reasons.
- Config knobs for strict-result threshold and short-circuit confidence.

**Acceptance criteria**
- No query generates more than configured max variations.
- Relaxed variation is skipped when strict variation already satisfies quality threshold.

#### C.2 Workstream: unified filtering policy
**Implementation tasks**
1. Introduce `SearchFilterPolicy` as the only builder for `SearchOptions` + local fallback predicates.
2. Migrate all search entry points to consume that policy object.
3. Keep excluded-phrase and queue-pressure filtering centralized in this policy path.

**Deliverables**
- Single policy module for bitrate/format/queue/excluded-phrase decisions.
- Removal of duplicated filter branches across orchestration paths.

**Acceptance criteria**
- One source of truth for search filtering.
- Filter changes require edits in one location only.

#### C.3 Workstream: pressure-aware load shedding
**Implementation tasks**
1. Define pressure levels (`Normal`, `Elevated`, `Critical`) from active lanes + rejection counters.
2. Map each level to dynamic limits:
   - response/file caps
   - relaxed variation suppression
   - minimal inter-query pacing increase
3. Emit pressure-level transitions via diagnostics events.

**Deliverables**
- Runtime load-shedding policy with bounded degradation behavior.
- Evented state transitions for diagnostics panel and logs.

**Acceptance criteria**
- Under stress, fan-out and payload size reduce automatically.
- Recovery returns policy to baseline when pressure subsides.

#### C.4 Validation plan
- Unit tests:
  - strict-first and short-circuit behavior
  - policy-level filter inclusion/exclusion rules
  - pressure-level transitions and limit mapping
- Integration tests:
  - concurrent discovery load honors variation and pressure limits
  - quality outcomes remain acceptable after adaptive shedding

#### C.5 Exit criteria for Phase C
- Search fan-out is deterministic and bounded.
- Filtering logic is centralized and fully observable.
- Stress runs show lower rejection churn and stable completion latency.

### C1. Query strategy simplification
- Keep current normalization intelligence.
- Enforce bounded query variation count (`MaxSearchVariations`).
- Add adaptive policy:
  - strict query first
  - relaxed query only if strict returns under threshold
  - no further expansion if early high-confidence match found

### C2. Unified filtering policy object
Introduce `SearchFilterPolicy` to remove duplicated filter logic.
- Inputs: preferred formats, bitrate range, sample-rate ceiling, queue cap, excluded phrases.
- Outputs: `SearchOptions` and local fallback filters.

**Acceptance criteria**
- Filter logic lives in one place and is used by all search entry points.

### C3. Load shedding under pressure
When active discovery/search exceeds thresholds:
- Reduce response/file limits dynamically.
- Increase per-query delay minimally.
- Skip low-value relaxed variations.

---

## Phase D — Transfer and queue reliability (1 sprint)

### D1. Enqueue-first transfer flow
Adopt enqueue-first semantics where possible for better UX/telemetry:
- distinguish `queued remotely` vs `transferring`
- better cancellation and retry intent

### D2. Streamed download standardization
Standardize on stream-based downloads for large files to reduce memory spikes and partial-failure risk.

### D3. Retry taxonomy
Use error-class based retry matrix:
- retry: transient network timeout, remote queue denial with backoff
- no retry: auth rejection, persistent banned phrase policy failures

---

## Phase E — Observability + diagnostics (parallel track)

### Phase E execution plan (detailed)

#### E.0 Scope and telemetry contract
- Every adaptive decision must have a human-readable reason.
- Metrics must support both realtime UI insight and log-based incident review.
- Keep diagnostics lightweight and non-blocking on UI thread.

#### E.1 Workstream: metrics coverage completion
**Implementation tasks**
1. Finalize counter set for:
   - connection attempts/success/failure by reason
   - kick and cooldown activations
   - search started/completed/cancelled
   - filter/rejection reasons by category
   - transfer outcomes by terminal state
2. Standardize label dimensions (`reason`, `tier`, `pressureLevel`, `peerClass`).

**Deliverables**
- Stable metrics schema + naming contract.
- One diagnostics service surface for counters/snapshots.

**Acceptance criteria**
- No critical transition path lacks a metric.
- Metric labels remain bounded (no high-cardinality explosions).

#### E.2 Workstream: correlation and traceability
**Implementation tasks**
1. Generate operation correlation ID at track/discovery start.
2. Propagate correlation through search variation and transfer lifecycle events.
3. Include correlation IDs in structured logs and key diagnostics events.

**Deliverables**
- End-to-end trace path: track → discovery → search → transfer.

**Acceptance criteria**
- Any failed transfer can be traced back to originating search decisions in one query path.

#### E.3 Workstream: diagnostics UI hardening
**Implementation tasks**
1. Keep current adaptive lane status readout and rolling history (last 10 decisions).
2. Add compact health summary card (current pressure level, cooldown active flag, recent rejection mix).
3. Add operator actions:
   - clear local diagnostics feed
   - copy diagnostics snapshot to clipboard/log bundle

**Deliverables**
- Settings diagnostics pane with realtime summary + short history context.

**Acceptance criteria**
- Operator can identify why lanes changed without opening raw logs.
- Diagnostics render remains responsive during stress runs.

#### E.4 Structured logging standardization
**Implementation tasks**
1. Normalize log fields: `state`, `reason`, `attempt`, `cooldownUntil`, `queryHash`, `tier`, `peer`, `token`, `correlationId`.
2. Ensure all warnings/errors include at least `reason` + `correlationId` when available.

**Deliverables**
- Consistent log schema for troubleshooting and postmortem analysis.

**Acceptance criteria**
- Incident review no longer depends on ad-hoc message text parsing.

#### E.5 Validation and operational readiness
- Add telemetry-focused tests:
  - event emission on adaptive lane transitions
  - bounded history behavior (size cap + ordering)
  - correlation ID propagation across search/transfer events
- Run 30-minute stress pass and confirm:
  - no missing critical metrics
  - diagnostics UI remains stable
  - lane decision reasons stay understandable

#### E.6 Exit criteria for Phase E
- Critical paths are measurable and traceable end-to-end.
- Operators can diagnose search/connection pressure from diagnostics UI + logs in minutes.
- Telemetry schema is stable enough for long-term dashboards/alerts.

### E1. Required metrics
Emit counters/timers for:
- connection attempts/success/failure by reason
- kicks and cooldown activations
- search requests started/completed/cancelled
- response/file rejection reasons (format, bitrate, queue, excluded phrase, safety)
- transfer outcomes by state (`Succeeded`, `Rejected`, `TimedOut`, `Errored`, `Cancelled`)

### E2. Correlation ids
Attach operation IDs across:
- playlist track
- discovery tier
- search query variation
- transfer token

### E3. Structured logs
Standardize key log fields:
- `state`, `reason`, `attempt`, `cooldownUntil`, `queryHash`, `tier`, `peer`, `token`

---

## 5) Security and compliance hardening
- Preserve strict handling for server-sent excluded phrases.
- Keep search query hardening before dispatch.
- Add explicit audit event when a query/path is blocked by excluded phrase policy.
- Add optional “safe mode” profile that narrows response/file limits and disables aggressive tiers.

---

## 6) Test strategy

## Unit tests
1. Kick event triggers cooldown and blocks reconnect until expiry.
2. Variation cap truncates generated list deterministically.
3. `SearchOptions.fileFilter` rejects by each criterion independently.
4. Excluded phrase query/path block path works as expected.

## Integration tests
1. Concurrent discovery load does not exceed effective search concurrency limits.
2. Reconnect behavior under forced disconnect + kick simulation.
3. Transfer resume and cancellation semantics with queued remote state.

## Non-functional tests
- 30-minute stress run (multi-lane discovery bursts).
- Memory profile during stream downloads.

---

## 7) Rollout and rollback

## Rollout
- Feature-flag risky behavior changes:
  - adaptive load shedding
  - dynamic reconfigure path
- Deploy in stages:
  1) baseline hardening
  2) observability validation
  3) adaptive controls

## Rollback
- Runtime switch to conservative profile:
  - `MaxSearchVariations=1`
  - lower `SearchResponseLimit`
  - fixed reconnect delay
- Fallback to stable previous adapter options preset.

---

## 8) Priority backlog (next actionable items)

## Phases A–D — Completed ✅
All planned work through Phase D has been delivered and merged to `master`.
- Commits: `6f0023d` (B1/B2), `e12536a` (B3), `80e2353` (D1/D3), `59b6f05` (D3 classification tests), `9ad60dd` (queued-state UX polish)
- Test suite: 88/88 passing, 0 build errors

## P0 — Phase E remaining work
1. ✅ **E1 transfer metrics coverage** — transfer terminal outcomes now tracked in `NetworkHealthService` (`Succeeded`, `RemoteQueueDenied`, `RemoteAccessDenied`, `NetworkError`, `Timeout`, `PeerRejected`, `Cancelled`, `OtherFailure`) and reset with diagnostics.
2. ✅ **Transfer classification unit tests** — all 5 `ClassifyTransferFailure` branches covered (+ priority-ordering test and `ShouldAutoRetry` cross-check).
3. ✅ **`UnifiedTrackViewModel` Queued state enrichment** — "Waiting in peer queue…" status, distinct queued color, queued included in `IsActive`, and `IsRemoteQueued` helper exposed.

## P1 — Phase E stretch goals
1. 🟨 E5 telemetry stress validation — automated stress-style tests delivered (`NetworkHealthServiceTests`: high-volume recording, bounded/ordered history slice, exact transfer aggregation). Remaining: live 30-minute run confirming diagnostics UI behavior under sustained runtime load.
2. 🟨 Structured log field normalization across all warn/error sites (`reason` + `correlationId` at minimum). Status: transfer-critical `DownloadManager` + `ConnectionLifecycleService` covered; broad sweep for remaining low-priority sites pending.
3. ✅ DownloadManager DI cleanup — `ISoulseekAdapter` now injected instead of concrete `SoulseekAdapter` for full DI compliance.

---

## 9) Definition of done
This initiative is complete when all are true:
- No reconnect storms observed in stress/integration runs.
- Kick cooldown and backoff behavior are deterministic and tested.
- Search fan-out and filtering are bounded and observable.
- Transfer reliability metrics show improved success/timeout ratio.
- Playbook exists for tuning/rollback without code changes.

---

## 10) Operational tuning defaults (recommended)
- `ConnectTimeout`: 60_000 ms
- `messageTimeout`: max(15_000, `ConnectTimeout`)
- `MaxConcurrentSearches` (library): 2–3
- `MaxSearchVariations`: 2
- `SearchResponseLimit`: 100
- `SearchFileLimit`: 100
- `MaxPeerQueueLength`: 50 (tune lower for speed profiles)
- Kick cooldown: 60s

These defaults prioritize reliability under mixed home-network conditions while keeping discovery speed acceptable.