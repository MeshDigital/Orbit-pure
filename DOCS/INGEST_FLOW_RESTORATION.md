# Ingest Flow Restoration — Session Report
**Date**: 2026-02-21  
**Build Status**: ✅ 0 Warnings, 0 Errors  
**Coverage**: Zombie Scout reports 81% subscription tracking (93/115)

---

## Executive Summary

This session addressed two critical system-level issues: **priority lane saturation** in the download manager and **memory/CPU leaks** from unmanaged ViewModel subscriptions. The result is a production-hardened download pipeline and a permanent automated guardrail preventing future leaks.

---

## Priority 1: Download Lane Architecture

### Problem
All new downloads defaulted to `Priority = 0`, the same lane reserved for VIP/ForceStart operations. This meant "Force Start" had no actual effect — every track was already in the express lane.

### Solution
Implemented a tiered **Lane-Based Priority Architecture**:

| Lane     | Priority | Designation | Behavior                                                 |
| -------- | -------- | ----------- | -------------------------------------------------------- |
| Express  | **0**    | VIP         | Bypasses all queues. Reserved for ForceStart/VIP Rocket. |
| Standard | **1–9**  | User-Bumped | Manual priority adjustments ("Bump to Top").             |
| Bulk     | **10**   | Default     | Steady state for new imports and retries.                |
| Low      | **20**   | Cooldown    | Failed retries with backoff.                             |

### Files Changed
| File                                                                  | Change                                                     |
| --------------------------------------------------------------------- | ---------------------------------------------------------- |
| `Services/DownloadManager.cs` L439                                    | Default priority: `0` → `10`                               |
| `Services/DownloadManager.cs` L534-536                                | Retry priority: `0` → `10`                                 |
| `Services/DownloadManager.cs` L889, L1032, L2283, L2316, L2405, L2556 | VIP/ForceStart remains at `0` (verified, no change needed) |

### Stalled Download Indicator Fix
| File                                                  | Change                                                                                                       |
| ----------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `Views/Avalonia/Controls/StandardTrackRow.axaml` L277 | Replaced missing `{StaticResource warning_regular}` with inline SVG path data for the amber warning triangle |

The `IsStalled` property and `.pulse_amber` animation were already correctly wired — the only issue was the missing icon resource causing a silent render failure.

### Thread Safety Audit
Verified that post-download operations don't block the download semaphore:
- **Enrichment**: Uses `QueueForEnrichmentAsync()` (non-blocking queue)
- **Analysis**: Wrapped in `Task.Run()` (background thread)

---

## Priority 2: EventBus Leak Protection ("Zombie Exorcism")

### Problem
Multiple ViewModels subscribed to EventBus events or Rx observables without implementing `IDisposable`. When these VMs were destroyed (e.g., closing Theater Mode), the EventBus `Subject<T>` held strong references, preventing garbage collection and causing:
- **Memory leaks**: Dead VMs accumulating in heap
- **CPU leaks**: `TheaterModeViewModel`'s 30fps DispatcherTimer continuing to fire for ghost instances

### Audit Results

| ViewModel                    | Leak Source                                | Subs    | Fix                                                                                           |
| ---------------------------- | ------------------------------------------ | ------- | --------------------------------------------------------------------------------------------- |
| `ContextualSidebarViewModel` | 2 raw `.Subscribe()`, no `IDisposable`     | 2       | Added `IDisposable` + `CompositeDisposable` + `.DisposeWith()`                                |
| `TheaterModeViewModel`       | 6 raw `.Subscribe()` + `DispatcherTimer`   | 6+timer | Added `IDisposable` + `CompositeDisposable` + timer `.Stop()` + `Tick -=` + stem engine pause |
| `SettingsViewModel`          | 1 untracked EventBus sub                   | 1       | Captured `IDisposable` return → disposed in existing `Dispose()`                              |
| `LibrarySourcesViewModel`    | 1 untracked EventBus sub, no `IDisposable` | 1       | Added `IDisposable` + tracked sub                                                             |

### Already Clean (No Fix Needed)
- `TrackInspectorViewModel` — 4/4 tracked via `.DisposeWith()`
- `VirtualizedTrackCollection` — 5/5 tracked
- `ProjectListViewModel` — all tracked via `_disposables.Add()`
- `TrackListViewModel` — all tracked
- `SearchViewModel` — has `CompositeDisposable`
- `StatusBarViewModel` — has `CompositeDisposable`

---

## Priority 3: Zombie Scout (Automated Guardrail)

### New File
`Tests/SLSKDONET.Tests/Architecture/ViewModelDisposalGuardTests.cs`

### Tests Implemented

| Test                                                            | Purpose                                                                         |
| --------------------------------------------------------------- | ------------------------------------------------------------------------------- |
| `AllViewModels_WithEventSubscriptions_MustImplementIDisposable` | Source-level scan: any `.cs` file with `.Subscribe()` must have `IDisposable`   |
| `AllViewModels_WithDispatcherTimer_MustDisposeTimer`            | Timer leak detection: DispatcherTimer must be stopped in `Dispose()`            |
| `AllViewModels_WithCompositeDisposable_MustDisposeIt`           | Ensures `CompositeDisposable` fields are actually disposed                      |
| `AllTypes_WithEventBusField_MustImplementIDisposable`           | Reflection-based: any class with `IEventBus` field must implement `IDisposable` |
| `DiagnosticReport_SubscriptionHealthAudit`                      | Generates full subscription health dashboard                                    |

### Current Health Report
```
Total Files with Subscriptions: 26
Total Subscriptions:            115
Tracked (DisposeWith/Add):       93
Untracked:                       22
Coverage:                        81%
```

### Remaining Zombies Flagged (for future sessions)
- `BulkOperationViewModel`
- `CommandPaletteViewModel`
- `ConnectionViewModel`
- `DJCompanionViewModel`
- `FlowBuilderViewModel`

---

## Other Changes (from earlier in session)

| File                               | Change                                                                              |
| ---------------------------------- | ----------------------------------------------------------------------------------- |
| `Services/AI/SonicMatchService.cs` | BPM penalty threshold standardized to 0.06 (6%)                                     |
| `Services/SoulseekAdapter.cs`      | Event implementation verified for `DownloadProgressChanged` and `DownloadCompleted` |
| `Services/ISoulseekAdapter.cs`     | Event declarations verified                                                         |
| `App.axaml`                        | `.pulse_amber` animation style confirmed present                                    |

---

## Run Commands

```bash
# Full zombie sweep
dotnet test --filter "FullyQualifiedName~ViewModelDisposalGuard"

# Diagnostic health report only
dotnet test --filter "FullyQualifiedName~DiagnosticReport" -v detailed
```
