# Download Center Architecture Overview

**Last Updated:** March 22, 2026  
**Scope:** ORBIT Pure Download Center (DownloadsPage) — Singleton container managing all active, completed, and failed downloads with real-time state synchronization.

---

## 1. High-Level Component Stack

```
┌─────────────────────────────────────────────────────────────────┐
│                    DownloadsPage.axaml (View)                   │
│                                                                   │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │         DownloadCenterViewModel (Singleton)                 ││
│  │                                                              ││
│  │  • Master collection lifecycle (add/remove/filter tracks)  ││
│  │  • Three swimlanes: Active | Completed | Failed             ││
│  │  • Download profile management (NonStrict/Strict/Stricter) ││
│  │  • Grouping & aggregation (Albums, Peer Lanes)             ││
│  │                                                              ││
│  │  ┌──────────────────────────────────────────────────────┐  ││
│  │  │ DynamicData SourceCache<UnifiedTrackViewModel>       │  ││
│  │  │ (Key: GlobalId — Guid per track lifetime)           │  ││
│  │  └──────────────────────────────────────────────────────┘  ││
│  │         ↓  (Dynamic filters)                                ││
│  │  ┌──────────────────────────────────────────────────────┐  ││
│  │  │ ReadOnlyObservableCollections:                       │  ││
│  │  │ • ActiveDownloads              (swimming)            │  ││
│  │  │ • CompletedDownloads           (history)             │  ││
│  │  │ • FailedDownloads              (terminal errors)     │  ││
│  │  │ • ExpressItems/StandardItems   (by priority)         │  ││
│  │  │ • OngoingDownloads/QueuedItems (state split)         │  ││
│  │  │ • ActiveGroups/ExpressGroups   (album aggregates)    │  ││
│  │  │ • ByPeerGroups                 (peer lanes)          │  ││
│  │  └──────────────────────────────────────────────────────┘  ││
│  └─────────────────────────────────────────────────────────────┘│
│                                                                   │
└─────────────────────────────────────────────────────────────────┘
         ↓                      ↓                      ↓
    ┌──────────┐          ┌──────────┐          ┌──────────┐
    │Standard  │          │Universal │          │Peer Lane │
    │Track Row │          │Track VM  │          │VM        │
    │Control   │          │          │          │          │
    └──────────┘          └──────────┘          └──────────┘
```

---

## 2. ViewModel Hierarchy & Roles

### **DownloadCenterViewModel** (1065 lines)
**Purpose:** Singleton orchestrator; manages global download state, collection lifecycle, profiles, and UI composition.

**Key Responsibilities:**
- **Collection Management:** Create, bind, track `UnifiedTrackViewModel` instances
- **State Filtering:** Dynamically filter tracks by state (Active → Completed/Failed)
- **Download Profiles:** Apply policy presets (NonStrict/Strict/Stricter) affecting search scope + quality gates
- **Grouping Logic:** Aggregate tracks by Album (DownloadGroupViewModel) or Source Peer (PeerLaneViewModel)
- **UI Batching:** Debounce collection updates via DispatcherTimer (250ms) to prevent redraw storms
- **Event Subscription:** Wire `IEventBus` listeners for download state changes, search completion, transfer progress

**Dependencies:**
- `DownloadManager` — Core download lifecycle (queue, execute, monitor)
- `IEventBus` — Application event pub/sub (StateChanged, SearchCompleted, etc.)
- `AppConfig` — Configuration knobs (search timeouts, profile presets, UI batch window)
- `DatabaseService` — Persist download history

**Key Collections (DynamicData-backed):**
| Collection | Filter | Purpose |
|-----------|--------|---------|
| `_downloadsSource` | N/A | Master SourceCache (in-memory, keyed by GlobalId) |
| `ActiveDownloads` | State ∈ {Pending, Searching, Downloading, Queued, Waiting, Stalled, Paused} | Visible downloads in progress |
| `CompletedDownloads` | State == Completed | History of successful downloads |
| `FailedDownloads` | State == Failed | Terminal failures eligible for retry |
| `ExpressItems` | Active ∧ Priority == Express | High-user-priority tracks |
| `StandardItems` | Active ∧ Priority == Standard | Normal-priority tracks |
| `BackgroundItems` | Active ∧ Priority == Background | Low-priority/batch downloads |
| `ActiveGroups` | Album-grouped from ActiveDownloads | Album-level aggregates |
| `ByPeerGroups` | PeerName-grouped from OngoingDownloads | Bottleneck visibility per source |

---

### **UnifiedTrackViewModel** (1941 lines)
**Purpose:** Per-track state machine; self-governing state machine for individual track lifecycle (search → download → completion/failure).

**Key Responsibilities:**
- **Search Orchestration:** Trigger search, manage search window (with lossless 20s hardening), countdown timer
- **Download Monitoring:** Monitor transfer state, speed, ETA, peer assignment
- **Result Accumulation:** Collect incoming peer matches (TrackPeerResultViewModel) with state tracking
- **Telemetry:** Rich diagnostics (search duration, countdown, match rate, peer rejection trace)
- **User Commands:** Execute removal, retry, force-download, play, reveal-in-explorer
- **Image Caching:** Async load album artwork via ArtworkCacheService

**Lifecycle States:**
```
Pending 
  ↓ (user initiates search)
Searching (with countdown: SearchMaxWindowSeconds - elapsed)
  ├─ Active search inflow from SoulseekAdapter
  ├─ Real-time countdown ticks every 1 second
  └─ Either → Downloading (match found) or → No Results (timeout)
  ↓
Downloading
  ├─ Monitoring transfer completion
  └─ Peer assignment (PeerName field updates)
  ↓
Completed OR Failed
  ├─ Terminal state (moved to history)
  └─ Eligible for Retry or permanent cleanup
```

**Key Properties:**
| Property | Type | Purpose |
|----------|------|---------|
| `SearchCountdownDisplay` | string | "XXs" format — real-time countdown to max window |
| `SearchMaxWindowSeconds` | int | Computed max search duration (lossless 20+s vs default 16+s) |
| `SearchDurationDisplay` | string | "h:mm:ss" or "m:ss" or "Xs" — actual elapsed time |
| `SearchOutcomeLabel` | string | "Searching" / "Match Found" / "No Results" |
| `SearchResultBreakdown` | string | "M matched, Q queued, F filtered" counts |
| `IncomingResults` | ObservableCollection<TrackPeerResultViewModel> | Per-result state log (Matched, Filtered, Queued, Rejected) |
| `LatestIncomingMessage` | string | Most recent peer message (e.g., "winner: Peer123") |
| `PeerName` | string? | Current download source (null if queued/waiting) |
| `DownloadSpeed` | long bytes/sec | Live transfer rate for aggregation |
| `State` | PlaylistTrackState | Enum { Pending, Searching, Downloading, …, Completed, Failed } |

**Recent Features (M4–M8):**
1. **Remove from Queue Button** (M4): Visible for 7 non-terminal states via `CanRemoveFromQueue` predicate
2. **Lossless Search Hardening** (M6): Detects lossless-only intent → enforces ≥20s search window
3. **Countdown Timer** (M8): 1-second Observable.Interval ticker displays `SearchCountdownDisplay` live in UI

---

### **DownloadGroupViewModel** (194 lines)
**Purpose:** Album-level aggregator; rolls up speed, progress, status from constituent tracks.

**Key Aggregates:**
- `TotalProgress` — Weighted average of track completion percentages
- `TotalSpeed` — Sum of all member track speeds
- `StatusText` — Rolled-up state ("3/5 downloading", "2/5 queued", etc.)
- `HasFailures` — True if any member track has Failed state

**Used By:** Album swimlane templates (grouped by Album ID)

---

### **PeerLaneViewModel** (77 lines)
**Purpose:** Peer-centric bottleneck dashboard; shows which single peer is serving multiple tracks.

**Key Insights:**
- `TotalSpeed` — Aggregated bandwidth from one peer across all assigned tracks
- `TrackCount` — How many concurrent tracks from this peer
- `LaneAccentColor` — Green (>2 MB/s, fast sole provider) → Amber (multi-peer shared) → Gray (slow)

**Use Case:** "I see Peer:FastUser serving 8/10 tracks at 2.5 MB/s" → visual bottleneck detection

---

## 3. Data Flow & Event Wiring

### **Inbound Event Path** (How tracks get added)

```
User triggers "Download Track" in Library/Search
     ↓
[External] → PlaylistTrack created, inserted into database
     ↓
IEventBus.Publish(NewDownloadQueuedEvent)
     ↓
DownloadCenterViewModel.OnDownloadQueued(event)
     ├─ Create UnifiedTrackViewModel wrapper
     ├─ Add to _downloadsSource (SourceCache)
     ├─ Trigger DynamicData pipeline (filtering)
     └─ ReadOnlyObservableCollections updated
     ↓
UI bindings (ActiveDownloads, ExpressItems, etc.) refresh
     ↓
DownloadsPage.axaml renders new row(s)
```

### **Search Lifecycle** (Within UnifiedTrackViewModel)

```
State: Pending
     ↓ (user clicks "Find"  OR automatic on queue)
EnsureSearchStarted()
  ├─ Set _searchStartedAtUtc = DateTime.UtcNow
  ├─ Call StartSearchClock() → Subscribe to Observable.Interval(1s)
  ├─ Invoke SearchOrchestrationService.StreamAndRankResultsAsync(...)
  └─ State → Searching
     ↓
Async search stream flows in
  ├─ Each result → TrackPeerResultViewModel added to IncomingResults
  ├─ Countdown ticker fires every 1s → SearchCountdownDisplay refreshed
  ├─ UI shows "Time Left: 15s" → "Time Left: 14s" → ...
  └─ Match found → DownloadManager queues transfer
     ↓
When match queues OR search window expires:
MarkSearchEnded()
  ├─ Set _searchEndedAtUtc = DateTime.UtcNow
  ├─ Call StopSearchClock() → Unsubscribe Observable
  ├─ Calculate final SearchDurationDisplay
  └─ State → Downloading (if match) OR Completed/Failed
```

### **Download Transfer Stream** (Managed by DownloadManager)

```
UnifiedTrackViewModel.State = Downloading
     ↓
DownloadManager monitors file transfer via Soulseek.NET callbacks
     ↓
Periodic transfer events:
  ├─ Update DownloadSpeed property (raises PropertyChanged)
  ├─ Update transfer percentage
  └─ Trigger PeerLaneViewModel aggregation
     ↓
Transfer completes
     ↓
IEventBus.Publish(DownloadCompletedEvent)
     ↓
DownloadCenterViewModel.OnDownloadCompleted(event)
  ├─ Move track to CompletedDownloads (state filter applies)
  ├─ Remove from ActiveDownloads
  └─ Update DownloadGroupViewModel aggregates
     ↓
UI reflects move (visual transition)
```

---

## 4. UI Structure (DownloadsPage.axaml)

### **Page Composition**

```xml
DownloadsPage
├─ Toolbar
│  ├─ Profile Chips (NonStrict / Strict / Stricter toggle)
│  ├─ Clear History button
│  └─ Settings link
│
├─ ACTIVE DOWNLOADS SECTION
│  ├─ Express Swimlane
│  │  └─ ItemsControl bound to ExpressItems
│  │     └─ StandardTrackRow control (per item)
│  │
│  ├─ Standard Swimlane
│  │  └─ ItemsControl bound to StandardItems
│  │     └─ StandardTrackRow control (per item)
│  │
│  └─ Background Swimlane
│     └─ ItemsControl bound to BackgroundItems
│        └─ StandardTrackRow control (per item)
│
├─ COMPLETED SECTION
│  └─ ItemsControl bound to CompletedDownloads
│     └─ Data template (minimal, history view)
│
├─ FAILED SECTION
│  └─ ItemsControl bound to FailedDownloads
│     └─ Failed card template (error summary + retry buttons)
│
└─ PEER LANES SECTION (Optional dashboard)
   └─ ItemsControl bound to ByPeerGroups
      └─ PeerLaneViewModel card (speed + track count)
```

### **StandardTrackRow Control** (Reusable row component)

**Renders per `UnifiedTrackViewModel`:**

```xml
StandardTrackRow
├─ Artwork (album thumbnail)
├─ Title & Artist
├─ State Badge (Searching | Downloading | Queued | etc.)
├─ Knowledge Bar (inline telemetry)
│  ├─ Duration: {SearchDurationDisplay}
│  ├─ Time Left: {SearchCountdownDisplay} ← Cyan, live-updating
│  ├─ Outcome: {SearchOutcomeLabel}
│  ├─ Results: {SearchResultBreakdown}
│  └─ Path: {SearchPathSummary}
├─ Progress Bar (% complete)
├─ Speed Indicator (MB/s or KB/s)
├─ Peer Lane (if downloading)
├─ Commands (Play, Reveal, Remove, Retry, Force, etc.)
└─ Expandable Log (collapsible IncomingResults list)
   └─ Per-result rows:
      ├─ Time
      ├─ Peer Name
      ├─ Match State label + color
      └─ Detail message
```

---

## 5. Services & External Dependencies

### **DownloadManager**
- **Role:** Core lifecycle orchestrator for download queue, transfer execution, state transitions
- **Integration:** UnifiedTrackViewModel calls into this for retry, force-download, cancellation
- **Events:** Publishes state-change events consumed by DownloadCenterViewModel

### **SearchOrchestrationService**
- **Role:** Async search stream generator; handles cascade timing, brain buffer, format filtering, lossless 20s hardening
- **Integration:** UnifiedTrackViewModel calls `StreamAndRankResultsAsync(...)` in search phase
- **Lossless Logic:** Detects lossless-only intent → enforces `Math.Max(brainBuffer, MinLosslessSearchDurationSeconds)`

### **SoulseekAdapter**
- **Role:** Soulseek.NET wrapper; converts callback-based search/transfer API into reactive streams
- **Integration:** SearchOrchestrationService uses `StreamResultsAsync(...)` for result flow
- **Rate Limiting:** Respects `responseLimit` / `fileLimit` from AppConfig

### **ArtworkCacheService**
- **Role:** Async album artwork fetching & caching
- **Integration:** UnifiedTrackViewModel.LoadArtworkAsync() (background task)

### **DatabaseService**
- **Role:** Persistence layer for download history, track metadata, search telemetry
- **Integration:** Periodic flush of completed/failed track records

### **IEventBus**
- **Role:** Application-wide pub/sub; decouples components
- **Events:** 
  - `TrackStateChangedEvent` (monitored by DownloadCenterViewModel)
  - `SearchCompletedEvent`
  - `DownloadCompletedEvent`
  - `DownloadFailedEvent`

### **AppConfig**
- **Role:** Runtime configuration
- **Used For:**
  - `MinLosslessSearchDurationSeconds` (default 20) — Lossless search floor
  - `MinSearchDurationSeconds` (default 16) — Base search window
  - `SearchTimeout` (12000ms) — Network await
  - Download profile presets (formats, bitrates, response limits)

---

## 6. Collection Staging Pipeline

### **DynamicData Processing Chain**

```
SourceCache<UnifiedTrackViewModel> (_downloadsSource)
│
├─ Filter(x => x.State in {Pending, Searching, …})
│  └─ ReadOnlyObservableCollection<> ActiveDownloads
│
├─ Filter(x => x.State == Completed)
│  └─ ReadOnlyObservableCollection<> CompletedDownloads
│
├─ Filter(x => x.State == Failed)
│  └─ ReadOnlyObservableCollection<> FailedDownloads
│
├─ Filter(x ∈ Active && Priority == Express)
│  └─ ReadOnlyObservableCollection<> ExpressItems
│
├─ Filter(x ∈ Active && Priority == Standard)
│  └─ ReadOnlyObservableCollection<> StandardItems
│
├─ Filter(x ∈ Active && Priority == Background)
│  └─ ReadOnlyObservableCollection<> BackgroundItems
│
├─ Group(x => x.AlbumId)
│  └─ ReadOnlyObservableCollection<DownloadGroupViewModel> ActiveGroups
│
└─ Filter & Group(x => x.PeerName)
   └─ ReadOnlyObservableCollection<PeerLaneViewModel> ByPeerGroups
```

**Key:** All derived collections are **live-binding** — changes in _downloadsSource cascade automatically.

---

## 7. Key Operations & Command Handlers

### **Download Profile Application**
```csharp
// User clicks "Strict" chip
DownloadCenterViewModel.ApplyDownloadProfile(DownloadProfile.Strict)
  ├─ Update config (formats, bitrates, response limits)
  ├─ Trigger DownloadManager policy update
  └─ All active searches respect new scope immediately
```

### **Remove from Queue**
```csharp
UnifiedTrackViewModel.ExecuteRemoveFromQueue()
  ├─ Check CanRemoveFromQueue (covers 7 non-terminal states)
  ├─ Call _downloadManager.RemoveTrackAsync(...)
  ├─ Listen for state → Removed/Deleted
  └─ DownloadCenterViewModel filters out (ActiveDownloads updated)
```

### **Retry Download (After Failure)**
```csharp
UnifiedTrackViewModel.ExecuteRetryCommand()
  ├─ Reset internal search state (_searchStartedAtUtc = null)
  ├─ Call DownloadManager.RetryTrack(...)
  ├─ Trigger new search via EnsureSearchStarted()
  └─ State: Failed → Pending → Searching → {Downloading | Failed}
```

### **Force Download** (Bypass quality gates)
```csharp
UnifiedTrackViewModel.ExecuteForceDownloadIgnoreGuards()
  ├─ Skip search orchestration
  ├─ Directly accept first candidate from IncomingResults
  └─ State: Failed → Downloading (immediately)
```

### **Countdown Timer Update** (Every 1 second)
```csharp
StartSearchClock()  // Called on EnsureSearchStarted()
  ├─ Subscribe to Observable.Interval(TimeSpan.FromSeconds(1))
  ├─ On each tick:
  │  ├─ Compute elapsed = DateTime.UtcNow - _searchStartedAtUtc
  │  ├─ Compute remaining = SearchMaxWindowSeconds - elapsed.TotalSeconds
  │  ├─ Set SearchCountdownDisplay = $"{remaining}s"
  │  └─ Raise PropertyChanged notification
  └─ UI one-way binding displays live countdown
```

---

## 8. State Machine Overview

### **Track Lifecycle States**

```
┌────────────────────────────────────────────────────────────────┐
│                      ACTIVE PHASE                              │
├────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Pending ──[EnsureSearchStarted]──> Searching                 │
│               (countdown starts)      │                        │
│                                       ├─> Match found          │
│                                       │    ↓                   │
│                                       └─> Queued               │
│                                            ↓                   │
│  Waiting ──[peer online]──> Downloading ──> Completed        │
│              (stalled)      │                 ↑                │
│              ↓              ├─> Paused        │                │
│           Stalled  ────────→┘                 │                │
│                                               │                │
├────────────────────────────────────────────────────────────────┤
│                    TERMINAL PHASE                              │
├────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Failed ──[Retry]──> Pending (cycle restart)                 │
│          [Removed]──> Deleted                                 │
│          [Clean]──> Removed (hide from history)              │
│                                                                 │
│  Completed ──[Clean]──> Removed (hide from history)          │
│                                                                 │
└────────────────────────────────────────────────────────────────┘
```

---

## 9. Performance & Optimization Patterns

### **UI Batching**
- **Pattern:** DispatcherTimer (250ms window) debounces collection mutations
- **Benefit:** Prevents DataGrid redraw storms under high inflow (e.g., 100+ results/sec in search)
- **Flag:** `_hasPendingUiRefresh` tracks dirty state; timer flushes accumulated changes in one batch

### **DynamicData Reactive Pipeline**
- **Pattern:** DynamicData SourceCache → Filter/Group chains → ReadOnlyObservableCollections
- **Benefit:** Live filtering without manual collection-clearing; O(1) per-change cost
- **Memory:** SourceCache keyed by `GlobalId` prevents duplicates (per-track singleton)

### **Search Clock (1-second ticker)**
- **Pattern:** Observable.Interval(1s) for countdown updates
- **Benefit:** Smooth real-time countdown without manual timer boilerplate
- **Cleanup:** SerialDisposable auto-disposes on track completion or VM destruction

### **Lazy Asset Loading**
- **Pattern:** ArtworkCacheService fetches thumbnails in background; UI renders placeholder first
- **Benefit:** No blocking; progressive enhancement

---

## 10. Recent Changes (M4–M8)

| Message | Feature | Files | Impact |
|---------|---------|-------|--------|
| M4: aa53f34 | Remove button visibility fix | UnifiedTrackViewModel | `CanRemoveFromQueue` covers 7 states |
| M6: 619aca2 | Lossless search hardening | SearchOrchestrationService, AppConfig | 20s minimum for lossless-only lanes |
| M8: dd6929f | Countdown timer UI | UnifiedTrackViewModel, DownloadCenterViewModel, StandardTrackRow.axaml, DownloadsPage.axaml | Live "Time Left: XXs" display + config param |

---

## 11. Key Design Patterns Used

| Pattern | Where | Benefit |
|---------|-------|---------|
| **Reactive ViewModel** | UnifiedTrackViewModel | Property-change reactive updates; INotifyPropertyChanged integration |
| **DynamicData Pipeline** | DownloadCenterViewModel | Efficient live collection filtering/grouping |
| **Event Bus (Pub/Sub)** | IEventBus across app | Loose coupling; global event lifecycle |
| **Singleton Scope** | DownloadCenterViewModel (injected) | One truth for download state |
| **Smart Component** | UnifiedTrackViewModel self-governing | Per-track state machine; reduces coupling to parent |
| **Observable.Interval** | Search countdown timer | Reactive 1s ticker with auto-cleanup |
| **MVVM with XAML Binding** | DownloadsPage ↔ ViewModels | Declarative UI; separation of concerns |

---

## 12. Testing Surface

### **Unit Test Targets**
- **UnifiedTrackViewModel:**
  - Countdown calculation (remaining = max - elapsed)
  - Lossless intent detection (hasLossless ∧ ¬hasMp3)
  - State machine transitions (Pending → Searching → Downloading → Completed)
  - CanRemoveFromQueue predicate coverage

- **DownloadCenterViewModel:**
  - Collection lifecycle (add/remove/filter)
  - Profile application (scope changes propagate)
  - UI batch debouncing (updates in 250ms windows)

- **DownloadGroupViewModel:**
  - Aggregate calculations (sum speeds, weighted avg progress)
  - Status text generation

### **Integration Test Targets**
- Search stream to UI binding (inflow → IncomingResults → display)
- Transfer speed aggregation (peer lanes update on speed change)
- State transitions trigger correct collection moves (Active → Completed)

---

## 13. Deployment Checklist

- [x] DownloadCenterViewModel singleton registered in DI container
- [x] UnifiedTrackViewModel constructor accepts AppConfig for config-driven window computation
- [x] SearchOrchestrationService enforces lossless 20s minimum
- [x] StandardTrackRow.axaml includes countdown binding (cyan #8BD2FF)
- [x] DownloadsPage.axaml includes countdown in knowledge bars (2 locations)
- [x] AppConfig.MinLosslessSearchDurationSeconds defaults to 20
- [x] Observable.Interval cleanup via SerialDisposable in Dispose()
- [x] Build verified: dotnet build succeeded, no XAML parse errors

---

## 14. Future Enhancements (Out of Scope)

1. **Search Stream Firehose Hardening (Phases 1–5):** Session lifecycle, dispatcher protection, hard caps, telemetry UX
2. **Countdown Color Cue:** Amber/orange when <5s remaining (urgency signal)
3. **Peer Lane Sorting:** Sort by speed descending (fastest peers first)
4. **Batch Operations:** Select multiple tracks → retry/remove in bulk
5. **Search Streaming Stats:** Aggregate results-per-second telemetry across all active tracks

---

**Diagram Legend:**
- `┌─┐` = Component boundary
- `├─` = Sub-component or feature
- `→` = Data flow or state transition
- `[Label]` = Event or action trigger

