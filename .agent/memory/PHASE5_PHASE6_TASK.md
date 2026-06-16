# Task List: Download Flow Diagnostics and Safety Fixes

- [x] **1. Safety Filter & Bitrate Inference Adjustments**
  - [x] Modify `SoulseekAdapter.cs` to calculate inferred bitrates: `(file.Size * 8) / (length * 1000)` when the bitrate attribute is unreported (0)
  - [x] Modify `SafetyFilterService.cs` to skip `_lossyBlacklist` check if `allowLossy == true`, and ignore bitrate/sample-rate check if unreported (0/null). Enforce `>= 256` kbps for MP3 fallbacks.
  - [x] Modify `SearchOrchestrationService.cs` to pass `allowLossy` based on maxBitrate / format filter
  - [x] Modify `DownloadDiscoveryService.cs` to pass `forceMp3` as `allowLossy` to safety filter

- [x] **2. Asynchronous Track Audit Logger Service**
  - [x] Create `ITrackAuditLogger.cs` and `TrackAuditLogger.cs` in `Services/Diagnostics/`
  - [x] Implement `Channel<AuditLogEntry>` background writer loop for non-blocking file I/O
  - [x] Implement log file partitioning: `%APPDATA%/ORBIT/TrackLogs/YYYY-MM/[TrackUniqueHash]_audit.log`
  - [x] Register `ITrackAuditLogger` in `App.axaml.cs`

- [x] **3. Integration of Logging in Orchestration**
  - [x] Integrate logger in `DownloadManager.cs` (high-level lifecycle and validation details)
  - [x] Integrate logger in `DownloadDiscoveryService.cs` (search attempts, variation plans, candidates evaluated and scored/ignored/rejected)
  - [x] Integrate logger in `PostDownloadSpectralScanService.cs` (verdict, confidence, and cutoff details)

- [x] **4. Context Menu UI & BlackBoxTerminal Unification**
  - [x] Create `BlackBoxTerminalViewModel.cs` in `ViewModels/Diagnostics/` to tail and parse log files in real-time
  - [x] Create `ForensicLevelToColorConverter.cs` in `Views/Avalonia/Converters/` and register in `App.axaml`
  - [x] Map `BlackBoxTerminalViewModel` DataTemplate in `MainWindow.axaml` to render `<controls:BlackBoxTerminal/>`
  - [x] Support `Library.TrackSelection.AuditLog` defaults in `OpenInspectorEvent.cs`
  - [x] Expose `OpenAuditLogCommand` in `TrackOperationsViewModel.cs` to publish `OpenInspectorEvent`
  - [x] Expose `OpenAuditLogCommand` in `UnifiedTrackViewModel.cs`
  - [x] Bind `OpenAuditLogCommand` to "Terminal: View Search Audit" menu item in `TrackListView.axaml` ContextMenu
  - [x] Bind `OpenAuditLogCommand` to "Terminal: View Search Audit" menu item in `DownloadsPage.axaml` ContextMenu

- [x] **5. Verification & Testing**
  - [x] Verify compiling and running the application
  - [x] Verify that WAV and FLAC tracks with missing attributes are successfully downloaded
  - [x] Verify MP3 fallback searches are allowed to download
  - [x] Verify logs are correctly written to partition folders and tailed in the right-panel terminal view in real-time
