# Walkthrough: Antigravity UI Overhaul, Dialog Systems & Download Flow Diagnostics

We have successfully completed:
1. The UI overhaul and fully implemented the missing dialog systems for **Tag Edit** and **Add to Playlist**.
2. The Download Flow Diagnostics & Safety Hardening system to solve download blockages on Soulseek and implement live search/download logs.

---

## 🛠️ Phase 5: Batch Action FAB & Dialog Systems (Completed)

### 1. FAB UI Hardening & separation
* **[LibraryPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/LibraryPage.axaml)**:
  * Hardened the floating action bar's background with a completely solid `#FF0B0B12` brush (100% opaque) to prevent underlying track list text from bleeding through.
  * Enhanced the outer borders with a high-contrast `#904EC9B0` brush and thickness of `1.5`.
  * Added a massive drop shadow (`BoxShadow="0 12 40 8 #FF000000"`) to isolate the floating bar from the list content.
  * Fixed a visual bug where collapsing/minimizing the navigation sidebar squished and vertically wrapped the text in the middle scrollable panel. Added `IsVisible="{Binding !IsNavigationCollapsed}"` to the middle ScrollViewer to clean up the drawer collapsed state.
  * Fixed the library header title truncation bug where the title displayed as "LIBRAI" instead of "LIBRARY". Changed the header grid to use an `Auto` column for the title and introduced a `*` Spacer panel, pushing the quick-access action buttons to the right and preventing any text clipping.

### 2. Playlist Picker Dialog
* **[PlaylistPickerViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Library/PlaylistPickerViewModel.cs)**:
  * Exposes existing user playlists fetched from the database.
  * Includes input tracking for creating a brand-new playlist, automatically clearing existing list selection on input.
* **[PlaylistPickerDialog.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Dialogs/PlaylistPickerDialog.axaml) & [.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Dialogs/PlaylistPickerDialog.axaml.cs)**:
  * Standardized dialog layout featuring existing playlists selection and new playlist name generation.
  * Bound button activation to selection or text input validation.

### 3. Batch Tag Edit Dialog
* **[BatchTagEditViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Library/BatchTagEditViewModel.cs)**:
  * Tracks bulk changes for common fields: **Artist**, **Album**, **Genre**, and **Year**.
  * Allows executing changes when at least one field is filled.
* **[BatchTagEditDialog.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Dialogs/BatchTagEditDialog.axaml) & [.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Dialogs/BatchTagEditDialog.axaml.cs)**:
  * Premium, styled modal window indicating that blank fields will preserve their original values on the tracks.

### 4. Service Orchestration & Command Wiring
* **[DialogService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DialogService.cs) & [IDialogService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/IDialogService.cs)**:
  * Exposed `ShowPlaylistPickerDialogAsync` and `ShowBatchTagEditDialogAsync` to allow ViewModels to trigger the modal dialogs asynchronously from the UI thread.
* **[PlaylistTrackViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/PlaylistTrackViewModel.cs)**:
  * Added a public `NotifyMetadataChanged` helper to raise UI property update notifications for changed metadata (Artist, Album, Genres, ReleaseDate).
* **[LibraryViewModel.Commands.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/LibraryViewModel.Commands.cs)**:
  * **Batch Tag Edit**: Opens the `BatchTagEditDialog`. On confirmation, updates the physical track files using TagLib# (for local, downloaded tracks) and updates both `PlaylistTracks` and `LibraryEntries` database entities in a background task before committing changes via `AppDbContext`. Finally, refreshes the ViewModels' state dynamically on the UI thread.
  * **Batch Add to Playlist**: Opens the `PlaylistPickerDialog`. If a new playlist name is specified, creates the playlist in the DB first via `CreateEmptyPlaylistAsync`. Otherwise, retrieves the selected existing playlist. Links all selected tracks by calling `AddTracksToProjectAsync` and triggers the collection update, then clears the current grid selection to transition the FAB out.

---

## 🛠️ Phase 6: Download Flow Diagnostics & Safety Hardening (Completed)

This phase addresses download blockages by relaxing over-aggressive network filters, mathematically inferring track bitrates from network metadata, and implementing a persistent per-track search/download audit logger linked directly to an in-app console panel.

### 1. Safety Filter & Bitrate Inference
* **[SoulseekAdapter.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/SoulseekAdapter.cs)**:
  * Added mathematical inference for unreported track bitrates (where network returns `0`). Calculated as: $\text{Bitrate (kbps)} = \frac{\text{Size (bytes)} \times 8}{\text{Length (seconds)} \times 1000}$.
  * Surface calculated bitrates to safety filters and scoring engines.
* **[SafetyFilterService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/SafetyFilterService.cs)**:
  * Modified `EvaluateCandidate` to bypass the `_lossyBlacklist` check when `allowLossy == true` (for MP3 fallbacks), and enforce a minimum standard of $\ge 256\text{ kbps}$ for fallback.
  * Ignored bitrate and sample rate gates for unreported attributes if they evaluate to zero/null.

### 2. Contextual Audit Logging Integration
* **[App.axaml.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/App.axaml.cs)**:
  * Restored standard search services (`ProtocolHardeningService`, `SearchNormalizationService`, `SafetyFilterService`, `SearchResultMatcher`, `AutoSearchService`) and registered `ITrackAuditLogger` singleton.
* **[DownloadManager.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DownloadManager.cs)**:
  * Injected `ITrackAuditLogger` to log lifecycle state transitions, duplicate/existing file reuses, verification events (format & size checks), and transfer failures.
* **[DownloadDiscoveryService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/DownloadDiscoveryService.cs)**:
  * Injected `ITrackAuditLogger` to log query dispatching, search tier initiations, peer rejections (blacklist/forensics/suspicious transcodes), and final best match selection.
* **[PostDownloadSpectralScanService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Services/PostDownloadSpectralScanService.cs)**:
  * Injected `ITrackAuditLogger` to log the post-download spectral check verdict (cutoff frequency, confidence level, and transcode status).

### 3. UI Real-time Terminal Tailing
* **[BlackBoxTerminalViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Diagnostics/BlackBoxTerminalViewModel.cs)**:
  * Implemented a background tailing task that streams and parses the track's log file (`TrackLogs/YYYY-MM/[Hash]_audit.log`) in real-time.
  * Dispatches updates safely to the UI thread using `Dispatcher.UIThread.Post`.
* **[ForensicLevelToColorConverter.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/Converters/ForensicLevelToColorConverter.cs)**:
  * Maps log levels/icons (`ERROR`, `WARN`, `ACCEPTED`, `REJECTED`, `SPECTRAL`, `INFO`) to themed Hex brushes.
* **[App.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/App.axaml) & [MainWindow.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/MainWindow.axaml)**:
  * Registered the converter and mapped the `BlackBoxTerminalViewModel` `DataTemplate` to the native `<controls:BlackBoxTerminal/>` component.
* **[OpenInspectorEvent.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Events/OpenInspectorEvent.cs)**:
  * Handled default header text `"SEARCH AUDIT LOG"` and `📝` icon.
* **[TrackOperationsViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Library/TrackOperationsViewModel.cs) & [UnifiedTrackViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/ViewModels/Downloads/UnifiedTrackViewModel.cs)**:
  * Exposed `OpenAuditLogCommand` that fires `OpenInspectorEvent` with the custom `BlackBoxTerminalViewModel`.
* **[TrackListView.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/TrackListView.axaml) & [DownloadsPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/ORBIT-Pure/Views/Avalonia/DownloadsPage.axaml)**:
  * Bound the command to the "Terminal: View Search Audit" context menu items.
