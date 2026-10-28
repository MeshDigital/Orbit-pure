# Implementation Plan: Download Flow Diagnostics and Safety Fixes

This plan addresses the issue of new track downloads failing/stalling by identifying root causes in the network safety filter, relaxing over-aggressive filters, and implementing a persistent per-track search and download audit logger.

---

## 1. Findings & Root Cause Analysis

After deep-diving into the download orchestration and safety filters, the following critical issues were identified:
1. **Unreported Metadata Rejections**:
   - In `SoulseekAdapter.ParseTrackFromFile`, files with unreported bitrates default to `0`, and sample rates default to `null`.
   - In `SafetyFilterService.EvaluateCandidate`, the bitrate check (`candidate.Bitrate <= 700`) and sample rate check (`!candidate.SampleRate.HasValue || candidate.SampleRate.Value < 44100`) unconditionally reject these files.
   - This means any FLAC or WAV file that does not report metadata is immediately rejected before downloading. Since many files lack this info, the download queue fails to acquire them.
   - **Inferred Bitrate Calculation**: Soulseek search results *do* return the size of the track in bytes. If a track's bitrate is not reported (e.g. 0), we can mathematically infer it using the track length and file size: `Bitrate (kbps) = (Size * 8) / (Length * 1000)`. This allows the application to accurately identify FLAC and WAV tracks and pass them through quality filters even when their explicit bitrate tag is absent on the network.
2. **MP3 Fallback Blocking**:
   - In `SafetyFilterService.EvaluateCandidate`, lossy extensions (`.mp3`, `.m4a`) are unconditionally rejected by checking `_lossyBlacklist.Contains(ext)`.
   - Even when `forceMp3 = true` or in MP3 fallback mode, the safety filter rejects the files as `Lossy Extension Rejected`. MP3 fallback is therefore 100% blocked.
3. **Absence of Search Audit Trail**:
   - We lack a detailed persistent log per track on disk explaining exactly what queries were run, what peers were found, why candidates were ignored/rejected, or why downloads failed/cancelled.

---

## 2. Technical Proposed Changes

### Component 1: Safety Filter and Bitrate Inference Adjustments

#### [MODIFY] [SoulseekAdapter.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/SoulseekAdapter.cs)
- In `ParseTrackFromFile`, detect if the bitrate attribute is unreported/zero. If so, and if `length > 0` and `file.Size > 0`, calculate and assign the inferred bitrate:
  `bitrate = (int)((file.Size * 8) / (length * 1000));`
- This ensures all downstream consumers (UI, ranking scorer, safety filters) see and use a realistic bitrate instead of `0 kbps`.

#### [MODIFY] [SafetyFilterService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/SafetyFilterService.cs)
- Update `EvaluateCandidate` signature:
  `public SafetyCheckResult EvaluateCandidate(Track candidate, string query, int? targetDuration = null, bool allowLossy = false)`
- Adjust extension validation:
  - If `allowLossy == true`, skip the `_lossyBlacklist` check. Only enforce that the extension is inside either the lossy blacklist or the lossless whitelist (to block `.exe`, `.zip`, etc.).
- Adjust bitrate and sample-rate gates:
  - Only apply the `< 700kbps` gate if `allowLossy == false` AND `candidate.Bitrate > 0` (unreported bitrate `0` should pass).
  - Only apply the `< 44.1kHz` gate if `allowLossy == false` AND `candidate.SampleRate.HasValue` (unreported sample rate should pass).
- Update `EvaluateSafety` signature to match:
  `public void EvaluateSafety(Track track, string query, bool allowLossy = false)`

#### [MODIFY] [SearchOrchestrationService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/SearchOrchestrationService.cs)
- In `StreamAndRankResultsAsync`, compute `allowLossy`:
  `var allowLossy = maxBitrate > 0 || formatFilter.Contains("mp3", StringComparer.OrdinalIgnoreCase);`
- Pass `allowLossy` to `EvaluateSafety`:
  `_safetyFilter.EvaluateSafety(track, normalizedQuery, allowLossy);`

#### [MODIFY] [DownloadDiscoveryService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DownloadDiscoveryService.cs)
- In `PerformSearchTierAsync`, pass `forceMp3` as the `allowLossy` parameter to `EvaluateCandidate`:
  `var safety = _safetyFilter.EvaluateCandidate(searchTrack, query, targetDurationSeconds, allowLossy: forceMp3);`

---

### Component 2: High-Performance Track Audit Logger and ViewModel

#### [NEW] [TrackAuditLogger.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/Diagnostics/TrackAuditLogger.cs)
- Introduce a singleton `TrackAuditLogger` implementing `ITrackAuditLogger` that:
  - Uses `System.Threading.Channels.Channel<AuditLogEntry>` for non-blocking I/O to avoid UI lockups.
  - Implements a continuous background reader that flushes logs to disk asynchronously.
  - Partition files monthly: `%APPDATA%/ORBIT/TrackLogs/YYYY-MM/[TrackUniqueHash]_audit.log` to prevent OS directory bloat.
  - Auto-subscribes to `TrackDetailedStatusEvent` on the `IEventBus` to capture all UI Live Console messages.
  - Exposes `LogSearchCandidate(hash, peer, bitrate, format, action, reason)` to cleanly format and log evaluations.

#### [NEW] [BlackBoxTerminalViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Diagnostics/BlackBoxTerminalViewModel.cs)
- Introduce `BlackBoxTerminalViewModel` that:
  - Represents the backing context for the `BlackBoxTerminal` UserControl.
  - Launches a background streaming file reader that tails the corresponding track log file in real-time.
  - Parses log lines into structured `TerminalLogEntry` items (`Timestamp`, `Stage`, `Level`, `Message`).
  - Implements `IDisposable` to cleanly cancel the tailing task when closed.

#### [NEW] [ForensicLevelToColorConverter.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Converters/ForensicLevelToColorConverter.cs)
- Value converter that maps log levels to Hex Brushes (e.g. `ERROR` -> Red, `WARN` -> Yellow, `INFO` -> Teal/Mint).

#### [MODIFY] [App.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/App.axaml)
- Register `ForensicLevelToColorConverter` resource:
  `<converters:ForensicLevelToColorConverter x:Key="ForensicLevelToColorConverter"/>`

#### [MODIFY] [App.axaml.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/App.axaml.cs)
- Register the new logger:
  `services.AddSingleton<ITrackAuditLogger, TrackAuditLogger>();`

---

### Component 3: Integration of Logging in Orchestration

#### [MODIFY] [DownloadManager.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DownloadManager.cs)
- Inject `ITrackAuditLogger`.
- Log high-level transitions:
  - Orchestration start/stop, library reuse, validation failures, transfer starts, progress/stall events, cancellations, final success/failure dispositions.

#### [MODIFY] [DownloadDiscoveryService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DownloadDiscoveryService.cs)
- Inject `ITrackAuditLogger`.
- Log search tier details:
  - Query dispatched, variations sanitized, and lane constraints.
  - Candidates found: peer, filename, format, bitrate, sample rate, queue size, match/fit/reliability/final scores.
  - Log safety check details and rejections with `LogSearchCandidate`.

#### [MODIFY] [PostDownloadSpectralScanService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/PostDownloadSpectralScanService.cs)
- Inject `ITrackAuditLogger`.
- Log the post-download spectral check verdict, cutoff frequency, Rolloff, DBFS, and transcode status.

---

### Component 4: Dynamic UI Right Panel Integration

#### [MODIFY] [TrackOperationsViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Library/TrackOperationsViewModel.cs)
- Expose `OpenAuditLogCommand`.
- Send an `OpenInspectorEvent` carrying a new instance of `BlackBoxTerminalViewModel` with the track's log file path:
  `MessageBus.Current.SendMessage(OpenInspectorEvent.Create(new BlackBoxTerminalViewModel(...), "Library.TrackSelection.AuditLog"));`

#### [MODIFY] [UnifiedTrackViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Downloads/UnifiedTrackViewModel.cs)
- Expose `OpenAuditLogCommand` that broadcasts `OpenInspectorEvent` with the `BlackBoxTerminalViewModel`.

#### [MODIFY] [MainWindow.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/MainWindow.axaml)
- Register `BlackBoxTerminalViewModel` DataTemplate inside the Inspector `ContentControl` pane:
  ```xml
  <DataTemplate DataType="vmCore:BlackBoxTerminalViewModel">
      <controls:BlackBoxTerminal/>
  </DataTemplate>
  ```

#### [MODIFY] [OpenInspectorEvent.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Events/OpenInspectorEvent.cs)
- Resolve presentation defaults for `"Library.TrackSelection.AuditLog"` to `("SEARCH AUDIT LOG", "📝")`.

#### [MODIFY] [TrackListView.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/TrackListView.axaml)
- Add "Terminal: View Search Audit" to the `VirtualGrid` right-click context menu:
  `<MenuItem Header="Terminal: View Search Audit" Command="{Binding Operations.OpenAuditLogCommand}" Icon="{StaticResource ConsoleIcon}" />`

#### [MODIFY] [DownloadsPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/DownloadsPage.axaml)
- Add "Terminal: View Search Audit" to the downloads list items context menu:
  `<MenuItem Header="Terminal: View Search Audit" Command="{Binding OpenAuditLogCommand}" Icon="{StaticResource ConsoleIcon}" />`

---

## 3. Verification Plan

### Automated Tests
- Run `dotnet test` to verify no compilation errors and ensure that existing tests run successfully.
- Verify `TrackAuditLogger` correctly writes entries asynchronously without blocking.

### Manual Verification
1. **The "Ghost" FLAC Test**: Search for a niche track known to drop metadata on the P2P network. Verify it passes the safety filter, enters the `Downloading` state, and successfully downloads to disk.
2. **The Fallback Test**: Attempt to download a track strictly unavailable in lossless. Verify `allowLossy` activates, the filter bypasses the lossy-blacklist, enforces `candidate.Bitrate >= 256`, and downloads.
3. **UI Stress Test**: Trigger 5 simultaneous downloads. Open the `BlackBoxTerminal` for one of them via the context menu. Verify the UI thread remains responsive (60fps) while the asynchronous channel flushes evaluations.
4. **Log Retention**: Verify logs are written to `%APPDATA%/ORBIT/TrackLogs/YYYY-MM/[TrackUniqueHash]_audit.log` and correctly partitioned.
