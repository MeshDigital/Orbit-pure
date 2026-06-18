# Codebase Quality Improvements P1–P4 Completion Report

> Status: Completed
>
> Last reviewed: 2026-06-16
>
> See also: [MEMORY_INDEX.md](MEMORY_INDEX.md)

Multi-session codebase quality sweep covering critical defects, memory leaks, EF Core lifecycle, test coverage, and several runtime crashes discovered during live use.

---

## 1. P1 — Critical Defects

### 1.1 Fire-and-Forget Task.Run() Error Handling
**File:** `Services/DownloadManager.cs` (15+ sites)

All `_ = Task.Run(...)` call sites were wrapped with `try { ... } catch (Exception ex) { _logger.LogError(ex, "..."); }`. Unhandled exceptions inside these tasks were previously swallowed silently; users now see them in the Serilog console sink and the log file.

### 1.2 Bare `catch {}` Blocks
**Files:** `Services/AnalysisResultDiskCache.cs`, `Services/DownloadManager.cs`, `Services/SearchResultMatcher.cs`, and 11 others

14 bare `catch { }` blocks replaced with `catch (Exception ex) { _logger.LogDebug(ex, "Best-effort cleanup failed"); }`. Production diagnosis of edge-case failures is now possible.

### 1.3 DownloadHealthMonitor Blocking Dispose
**File:** `Services/DownloadHealthMonitor.cs:198`

`_monitorTask?.Wait()` in `Dispose()` blocked the UI thread on app shutdown. Replaced with `IAsyncDisposable.DisposeAsync()` using `await _monitorTask.ConfigureAwait(false)` and a cancellation timeout so the window no longer freezes at exit.

### 1.4 async void in UnifiedTrackViewModel
**File:** `ViewModels/Downloads/UnifiedTrackViewModel.cs`

`CheckSynergyAsync()` was `async void`. Unobserved exceptions in `async void` crash the process. Converted to `async Task` and the call site uses `.ContinueWith(t => _logger.LogError(...), TaskContinuationOptions.OnlyOnFaulted)`.

### 1.5 Disabled Ranking Strategy
**File:** `App.axaml.cs:160`

The ranking strategy was commented out with `// TODO: Fix this after app launches` due to an NRE. Root cause was a null dependency at startup; added a null guard and deferred initialization to re-enable it.

---

## 2. P2 — Memory Leaks

### 2.1 Missing Dispose in UnifiedTrackViewModel
`UnifiedTrackViewModel` declared `IDisposable` and held a `CompositeDisposable _disposables` but never implemented `Dispose()`. Added `public void Dispose() => _disposables.Dispose();`.

### 2.2 Undisposed PropertyChanged Subscriptions
**Files:** `ViewModels/Downloads/DownloadCenterViewModel.cs:587`, `ViewModels/AnalysisPageViewModel.cs:1784`

`_downloadManager.PropertyChanged += (s, e) => ...` subscriptions were never unsubscribed, keeping parent ViewModels alive. Converted both to `Observable.FromEventPattern(...).DisposeWith(_disposables)`.

### 2.3 EventBus Subscription Tracking in DownloadManager
`DownloadManager` subscribed to `IEventBus` events in its constructor but never unsubscribed. Added `IDisposable` implementation; all subscriptions now stored in a `CompositeDisposable` and released on `Dispose()`.

### 2.4 Unbounded Downloads Collection
Completed downloads accumulated without trim in `DownloadManager._downloads`. Added a ring-buffer cap so terminal-state entries are evicted beyond a threshold, preventing memory growth over long sessions.

---

## 3. P3-C — IDbContextFactory Injection

**Files affected:** `LibrarySourcesViewModel`, `LibraryViewModel`, `LibraryViewModel.Commands`, `DownloadCenterViewModel`, `UnifiedTrackViewModel`, `WorkstationDeckViewModel`, `WorkstationViewModel`, `SimilarTracksViewModel`, `SettingsViewModel`

12+ direct `new AppDbContext()` calls replaced with injected `IDbContextFactory<AppDbContext>` (EF Core pooled factory). Pattern used:

```csharp
// DI-only ViewModels: required parameter
public LibraryViewModel(IDbContextFactory<AppDbContext> dbFactory, ...) { _dbFactory = dbFactory; }

// ViewModels also instantiated manually (tests, call-sites): optional parameter with fallback
await using var ctx = _dbFactory != null ? _dbFactory.CreateDbContext() : new AppDbContext();
```

`SimilarTracksViewModel.cs` contains two public classes in one file (`SimilarTrackRowViewModel` + `SimilarTracksViewModel`); both received `_dbFactory` fields and 3 construction call-sites were updated to propagate the factory.

---

## 4. P4 — Test Coverage

### 4.1 SafetyFilterTests Expansion
**File:** `Tests/SLSKDONET.Tests/Services/SafetyFilterTests.cs`

Expanded from 4 tests to 19. New coverage:
- `IsUpscaled()`: no cutoff → false; FLAC <20 kHz → true; FLAC >20 kHz → false; high-bitrate <16.1 kHz → true; high-bitrate >16.1 kHz → false; low-bitrate with low cutoff → false (expected, not upscaled)
- Size heuristic: 1 MB for 900 kbps/300 s → rejected; ~33.75 MB → accepted
- Keyword blocklist (Theory, 5 cases): regex word-boundary matching; filenames with `_` adjacent to keywords were initially false-negatives (`\b` doesn't match adjacent to `_`)
- Sample rate gate: 22050 Hz → rejected; 44100 Hz → accepted; 96000 Hz → accepted
- Extension guards: `.exe` → banned; `.mp3` in strict mode → rejected; `.mp3` with `allowLossy: true` → accepted

### 4.2 PostDownloadSpectralScanServiceTests
**File:** `Tests/SLSKDONET.Tests/Services/PostDownloadSpectralScanServiceTests.cs`

`DatabaseService` has no virtual methods so Moq cannot mock it. Used `RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService))` as a stub. Tests cover:
- State gate: `Searching / Downloading / Failed / Cancelled` events do not trigger `AnalyseAsync` (Theory)
- `CompletedState_WhenDbThrows_DoesNotCrash_AndAnalyseIsNotCalled`: uninitialized stub throws NRE; `ScanAsync` catches it; no crash; no analysis called
- Dispose safety: `Dispose()` does not throw; events after `Dispose()` are ignored

### 4.3 Pre-existing Build Failures Fixed
Three test files had compile errors from earlier changes:
- `SearchOrchestrationServiceTests.cs` (9 sites) + `SearchViewModelTests.cs` (1 site): `ISafetyFilterService.EvaluateSafety` has `bool allowLossy = false` optional param; Moq expression trees reject optional args (CS0854). Fixed by passing `It.IsAny<bool>()` explicitly.
- `IngestionLifecycleServiceLoggingTests.cs`: `DownloadManager` constructor gained `ITrackAuditLogger` as last param in P1; test helper was missing it. Fixed with `new Mock<SLSKDONET.Services.Diagnostics.ITrackAuditLogger>().Object`.

---

## 5. Runtime Crashes Fixed (Live Use, 2026-06-16)

### 5.1 WaveformControl Cue-Click Crash
**File:** `Views/Avalonia/Controls/WaveformControl.cs:438`
**Error:** `System.InvalidOperationException: Command requires parameters of type System.Double, but received parameter of type SLSKDONET.Models.OrbitCue`

`CueClickedCommand` is bound to `Deck.SeekCommand` (`ReactiveCommand<double, Unit>`) in `WorkstationDeckRow.axaml:234`. `OnPointerPressed` was passing the raw `OrbitCue` object. Fixed by passing `cue.Timestamp` (the `double` position in seconds).

### 5.2 VirtualizedTrackCollection Layout Crash
**File:** `ViewModels/Library/VirtualizedTrackCollection.cs:252`
**Error:** `System.InvalidOperationException: Changes in data source are not allowed during layout`

Avalonia's `ItemsRepeater` calls `IList.get_Item(index)` during its layout pass. `get_Item` called `LoadPageAsync().Wait()`, which on completion fired `CollectionChanged` synchronously — mid-layout. Fixed by wrapping both `CollectionChanged?.Invoke(...)` calls (in `LoadPageAsync` and `LoadMoreItemsAsync`) with `Dispatcher.UIThread.Post(...)` to defer them to the next dispatch cycle.

### 5.3 Audio Analysis Log Noise
**Files:** `Services/AudioAnalysis/AudioIngestionPipeline.cs`, `Services/AudioAnalysis/AudioAnalysisService.cs`

For corrupt FLAC files (non-spec blocksize 4608 > 4096), FFmpeg's full multi-line stderr blob was embedded in the `LogError` message, producing dozens of raw `[flac @ ...]` lines in the console. Two fixes:
1. In `DecodeToTempWavAsync`: extract the last meaningful line from stderr for the log message; keep full stderr in exception for debug.
2. In `AnalyzeFileAsync`: removed `ex` from `LogWarning(ex, ...)` to avoid double-logging the exception dump (it was already logged by the ingestion pipeline at Error level).

### 5.4 Analysis Queue Re-Submit Loop for Corrupt Files
**File:** `ViewModels/AnalysisPageViewModel.cs:1131`

The "Analyze All" filter was `t.AnalysisStatus != AnalysisRunStatus.Completed`, so permanently failed tracks (e.g., undecodable FLAC) were re-queued on every "Analyze All" invocation and spammed the logs. Filter changed to also exclude `AnalysisRunStatus.Failed`. Failed tracks remain visible in the queue with their red badge; users can still retry them manually via individual track re-analyse.

---

## 6. Test Results

After all changes: **1,015 passing, 2 skipped (pre-existing architecture guard failures unrelated to this work), 0 new failures**.
