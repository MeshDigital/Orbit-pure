# Network Resilience & Library Reconciliation

> Status: Completed
>
> Last reviewed: 2026-06-16 (updated: Full Library Sync added)
>
> See also: [download_orchestrator_hit_rate_improvements.md](download_orchestrator_hit_rate_improvements.md), [ui_overhaul_piped_marble_completion.md](ui_overhaul_piped_marble_completion.md)

Two independent hardening streams implemented in the same session. Both address gaps identified via a Soulseek protocol audit and a virtual/physical state architecture analysis.

---

## Stream A — Soulseek Ban Detection & Search Lockout

### Problem

The Soulseek server sends a `GlobalMessage` — "You have been banned for 30 minutes" — when the client fires too many searches in a short window (~28 searches per 10 min). Before this fix, the app had no listener on `GlobalMessageReceived`. All searches returned zero results silently. Automated queues kept firing uselessly for the full 30-minute ban window, potentially extending the ban.

### What already existed (do not re-implement)

- `SearchLoadSheddingPolicy` — adapts throttle delay under concurrent search pressure
- `_activeSearchCount` counter in `SearchOrchestrationService` — tracks concurrent searches
- `SearchThrottleDelayMs` stagger between variation lanes
- `ProtocolHardeningService.NormalizeSearchQuery` — strips globally banned phrases before they reach the wire
- `ImportOrchestrator` — uses sequential `foreach`, not `WhenAll`, so mass parallel flooding is structurally prevented
- Per-peer `SemaphoreSlim(1,1)` gate in `DownloadManager._perPeerGates` — enforces the 1-file-per-peer Soulseek protocol limit

### What was added

**`Events/SearchEvents.cs`** — `SearchBanDetectedEvent(string RawMessage, DateTime LockoutUntilUtc)` record.

**`Services/SoulseekAdapter.cs`**:
- Added `"GlobalMessageReceived"` to `ClientEventNamesToClear` (cleaned up on client swap)
- Added `client.GlobalMessageReceived` handler: detects any message containing "ban", sets `LockoutUntilUtc = UtcNow + 30min`, calls `_healthService.RecordConnectionKick("server-ban:...")`, publishes `SearchBanDetectedEvent`. Non-ban global messages are info-logged.

**`Services/SearchOrchestrationService.cs`**:
- `IEventBus` injected (new constructor parameter, registered as singleton — no DI breakage)
- `_searchBanUntilUtc` (`DateTime`) field; `_banSub` subscription to `SearchBanDetectedEvent` sets it
- Ban guard at top of `SearchAsyncCore`: if `DateTime.UtcNow < _searchBanUntilUtc` → log warning + `yield break`. Searches exit cleanly with zero results rather than hammering a server that has severed the connection.

### Key invariant

`GlobalMessageReceived` is `EventHandler<string>` — the arg IS the message string (confirmed via reflection on `Soulseek 9.1.0`). No wrapper EventArgs class needed.

---

## Stream B — Library Reconciliation Engine

### Problem

When a file was deleted by the user via Explorer (or moved to another drive), `LibraryService.LoadAllLibraryEntriesAsync` detected the missing file and published `FileMissingDetectedEvent`. The `DownloadCenterViewModel` subscribed and showed a red error banner. But **no code wrote back to the DB** — the `PlaylistTrackEntity.Status` remained `Downloaded` and `ResolvedFilePath` remained set. Auto-download never re-queued the track. The banner was a notification, not recovery.

### What already existed (do not re-implement)

- `FileMissingDetectedEvent` event record in `Events/LibraryDomainEvents.cs`
- `LibraryService.LoadAllLibraryEntriesAsync` — calls `File.Exists` on cached entries and publishes the event for stale paths
- `LibraryService.SyncLibraryEntriesFromTracksAsync` — checks `File.Exists` before indexing
- `PathProviderService.CleanupOrphanedPartFilesAsync` — startup scan for `.part` files older than 24h (called from `DownloadManager` at init)
- `TrackAvailabilityState` enum: `Ghost → QueuedForDownload → Downloading → LocalUnanalyzed → Ready`
- `TrackStatus` enum: `Missing`, `Downloaded`, `Pending`, `Failed`, `OnHold`, `Skipped`
- `DownloadManager` startup orphan sweep — resets tracks stuck in `Searching/Downloading/Stalled` state (in-memory only, not the file-deleted case)

### What was added

**`Services/DatabaseService.cs`** — `ReconcilePhysicalFilesAsync()`:
- Loads all `PlaylistTrackEntity` where `Status == TrackStatus.Downloaded && ResolvedFilePath != null` (tracked, not `AsNoTracking`)
- For each: if `!File.Exists(ResolvedFilePath)` → sets `Status = Missing`, `AvailabilityState = Ghost`, `ResolvedFilePath = null`
- One `SaveChangesAsync` call for the whole batch
- Returns `(int Reset, int Checked)` tuple

**`Services/ILibraryService.cs`** — `Task<(int Reset, int Checked)> ReconcileLibraryAsync()` added to interface.

**`Services/LibraryService.cs`** — `ReconcileLibraryAsync()` implementation:
- Calls `_databaseService.ReconcilePhysicalFilesAsync()`
- If any rows reset: calls `_cache.InvalidateGlobalLibrary()` so next load sees corrected state
- Logs result

**`ViewModels/SettingsViewModel.cs`**:
- `ILibraryService?` injected as optional parameter (no breaking DI change)
- `ReconcileLibraryCommand` (`AsyncRelayCommand`), `IsReconciling` (bool), `ReconcileStatus` (string) properties
- `ReconcileLibraryAsync()` private method: guards on `IsReconciling`, calls service, writes human-readable result to `ReconcileStatus`

**`Views/Avalonia/SettingsPage.axaml`** — "Reconcile Library" button added below "Scan Library" in the Library Scan section. Green checkmark icon (`checkmark_regular`), `IsEnabled` bound to `!IsReconciling`, status text bound to `ReconcileStatus`.

### Auto-requeue flow (post-fix)

1. User deletes file via Explorer
2. Next `LoadAllLibraryEntriesAsync` → `FileMissingDetectedEvent` fired (existing)
3. User clicks "Reconcile Library" in Settings → `ReconcilePhysicalFilesAsync` resets row to `Status=Missing`
4. Auto-download queue picks up `Missing` tracks on next project scan cycle → re-searches Soulseek

### Design note — no automatic requeue on detection

The reconciliation is deliberately manual (button-triggered) rather than automatic on `FileMissingDetectedEvent`. Automatically resetting to `Missing` and re-queuing every detected absence would be too aggressive: network drives, external HDDs, and cloud-synced folders all cause transient `File.Exists` failures that resolve on reconnect. A manual reconcile is the right UX for an intentional "I deleted some files, fix the queue" operation.

### Multi-folder coverage clarification

- **Scan** (`ScanAllFoldersAsync`) queries `context.LibraryFolders.Where(f => f.IsEnabled)` — correctly iterates all enabled configured folders. Direction: disk → DB (discovery).
- **Reconcile** (`ReconcilePhysicalFilesAsync`) queries all `PlaylistTrackEntity.Downloaded` rows by absolute `ResolvedFilePath` — path-aware, implicitly covers files from any folder since paths are absolute. Direction: DB → disk (validation).
- Neither operation had a coverage gap for multiple library folders. They serve complementary roles and together form a complete sync cycle.

---

## Stream C — Full Library Sync

### Problem

Scan and Reconcile served different directions (disk→DB and DB→disk). To fully sync a library across multiple configured folders, both needed to be run. There was no single-click operation combining them, so users had to know to press both buttons in order.

### What was added

**`ViewModels/SettingsViewModel.cs`**:
- `FullLibrarySyncCommand` (`AsyncRelayCommand`) — CanExecute guards on `!IsFullSyncing && !IsScanning && !IsReconciling`
- `IsFullSyncing` (bool) and `FullSyncStatus` (string) properties
- `FullLibrarySyncAsync()` private method:
  1. Calls `_libraryFolderScannerService.EnsureDefaultFolderAsync` then `ScanAllFoldersAsync` with progress callback updating `FullSyncStatus`
  2. Calls `_libraryService.ReconcileLibraryAsync()` immediately after scan
  3. Calls `LoadRemovalCandidatesAsync()` to refresh the removal candidates list
  4. Reports combined result: imported count + upgraded count + reset count
- `ScanLibraryCommand` and `ReconcileLibraryCommand` CanExecute also guards on `!IsFullSyncing` so individual buttons are disabled while a full sync is running
- All three commands' `RaiseCanExecuteChanged` are cross-notified in each operation's finally block

**`Views/Avalonia/SettingsPage.axaml`**:
- "Full Library Sync" button added above the individual Scan/Reconcile buttons in the Library Scan section
- Gold styling (`#2A1F00` background, `#7A5A00` border, `#FFD740` icon, `#FFF3B0` label) to distinguish it as the master action
- Uses `arrow_counterclockwise_regular` icon
- "Or run individually:" label separates it from the two individual buttons below
- `FullSyncStatus` text shown inline beside the button

### Full sync flow

1. User clicks "Full Library Sync" in Settings
2. Step 1/2: Scanner walks all enabled `LibraryFolderEntity` rows, discovers new files, updates `LibraryEntry` and links to `PlaylistTrackEntity`
3. Step 2/2: Reconciler checks all `Downloaded` `PlaylistTrackEntity` rows, resets any whose `ResolvedFilePath` no longer exists to `Status=Missing`
4. Removal candidates list refreshes
5. Status line reports: `Done. Imported N | Upgraded N | All N files verified.` or `Done. Imported N | Upgraded N | Reset N missing file(s).`
