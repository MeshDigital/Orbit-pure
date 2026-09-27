# Search Stream Firehose Hardening Plan (Mar 22, 2026)

## Scope

This plan validates external feedback against ORBIT’s current architecture and defines **phased, low-risk upgrades** for handling Soulseek broadcast-style result streams.

Goal: eliminate UI lockups and memory spikes under high-volume search streams while preserving existing ranking/discovery behavior.

---

## Current Reality Check (What ORBIT already does)

### Already in place (good)

1. **Streaming architecture exists**
   - `SearchViewModel` consumes `SearchOrchestrationService.SearchAsync(...)` via `await foreach` (not naive direct event binding).
   - `SoulseekAdapter.StreamResultsAsync(...)` already bridges callback-based Soulseek responses into a channel-based stream.

2. **Adapter-side result shaping exists**
   - Upstream filtering and dedup happen in `SoulseekAdapter.SearchCoreAsync(...)`.
   - `SearchOptions` enforces `responseLimit` and `fileLimit` from load-shedding policy/config.

3. **Batching to UI exists (partially)**
   - `SearchViewModel` batches stream items manually (`250ms` or `50` items) before `_searchResults.AddRange(...)`.

4. **Search orchestration buffering exists**
   - `SearchOrchestrationService` already buffers and ranks candidates per lane.

### Gaps (must fix)

1. **Cancel/Freeze is not wired end-to-end**
   - `ExecuteUnifiedSearchAsync` creates a local `CancellationTokenSource`.
   - `ExecuteCancelSearch` does not cancel that active token; it only flips UI flags.
   - UX has no explicit “Listening/Freeze” control despite streaming behavior.

2. **UI redraw path is still expensive**
   - `SyncSearchResultsView()` rebuilds `SearchResultsView` by clearing and re-adding all displayed results.
   - This can cause repeated full DataGrid refreshes under sustained inflow.

3. **Pre-UI heavy processing still happens on UI path**
   - Current flow batches on VM side but does not use an explicit background scheduler boundary for projection/filter prep.

4. **Hard fail-safe for pathological streams is missing**
   - While `responseLimit`/`fileLimit` exist, there is no explicit “kill switch” when accepted candidates exceed a hard cap for a search session.

5. **No stream rate telemetry for user trust**
   - Users cannot tell if search is still actively receiving responses or has effectively settled.

---

## Feedback Mapping (what to adopt / adapt)

### Feedback item: “Ditch direct event handlers”
- **Decision:** Adopt intent, adapt implementation.
- **Reason:** ORBIT already avoids direct UI event-add in search path, but still needs stronger reactive/session control and cheaper UI projection.

### Feedback item: “Use Rx buffer pipeline (250ms)”
- **Decision:** Adopt.
- **Reason:** Consistent with existing DynamicData/Rx usage in app and reduces dispatcher churn.

### Feedback item: “Freeze/Stop listening button”
- **Decision:** Adopt.
- **Reason:** Required for user control in endless/trickle streams; also fixes current ineffective cancellation behavior.

### Feedback item: “Inbound stream throttling + hard cap”
- **Decision:** Adopt with ORBIT-native knobs.
- **Reason:** Existing limits are helpful but not sufficient for worst-case popular queries.

### Feedback item: “Results-per-second telemetry”
- **Decision:** Adopt.
- **Reason:** Improves confidence and explains trickle behavior.

---

## Implementation Plan (Phased)

## Phase 1 — Search Session Lifecycle (Correctness)

### Changes
1. Add explicit active session state to `SearchViewModel`:
   - `_activeSearchCts`
   - `_activeSearchSubscription` (if Rx stream is used)
   - `IsListening`
   - `CurrentSearchSessionId` (diagnostics)

2. Replace local CTS in `ExecuteUnifiedSearchAsync` with session-scoped token.

3. Make `ExecuteCancelSearch` actually cancel the active session:
   - cancel token
   - dispose stream subscription
   - transition `IsListening=false`, `IsSearching=false`

4. Add a visible UI control in `Views/Avalonia/SearchPage.axaml`:
   - `Freeze Results` / `Stop Listening`
   - disabled when no active session

### Files
- `ViewModels/SearchViewModel.cs`
- `Views/Avalonia/SearchPage.axaml`

### Acceptance criteria
- Clicking Stop/Freeze stops further result arrivals in UI within ~250ms.
- No background result additions after cancellation.
- Search can be restarted cleanly after cancellation.

---

## Phase 2 — Dispatcher Protection + Projection Cost Reduction

### Changes
1. Introduce reactive stream buffering boundary in `SearchViewModel`:
   - Background scheduler for transformation/filter prep.
   - Buffer window `250ms` (configurable) and optional max batch size cap.

2. Remove full-collection rebuild behavior in `SyncSearchResultsView()`:
   - Avoid `Clear()` + re-add on every update.
   - Prefer DynamicData pipeline outputs bound directly to DataGrid.

3. Keep expensive operations off UI thread where possible:
   - view-model mapping
   - hidden reason precompute
   - any string normalization/derivation

### Files
- `ViewModels/SearchViewModel.cs`
- (Optional small XAML binding adjustments) `Views/Avalonia/SearchPage.axaml`

### Acceptance criteria
- Under bursty inflow, UI remains interactive (no visible hard lock/stall).
- DataGrid updates occur in chunks, not per-item redraw storms.
- Selection remains stable while stream active.

---

## Phase 3 — Adapter Hard Cap / Circuit Breaker

### Changes
1. Add app-configurable hard caps:
   - `SearchHardResultCap` (e.g., default 10_000)
   - `SearchHardFileCap` (optional)

2. Enforce in `SoulseekAdapter.SearchCoreAsync(...)`:
   - When accepted result count exceeds cap, end that search safely.
   - Prefer cancellation-token-driven termination for compatibility.
   - If Soulseek client exposes cancel-by-id reliably, use it; otherwise rely on local CTS stop.

3. Emit explicit warning log + status event for transparency.

### Files
- `Configuration/AppConfig.cs`
- `Services/SoulseekAdapter.cs`

### Acceptance criteria
- Hot queries cannot grow unbounded in memory.
- App remains connected and responsive during pathological search volume.
- Logs clearly indicate when cap-triggered shutdown occurred.

---

## Phase 4 — Stream Telemetry UX

### Changes
1. Add VM properties:
   - `ResultsPerSecond`
   - `TotalResultsReceived`
   - `LastResultAtUtc`

2. Compute RPS from batch arrivals (`batch.Count * 1000 / windowMs`).

3. Surface telemetry in UI status line:
   - `Found N files (streaming at X results/sec)`
   - if no arrivals for threshold window, show `Stream idle`.

### Files
- `ViewModels/SearchViewModel.cs`
- `Views/Avalonia/SearchPage.axaml`

### Acceptance criteria
- RPS updates while stream active and trends to zero when trickle ends.
- User can infer safe “selection moment” without guesswork.

---

## Phase 5 — Validation & Regression Guardrails

### Tests to add/update
1. `SearchViewModel` session cancellation test:
   - cancel prevents further result additions.

2. Buffering behavior test:
   - verifies batched updates (not per-item UI updates).

3. Adapter hard cap test:
   - simulated high-volume callback stream terminates at configured cap.

4. Telemetry test:
   - RPS computes correctly from timed batches.

### Suggested test files
- `Tests/SLSKDONET.Tests/ViewModels/SearchViewModelTests.cs` (new or extended)
- `Tests/SLSKDONET.Tests/Services/SoulseekAdapterTests.cs` (if mockable path exists)

---

## Risks & Mitigations

1. **Risk:** Over-buffering delays first visible results.
   - **Mitigation:** dual trigger (time window OR max batch size), e.g. 250ms or 50 items.

2. **Risk:** Freeze button may conflict with active download selection workflows.
   - **Mitigation:** freeze only halts inflow; keeps current visible list and selections intact.

3. **Risk:** Hard cap may hide good results in extremely broad searches.
   - **Mitigation:** expose cap in settings and log cap-hit reason clearly.

4. **Risk:** Large refactor to result projection could destabilize filtering.
   - **Mitigation:** implement Phase 2 incrementally and preserve current predicate semantics.

---

## Recommended Execution Order

1. Phase 1 (session/cancel correctness)
2. Phase 2 (UI projection efficiency)
3. Phase 3 (hard cap safety)
4. Phase 4 (telemetry UX)
5. Phase 5 (tests/regression)

This order gives immediate correctness and UX wins before deeper adapter protections.

---

## Out-of-scope for this plan (separate tracks)

- Discovery matching policy changes (already addressed in prior fixes).
- Download transfer exception normalization (`Transfer failed: Transfer complete`) — separate reliability hardening item.

---

## Commit/Push Policy for this request

Per request: this document is prepared for review first. **No commit/push should happen until approval is given.**
