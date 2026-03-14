# QMUSICSLSK - Upgrade Roadmap

**Spotify-Like Music Player - Feature Implementation Plan**

---

## Current Status: 75% Complete ✅

### Recent Updates (February 24, 2026) - Major Intelligence & UI Consolidation
- ✅ **Phase 1: UI Consolidation**: Implemented Mini/Collapsed navigation states for `MainWindow.axaml` to give more screen real estate to the primary workspace.
- ✅ **Phase 1B: Contextual Sidebar Redesign**: Re-engineered the library side panel with folding navigation, dynamically adjusting to workspace selection, and resolving complex layout overflows and selection UI bugs.
- ✅ **Phase 1C: Vibe Radar Engine**: Developed a custom `VibeSidebarView.axaml` integrating an `SKCanvasView` to visualize nearby tracks via SkiaSharp rendering, hit-testing, and dynamically updated normalized mapping for Discovery.
- ✅ **Phase 2: Intelligence Refinement**: Adjusted `SonicMatchService` weights; transitioned from strict BPM to ML-driven 'Vibe Match' utilizing 512D Essentia profiles.
- ✅ **Phase 3: Discovery Bridge**: Built `ExternalDiscoveryService`, linking Spotify seeded recommendations and MusicBrainz producer lookups to the Similarity Sidebar.
- ✅ **Phase 5: Deep DNA Overhaul**: Wrote vector-length agnostic similarity logic and upgraded ML services (LightGBM/PersonalClassifier) to support 512-dimensional Essentia BLOBs.
- ✅ **Phase 5B: Background Engine Fixes**: Extensively debugged compile errors, `LibraryEntryEntity` metadata discrepancies, SQLite structural mismatches (such as `VocalDensity` constraints), and resolved an infinite layout loop crash.
- ✅ **Phase 6: Forensic Librarian**: Designed an Integrity Scan pipeline to detect up-converted transcodes and fake FLAC files via frequency cutoffs, establishing a "Report as Fraud" workflow.
- ✅ **Phase 7: Mission Control Overhaul**: Rebuilt the `DashboardSnapshot`, injected real-time SkiaSharp visualizations ("Genre Galaxy" constellations), and repaired syntax errors tying the dashboard to system health metrics.
- 🎯 **Hidden Potential Discovered**: Identified and logged several high-impact stubbed features for future development, including:
  - *Drop Detection Engine* (Automated climax location)
  - *Surgical Processing* (FFmpeg direct track editing)
  - *Rekordbox Phrase Parsing* (Binary extraction of Chorus/Bridge markers)
  - *Set Intelligence* (Harmonic flow-based playlist sorting)
  - *Monthly Drop Mission* (Spotify trending automation loop)
  - *Self-Healing Upgrades* (Automated FLAC replacement for low-quality MP3s)
- 🚀 **MIK-Killer UX Initiatives (Planned)**:
  - *X-Ray Sparkline* (Miniature energy contour graphs inside the Library rows)
  - *Mashup Lab* (Dual-deck sandbox in the sidebar for instant key-shifted stem mixing)
  - *Flow Builder* (Horizontal timeline playlist view with colored transition bridges)
  - *Semantic Waveform Zones* (Color-blocked waveforms based on musical phrasing)
  - *Automated Native Export* (Direct "Sync to USB" Rekordbox/Serato binary writing)

### Recent Updates (January 15, 2026)
- ✅ **High-Fidelity Playback**: Switched to WASAPI (Windows Audio Session API) for low-latency, stutter-free performance.

### Recent Updates (December 21, 2025)
### Recent Updates (December 21, 2025)
### Recent Updates (December 22, 2025)
- ✅ **Download Stability**: Fixed "Download Album" button binding and layout (CompactPlaylistTemplate).
- ✅ **API Rate Limits**: Fixed "Single-Item Batch" bug in Enrichment Orchestrator using Smart Buffer (250ms).
- ✅ **Streaming Import**: Implemented `IAsyncEnumerable` streaming for Spotify imports (50-track batches) for instant responsiveness.
- ✅ **Enrichment Control**: Added "Use Spotify API" toggle to dynamically enable/disable background metadata fetching.
- ✅ **Decoupling**: Ensured downloads continue gracefully even if Spotify enrichment fails (403/Auth errors).
- ✅ **Validation**: Sanitized Spotify IDs to reduce API errors.
- ✅ **API Reliability**: Implemented Global Circuit Breaker for Spotify 403 errors (5min backoff).
- ✅ **Soulseek Stability**: Fixed startup race condition by waiting for `LoggedIn` state.
- ✅ **Data Schema**: Added `ISRC` field to all relevant entities for cross-platform matching.
- ✅ **Ingestion Safety**: Implemented strict length validation (>= 20 chars) for Spotify IDs to prevent malformed imports (e.g. "64").

### Recent Updates (December 23, 2025)
- ✅ **Spotify Robustness (The Three Laws)**: Implemented chunking (50/100), two-pass fetching, and global `Retry-After` handling to prevent API bans.
- ✅ **Unified Import**: Fixed `SpotifyLikedSongsImportProvider` crash by implementing streaming interface.
- ✅ **Album Support**: Added native API support for `spotify:album:` imports (previously scraper-only/broken for auth users).
- ✅ **Efficiency**: Optimized metadata enrichment to skip tracks with existing data (BPM/Key).
- ✅ **Library UI Sync**: Fixed critical bug where new imports didn't appear in the list (ProjectListViewModel synchronization).

### Recent Updates (December 21, 2025)
- ✅ **Library & Import 2.0 Refinement**: Multi-select support, Floating Action Bar (FAB), side-filtering.
- ✅ **Performance**: Background DB checks, faster search rendering.
- ✅ **Search UI**: Implemented Bento Grid for albums and reactive status icons for tracks.
- ✅ **Library UI**: Fixed real-time status updates and bindings for track view.

### Recent Updates (December 25, 2025)
- ✅ **Phase 5A Complete**: Self-healing library with automatic quality upgrades.
- ✅ **Phase 5B Complete**: Rekordbox Analysis Preservation (RAP).
  - ✅ **ANLZ Parser**: Binary parsing of Rekordbox `.DAT`, `.EXT`, `.2EX` files.
  - ✅ **Waveform Visualization**: Custom `WaveformControl` support for Rekordbox PWAV data.
  - ✅ **XOR Descrambling**: Support for song structure phrase markers.
- ✅ **Sprint A: Background Enrichment**:
  - ✅ **Priority Worker**: Playlist tracks are enriched before global library.
  - ✅ **Unified features**: Fetching Energy, Danceability, Valence in 100-track batches.
  - ✅ **Event Messaging**: `IEventBus` integration for real-time UI updates.
- ✅ **Sprint B: High-Fidelity Player**:
  - ✅ **NAudio Backend**: Switched from LibVLC for better signal processing.
  - ✅ **VU Meters**: Real-time Left/Right peak monitoring.
  - ✅ **Pitch Control**: Hardware-style tempo adjustment (0.9x - 1.1x).
  - ✅ **Companion Probing**: Automatic discovery of Rekordbox metadata in secondary folders.
- ✅ **Metadata Persistence**:
  - ✅ **Upgrade Safety**: `MetadataCloner` now preserves musical intelligence during file swaps.
  - ✅ **Database Sync**: Synced Spotify features between `LibraryEntry` and `PlaylistTrack`.

### Recent Updates (December 26, 2025)
- ✅ **Search 2.0 (Phase 12.6) Complete**:
  - ✅ **Visual Scalability**: Multi-line row templates for dense metadata display.
  - ✅ **Smart Filters**: Bi-directional synchronization between Search Bar tokens and Filter HUD.
  - ✅ **Visual Hierarchy**: Gold/Silver/Bronze badges and heatmap opacity for search results.
- ✅ **Downloads UX**:
  - ✅ **Failure Visibility**: Errors like "No matches found" or "Timeout" clearly displayed in red.
  - ✅ **Force Retry**: Enabled "Retry" for stalled active downloads (Downloading/Queued states).
  - ✅ **Improved Context Menu**: Added Retry/Cancel options to the Failed tab.
- ✅ **Stability**: Fixed DI container crash (missing `AnlzFileParser`) and reset corrupt database.

### Recent Updates (December 28, 2025)
- ✅ **Search Intelligence (The Brain 2.0)**:
  - ✅ **Three-Zone Dashboard**: Initiator (Search), Brain (Rank/Filter), Gatekeeper (Results) layout.
  - ✅ **Control Surface**: Real-time Ranking Sliders (Bitrate vs Reliability vs Match) and Format Toggles (Auto-Saved).
  - ✅ **Search Presets**: One-click configuration for "Balanced", "DJ Mode", "Audiophile", and "Data Saver".
- ✅ **Library UX Polish**:
  - ✅ **Enhanced Track Cards**: Added real-time "Download Status" badges (Color-coded) and "Enrichment Sparkles".
  - ✅ **Integrity Badges**: Shield icons for "Gatekeeper" verification status.
  - ✅ **Mix Helper Integration**: Harmonic compatibility sidebar fully wired up.
- ✅ **Settings UI**:
  - ✅ **Layout Fixes**: Widened numeric input boxes for better visibility.
  - ✅ **Clarity**: Renamed "Quality Guard" to "Smart Features & Integrity" for better user understanding.
  - ✅ **Track Inspector Overhaul (Phase 4)**:
    - ✅ **Forensic UI**: Complete redesign with Hero section, Metrics Grid, and Forensic Logs.
    - ✅ **Interactive**: Added Force Re-analyze and Export Logs commands.
    - ✅ **Stability**: Fixed gradient crash.
  - ✅ **Analysis Queue Visibility (Phase 1-3)**:
    - ✅ **Status Bar**: Live bottom bar showing real-time analysis queue/progress.
    - ✅ **Album Priority**: Right-click "Analyze Album" calculates priority for entire albums.
    - ✅ **Event Bus**: Reactive updates for 100% UI consistency.

### Recent Updates (December 27, 2025) - Pivot to Local Analysis
- ⏸️ **PAUSED**: External Audio Features (Spotify BPM/Key/Energy) due to API changes.
- 🎯 **NEW FOCUS**: Local Audio Analysis First (FFmpeg/Essentia).
- 🎯 **NEW FOCUS**: Robust "Paste & Import" Workflow.
- ✅ **NEW FOCUS**: Repairing Spotify Metadata Enrichment Pipeline (6-Layer Architecture).
- ✅ **Queue Visibility**: Increased internal queue buffer from 100 to 5000.
- ✅ **Queue Visibility**: Increased internal queue buffer from 100 to 5000.
- ✅ **Concurrency Control**: Added dynamic slider (1-20).
- ✅ **Download Center Upgrade (Phase 2)**:
  - ✅ **Album Grouping**: Active downloads grouped by Project/Album.
  - ✅ **Group Controls**: Pause/Resume/Cancel All support.
  - ✅ **Visuals**: Aggregate progress bars and speed indicators.

### Recent Updates (December 30, 2025) - Code Polish & Integrity
- ✅ **Audio Integrity Badges**:
  - ✅ **Visualization**: Gold (🥇), Verified (🛡️), Suspicious (📉) badges in Library Grid.
  - ✅ **Data Model**: `IntegrityLevel` persisted in database.
  - ✅ **Inspector**: Frequency Cutoff display with "Native/Upscaled" context.
- ✅ **Rekordbox Integration (Phase 4 Complete)**:
  - ✅ **Export**: One-click "Export to Rekordbox XML" for playlists.
  - ✅ **Monthly Drop**: Automated "Last 30 Days" export.
- ✅ **Code Cleanup**: Removed unused `CloseInspector` residuals and hardened ViewModel commands.
- ✅ **Code Quality - Technical Review Fixes (Dec 30, 2025)**:
  - ✅ **CancellationToken Propagation**: Updated all analysis services to properly respect timeout logic
  - ✅ **UI Thread Safety**: Wrapped property updates in `Dispatcher.UIThread.Post` to prevent cross-thread exceptions

### Recent Updates (December 31, 2025) - Musical Brain Mission Control
- ✅ **Parallel Audio Analysis (Phase 1 Complete)**:
  - ✅ **SystemInfoHelper**: Auto-detection of optimal thread count based on CPU cores and RAM
  - ✅ **Multi-threaded Processing**: Implemented semaphore-limited parallelism with `Task.WhenAll`
  - ✅ **Progress Logging**: Throughput metrics (tracks/minute) with batch reporting
  - ✅ **Expected Performance**: 1.7x speedup on 4-core, 4.3x on 8-core, 11.2x on 16-core systems
- ✅ **Mission Control Dashboard (Phase 1A+1B Complete)**:
  - ✅ **Thread Activity Grid**: Live visualization of worker status with color coding (Processing/Complete/Error/Idle)
  - ✅ **Real-time Progress**: Per-thread progress bars and elapsed time tracking
  - ✅ **Metrics Dashboard**: Auto-calculating throughput (tracks/min), total processed, and ETA with smart formatting
  - ✅ **StatusToColorConverter**: Visual status differentiation (Green/Orange/Gray/Red)
- ✅ **Musical Brain Test Mode**:
  - ✅ **Automated Diagnostics**: Pre-flight checks for FFmpeg, temp directory, database, and queue functionality
  - ✅ **Test Track Selection**: Random sampling of 10 library tracks with valid file paths
  - ✅ **One-Click Testing**: "🧠 Test Brain" button for end-to-end pipeline validation
  - ✅ **Live Monitoring**: Integration with Mission Control dashboard for real-time test feedback

### Recent Updates (January 1, 2026) - Build Stabilization & Forensic Stream
- ✅ **Build Pipeline Restoration**:
  - ✅ **Dependency Resolution**: Fixed 25+ build errors across `SonicIntegrityService`, `EssentiaAnalyzerService`, and `DropDetectionEngine`.
  - ✅ **Service Hardening**: Corrected `IForensicLogger` interface usage and argument mismatches.
  - ✅ **Missing Components**: Restored missing fields (`_logger`, `_ffmpegPath`, `_isDisposing`) ensuring stable service lifecycle.
- ✅ **Live Forensic Stream**:
  - ✅ **Real-Time Logs**: Implemented `ObservableCollection` log stream in `AnalysisQueueViewModel`.
  - ✅ **Thread Safety**: Integrated `RxApp.MainThreadScheduler` for safe UI updates from background threads.

### Recent Updates (January 2, 2026) - The Style Lab (Sonic Taxonomy)
- ✅ **Phase 15: Style Lab Complete (ML.NET Upgrade)**:
  - ✅ **Core Infrastructure**: `PersonalClassifierService` (ML.NET/LightGBM).
  - ✅ **Style Lab UI**: Dedicated management view with Drag & Drop training bucket logic.
  - ✅ **Library Integration**: Dynamic filter chips in Library headers for instant style-based filtering.
  - ✅ **Database Schema**: Added `DetectedSubGenre` to `TrackEntity`, `PlaylistTrack`, and `AudioFeaturesEntity`.
  - ✅ **Build Resiliency**: Fixed namespace and binding errors across the stack.

### Recent Updates (January 4, 2026) - Database & AI Maintenance
- ✅ **Database & Maintenance**: Added "Reset Library" functionality to wipe data and re-initialize a fresh schema from Settings.
- ✅ **Spotify Refresh**: Implemented `ForceRefresh` mechanism to bypass the local metadata cache for fresh data during imports.
- ✅ **AudioGuard Quality Icons**: Enhanced track list UI with visual badges (💎, ✅, ⚠️, ❌) based on bitrate and format.
- ✅ **Audio Analysis Fix**: Refactored Essentia integration to use real machine learning data (RMS/BPM Confidence) instead of hardcoded heuristics.
- ✅ **Integrity Persistence**: Fixed the "Verified" badge to correctly update its status if the physical file is manually deleted.
- ✅ **Path Harmonization**: Standardized database storage path to `ORBIT` across all services to prevent data fragmentation.
- ✅ **Phase 10.2: Interactive Waveform & UI (Complete)**:
  - ✅ **Database Schema**: Fixed migration issues, created `TechnicalDetails` table for heavy analysis data.
  - ✅ **Interactive Cues**: Implemented draggable cue markers in `WaveformControl` with magnetic snapping logic foundation.
  - ✅ **Preparation UI**: Added "Ambiance Detail" expanded view, cue management list, and "Save All" persistence.
  - ✅ **Tri-Band Visualization**: Layered RGB waveform rendering for deep forensic insight into bass/mids/highs.
- ✅ **Phase 10.3: File Mobility (The Universal Tagger)**:
  - ✅ **Serato Injection**: Implemented `SeratoMarkerService` to write proprietary `GEOB` tags (Version 2.4).
  - ✅ **Safety First**: Integrated `SafeWriteService` for atomic, corruption-proof tagging.
  - ✅ **Build Stabilization**: Resolved critical `FontFamily` and EF Core build errors.

### Recent Updates (January 6, 2026) - Stabilization & Integrity
- ✅ **Clean Build Restoration**: Resolved 11+ build errors and 27+ warnings. Achieving 0 errors and 26 style warnings.
- ✅ **Industrial Prep Hardening**: Refactored `ManualCueGenerationService` for public consumption and integrated internal cue mapping.
- ✅ **DI Orchestration**: Corrected service registrations for `SonicIntegrityService` (concrete) and `PersonalClassifierService` (proper namespace).
- ✅ **Tagging Reliability**: Fixed `SeratoMarkerService` ID3v2 frame creation using robust `GeneralEncapsulatedObjectFrame.Get` patterns.
- ✅ **Navigation Stability**: Disambiguated `MainViewModel` property calls to restore Forensic Lab navigation.

### Recent Updates (January 7, 2026) - The Bird's Eye UI (Phase 3)
- ✅ **Library UI Refactor (Phase 3 Complete)**:
  - ✅ **StandardTrackRow**: Unified track visualization with "Traffic Light" quality indicators (Green/Blue/Orange/Red).
  - ✅ **Metadata Pills**: Styled badges for BPM, Key, Genre, and Year using `Border` elements ("Pills").
  - ✅ **Integrity Badges**: "Shield" icons and glows for verified forensic data.
  - ✅ **Layout Restoration**: Fixed critical XAML corruption ("Grid mismatch") restoring the 5-column layout (Left, List, Right, History, Overlays).
- ✅ **Build Remediation**:
  - ✅ **XAML Fix**: Resolved persistent `AVLN1001` Grid parsing error in `LibraryPage.axaml`.
  - ✅ **C# Polishing**: Removed duplicate properties in `PlaylistTrackViewModel` and fixed `ActiveWorkspace` enum usage.
  - ✅ **Stable Build**: Application now compiles cleanly (0 Errors).

### Recent Updates (January 8, 2026) - UI & Stability Fixes
- ✅ **Forensic Lab UI Fix**:
  - ✅ **Refactoring**: Decoupled `ForensicLabDashboard` overlay from the main content grid in `LibraryPage.axaml` to resolve persistent blur issue.
  - ✅ **UX Improvement**: Added a dedicated Loading Screen with progress bar to the dashboard during analysis.
- ✅ **Analysis Stability**:
  - ✅ **Timeout Adjustment**: Increased Essentia analysis timeout from 45s to 120s in `EssentiaAnalyzerService.cs` to prevent "SIDECAR TIMEOUT" on longer tracks.
  - ✅ **Bug Fix**: Resolved `CubicOut` easing exception via clean build.

### Recent Updates (January 8, 2026) - Reliability & Transparency Wave
- ✅ **Phase 10.5: Native Dependency Health (Complete)**:
  - ✅ **Proactive Service**: Created `NativeDependencyHealthService` to verify `ffmpeg` and `essentia` binaries on startup.
  - ✅ **Startup Integration**: Automated checks during app launch with warning logs for missing tools.
  - ✅ **Graceful Degradation**: `TrackOperationsViewModel` and `StatusBarViewModel` disable analysis features if dependencies are missing.
  - ✅ **Reactive UI**: Status bar shows "⚠️ Repair Required" if tools are missing (Event-driven updates).
- ✅ **Diff View Backend (Phase 10.5)**:
  - ✅ **Data Model**: Added `CurationConfidence` and `Source` to `PlaylistTrack` and `AudioFeaturesEntity`.
  - ✅ **Inspector Logic**: Implemented `HasBpmMismatch`, `HasKeyMismatch`, and `DiffBpmLabel` logic in `TrackInspectorViewModel`.

### Recent Updates (January 10, 2026) - Smart Playlists 2.0
- ✅ **Phase 20: Smart Playlists (Complete)**:
  - ✅ **Smart Engine**: Created `SmartPlaylistService` and `SmartPlaylistCriteria` for dynamic track evaluation.
  - ✅ **Database Support**: Added `IsSmartPlaylist` and `SmartCriteriaJson` columns to `Projects` table.
  - ✅ **Creation UI**: Implemented `CreateSmartPlaylistDialog` with Energy, BPM, and Genre filters.
  - ✅ **Persistence**: Verified robust saving and loading of smart criteria via `DatabaseService` and `LibraryService`.
  - ✅ **UI Integration**: Added "New Smart Playlist" entry point to Library Tools menu.

---

## 📊 CODE QUALITY REVIEW FINDINGS (December 30, 2025)

> [!IMPORTANT]
> Systematic code review identified critical issues across 5 quality dimensions. Items marked 🔴 CRITICAL require immediate attention.

### 🔴 Critical Issues (Must Fix)

#### 1. Memory Management
- [ ] **LibraryViewModel Memory Leak** 🔴 CRITICAL
  - **Issue**: `LibraryViewModel.cs` subscribes to `IEventBus` events but never unsubscribes
  - **Impact**: ViewModels stay in memory after navigation, causing memory leaks in long sessions
  - **Fix**: Implement `IDisposable`, track subscriptions, unsubscribe in `Dispose()`
  - **File**: `ViewModels/LibraryViewModel.cs`
  - **Estimated Time**: 2 hours

- [ ] **AnalysisWorker Unmanaged Resource Cleanup** ⚠️ HIGH
  - **Issue**: Relies on `IServiceScope` disposal for Essentia C++ wrappers
  - **Impact**: Unmanaged memory may not be released promptly
  - **Fix**: Explicit disposal logic in worker block
  - **File**: `Services/AnalysisQueueService.cs`
  - **Estimated Time**: 1 hour

#### 2. Scalability & Performance
- [ ] **UI Virtualization Missing** 🔴 CRITICAL
  - **Issue**: `ObservableCollection<T>` loads all tracks into memory (10k+ tracks = massive lag)
  - **Impact**: UI freezes with large libraries, scrolling is janky
  - **Fix**: Implement `ISupportIncrementalLoading` or reactive virtualization
  - **File**: `ViewModels/LibraryViewModel.cs`
  - **Estimated Time**: 6 hours
  - **Note**: Already planned in Phase 0A.1 (line 658)

#### 3. External Service Resilience
- [ ] **Spotify API Circuit Breaker** 🔴 CRITICAL
  - **Issue**: No circuit breaker or retry policy for Spotify API
  - **Impact**: HTTP 429/500 errors fail immediately, hammer service during outages
  - **Fix**: Implement Polly retry policy with exponential backoff
  - **File**: `Services/ImportProviders/SpotifyImportProvider.cs`
  - **Estimated Time**: 3 hours
  - **Dependencies**: Install `Microsoft.Extensions.Http.Polly` NuGet package

### ⚠️ High-Priority Issues

#### 4. Async Best Practices
- [ ] **ConfigureAwait(false) Missing** ⚠️ HIGH
  - **Issue**: Service layer awaits without `.ConfigureAwait(false)`
  - **Impact**: Continuations capture UI context unnecessarily, burdening dispatcher
  - **Fix**: Add `.ConfigureAwait(false)` to all library code (non-ViewModel awaits)
  - **Files**: `Services/DownloadManager.cs`, `Services/AnalysisQueueService.cs`, `Services/DatabaseService.cs`
  - **Estimated Time**: 2 hours
  - **Impact**: 10-20% performance improvement in high-concurrency scenarios

- [ ] **Spotify API Timeout Missing** ⚠️ MEDIUM
  - **Issue**: `SpotifyImportProvider.cs` has no visible timeout for HTTP requests
  - **Impact**: Can hang indefinitely on slow/stalled connections
  - **Fix**: Configure `HttpClient` timeout in DI registration
  - **File**: `App.axaml.cs` or `Services/SpotifyImportProvider.cs`
  - **Estimated Time**: 30 minutes

#### 5. Error Handling & UX
- [ ] **Technical Error Messages to Users** ⚠️ MEDIUM
  - **Issue**: Messages like `DownloadFailureReason.AtomicRenameFailed` bubble to UI
  - **Impact**: Confusing error messages for non-technical users
  - **Fix**: Create user-friendly message mapper (`ErrorMessageFormatter.cs`)
  - **Estimated Time**: 2 hours

### 🟡 Low-Priority Improvements

#### 6. Testing Infrastructure
- [ ] **Complete Absence of Tests** 🟡 TECHNICAL DEBT
  - **Issue**: No `Tests` project or unit/integration tests
  - **Impact**: Regression risk, difficult to refactor with confidence
  - **Fix**: Create test project, add critical path tests (download, import, analysis)
  - **Estimated Time**: 16+ hours (iterative)
  - **Note**: Defer to post-v1.0 stabilization

---

### ✅ Already Fixed (This Session)
- ✅ **CancellationToken Propagation** (Dec 30, 2025)
  - Fixed `AnalysisQueueService` to pass `linkedCts.Token` to all analysis methods
  - 60-second timeout now properly cancels FFmpeg/Essentia processes
- ✅ **Analysis Event Throttling** (Already implemented at 250ms)
- ✅ **UI Thread Safety** (Dec 30, 2025)
  - Wrapped `OnPropertyChanged` in `Dispatcher.UIThread.Post`

### 📋 Quality Metrics Summary

| Category             | Rating          | Status                   |
| -------------------- | --------------- | ------------------------ |
| Async/Await Patterns | ⚠️ Partial       | 2 issues remaining       |
| Resource Management  | ⚠️ Partial       | 2 critical leaks         |
| Error Handling       | ⚠️ Partial       | Missing circuit breakers |
| UI Responsiveness    | ⚠️ Partial       | Virtualization needed    |
| Testing              | ❌ Non-Compliant | No test suite            |

**Overall Code Quality**: **72/100** (Good foundations, needs polish)

---

## 🎯 ORBIT v1.0: 8-Week Stabilization Focus (STRATEGIC PRIORITY)

**Source**: ORBIT v1.0 Complete Development Strategy  
**Decision**: **PAUSE NEW FEATURES for 8 weeks** to focus on reliability and speed.

> [!IMPORTANT]
> **Month 1 (Weeks 1-4)**: Operational Resilience - Fix download reliability, prevent data loss  
> **Month 2 (Weeks 5-8)**: Speed & UX - Optimize database, UI virtualization, perceived performance

**Rationale**: Early adopters (DJ and audiophile communities) must not encounter a buggy experience that could damage project reputation. Build trust through reliability before adding features.

---

## Phase 0: Critical Operational Resilience (Weeks 1-4) 🔴⭐ HIGHEST PRIORITY

### 0.1 Crash Recovery Journal (8 hours) - NEW ⭐⭐⭐ CRITICAL
**Problem**: Power loss or crashes during downloads cause orphaned files, lost state, and duplicate downloads.  
**Solution**: Lightweight transaction log that enables automatic recovery on startup.

**Implementation**:
- [ ] Create `Services/CrashRecoveryService.cs`
- [ ] Add `CrashRecoveryJournal` SQLite table or JSON file
- [ ] Log active downloads with checkpoint data (bytes written, .part file paths, pending operations)
- [ ] Implement startup recovery logic:
  - Detect orphaned `.part` files
  - Resume interrupted downloads from last checkpoint
  - Rollback incomplete metadata writes
  - Clean up corrupted state
- [ ] Integrate with `DownloadManager` to write checkpoints every 5 seconds during active downloads
- [ ] Add `App.axaml.cs` startup hook to call `CrashRecoveryService.RecoverAsync()`

**Journal Schema**:
```json
{
  "session_id": "abc123",
  "active_downloads": [
    {
      "track_id": "xyz",
      "state": "downloading",
      "bytes_written": 2457600,
      "part_file": "C:\\temp\\track.mp3.part",
      "last_checkpoint": "2025-12-24T00:10:00Z"
    }
  ],
  "pending_metadata_writes": [...]
}
```

**Impact**: Prevents 100% of data loss from crashes. Professional-grade resilience.

---

### 0.2 SafeWrite Wrapper (4 hours) - NEW ⭐⭐⭐ CRITICAL
**Problem**: Direct file writes can corrupt files during power loss or app crashes.  
**Solution**: Reusable utility that ALL file-writing services use for atomic operations.

**Implementation**:
- [ ] Create `Utils/SafeWrite.cs` with atomic write pattern:
  ```csharp
  public static class SafeWrite
  {
      public static async Task<bool> WriteAtomicAsync(
          string finalPath, 
          Func<string, Task> writeAction,
          Func<string, Task<bool>>? verifyAction = null)
      {
          var tempPath = $"{finalPath}.part";
          // 1. Write to temp file
          // 2. Verify (optional)
          // 3. Atomic rename
      }
  }
  ```
- [ ] Refactor `DownloadManager` to use `SafeWrite.WriteAtomicAsync()`
- [ ] Refactor `MetadataTaggerService` to use SafeWrite for tag writes
- [ ] Refactor `DatabaseService.BackupAsync()` to use SafeWrite
- [ ] Refactor `ArtworkCacheService` to use SafeWrite for image downloads

**Impact**: Prevents file corruption even during power loss. Used by 4+ critical services.

---

### 0.3 Download Health Monitor (6 hours) - ENHANCE EXISTING ⭐⭐⭐ CRITICAL
**Current**: Basic stall detection exists (line 103-107)  
**Enhancement**:- [x] **Phase 3C: Advanced Queue Orchestration**
  - [x] **Multi-Lane Priority**: High (User) vs Low (Background/Import) lanes.
  - [x] **Lazy Hydration**: Only hydrate 50 tracks to memory.
  - [x] **Threshold Trigger**: Start download if score > 92%.
  - [x] **Speculative Start**: Start silver match (>70%) after 5s timeout.
  - [x] **Project Prioritization**: "VIP Pass" logic (P1-P10).
  - [x] **Phase 3C.3: UI Integration ("Air Traffic Control")**
    - [x] **Refactor `DownloadsViewModel`**
        - [x] Use `DynamicData` for reactive sorting/filtering (SourceCache).
        - [x] Create three `ObservableCollection`s (Express, Standard, Background) derived from source.
    - [x] **Implement "Grouped" View (`DownloadsPage.axaml`)**
        - [x] **Album-Centric Grouping**: Group actively downloading tracks by `PlaylistId`.
        - [x] **Aggregate Progress**: Show album-level progress bar and speed.
        - [x] **Group Controls**: Pause/Resume/Cancel entire albums at once.
        - [x] **Singles Handling**: Fallback to "Singles & Ad-Hoc" group.
    - [x] **"VIP Pass" Command**
        - [x] Add ContextMenu option to promote track to Express Lane (force Priority 0).
    - [ ] **Hardening**
        - [ ] Performance test with 100+ items.
        - [x] Verify "Speculative Start" visual feedback (Pulsing Silver Badge).
        - [x] **Demo Polish**:
            - [x] Express Lane Gold Borders/Styling.
            - [x] "Intelligence Breakdown" Tooltips on hover.
- [x] **Phase 0A: Mission Control Dashboard & System Health (Completed Dec 2025)**
  - [x] **"Bento Box" Layout**: Glassmorphism tiles for metrics.
  - [x] **Active Missions Widget**: Live visuals for Express/Standard/Background lanes.
  - [x] **System Health Widget**: "Dead Letter" monitoring with Auto-Recover button.
  - [x] **Library Purity**: Gold/Silver/Bronze stats overview.
  - [x] **Real-Time Feed**: Throttled (4FPS) "Live Operations" feed using `ItemsRepeater`.
  - [x] **Resilience Log**: Atomic operations history (Recovery/Zombies) visualization.
- [x] **Phase 6: UX Enhancements (2025/2026 Trends)**
  - [x] **Vibe Search**: Natural language parsing (e.g., "flac >320").
  - [x] **Skeleton Screens**: Shimmer-animated placeholders for perceived performance.
  - [x] **DataTemplateSelector**: Intelligent switching between skeleton/hydrated states.
  - [ ] **Advanced Search Features**:
    - [ ] BPM/Energy/Mood tagging support.
    - [ ] Genre Galaxy visualization.
  - [x] **Phase 1: Playback Polish & Queue** <!-- id: 9 -->
    - [x] Implement Queue Persistence (`QueueItemEntity`) <!-- id: 10 -->
    - [/] Implement `NowPlayingPage.axaml` (High-Fi UI - Waveform deferred) <!-- id: 11 -->

- [x] **Phase 2: Search Transparency** <!-- id: 12 -->
    - [x] Implement Score Breakdown Tooltips in Search <!-- id: 13 -->
  - [ ] **Tactile Interactions**:
    - [ ] Advanced cursor feedback for VIP actions.
    - [ ] Kinetic typography for live status.
- [x] **Phase 7: Rekordbox Integration (DJ Hardware Ready)**
  - [x] **Atomic XML Writes**: Temp file + rename pattern to prevent corruption.
  - [ ] **Settings Page Hardening**:
    - [ ] Interactive path validation with LED status.
    - [ ] USB path translation UI (C:/ → /Volumes/USB/).
    - [ ] Auto-Export Watcher toggle.
  - [ ] **Library Sync Status**:
    - [ ] Rekordbox Status Column (🔵 Synced, ⚪ Pending, 🔴 Missing).
    - [ ] One-Click "Crate" Creator.
  - [ ] **Pro Hardware Refinements**:
    - [ ] Camelot key display verification.
    - [ ] Bitrate color-coding (Green/Gold/Red).
- [x] **Phase 8: Harmonic Matching ("Works Well Together")**
  - [x] **HarmonicMatchService**: Camelot Wheel theory implementation.
  - [x] **Key Compatibility**: Perfect, Compatible, Relative major/minor detection.
  - [x] **BPM Matching**: ±6% beatmatching range.
  - [x] **Scoring Algorithm**: 0-100 compatibility score (Key: 50pts, BPM: 30pts, Energy: 20pts).
  - [x] **Command Implementation**: FindHarmonicMatchesCommand in LibraryViewModel.
  - [x] **Phase 9: Mix Helper Sidebar**: Real-time harmonic suggestions with debounced selection (250ms).
  - [ ] **UI Integration**:
    - [ ] Library TreeDataGrid context menu: "Find Harmonic Matches".
    - [x] "Mix Helper" panel for seed track visualization.
    - [ ] Smart Playlist Generator.
  - [ ] **Advanced Features**:
    - [ ] Spotify Energy/Valence integration.
    - [ ] Caching for performance optimization.
- [x] **Hardening**: Lazy Hydration (Waiting Room Pattern for 2k+ queues)`Services/DownloadHealthMonitor.cs`
- [ ] Track health metrics per download:
  - Stall count
  - Failure count
  - Average speed
  - Peer response time
- [ ] Implement automatic actions:
  - **On Stall** (2+ stalls): Switch to next best candidate automatically
  - **On Repeated Failure** (3+ failures from same peer): Blacklist peer for 1 hour
  - **On Slow Download** (<10KB/s for 30s): Switch to faster peer
- [ ] Add UI notifications: "Switched to faster source (250ms response)"
- [ ] Integrate with existing stall detection in `SoulseekAdapter.cs`

**Impact**: Makes ORBIT feel *smart* and *self-correcting*. Zero manual intervention required.

---

### 0.4 SQLite WAL Mode + Index Audit (2 hours) - ENHANCE EXISTING ⭐⭐ HIGH
**Current**: 6 performance indexes exist (ROADMAP line 29)  
**Enhancement**: Enable Write-Ahead Logging for better concurrency

**Implementation**:
- [ ] Update `AppDbContext.cs` or `DatabaseService.cs`:
  ```csharp
  protected override void OnConfiguring(DbContextOptionsBuilder options)
  {
      var connection = new SqliteConnection(connectionString);
      connection.Open();
      
      // Enable WAL mode
      var cmd = connection.CreateCommand();
      cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
      cmd.ExecuteNonQuery();
      
      options.UseSqlite(connection);
  }
  ```
- [ ] Add `DatabaseService.AuditIndexesAsync()` method to detect missing indexes
- [ ] Run audit on app startup (dev builds only)

**Benefits**: Writers don't block readers, faster commits, better UI responsiveness.

---

### 0.5 Atomic File Operations Pipeline (4 hours) - NEW ⭐⭐⭐ CRITICAL
**Problem**: Current "fire-and-forget" download model has no transaction safety.  
**Solution**: Implement `.part` file workflow with verification before final rename.

**Implementation**:
- [ ] Update `DownloadManager.ProcessTrackWithFolderLogicAsync()`:
  - Download to `{filename}.part` instead of final name
  - Verify file integrity (size check, format validation)
  - Atomic rename from `.part` to final filename
  - Delete `.part` file on failure
- [ ] Add resume capability:
  - Check for existing `.part` files before starting download
  - Resume from last byte position (if Soulseek protocol supports)
- [ ] Integrate with Crash Recovery Journal for checkpoint tracking

**Impact**: Enables resumable downloads, prevents corruption on crashes/WiFi drops.

---

### 🚨 Technical Debt & Stability (Pending FIX)
- [ ] **N+1 Query Pattern Risk**: Refactor project loading to use eager loading (.Include) for track counts to prevent performance degradation.
- [ ] **Soft Deletes & Audit Trail**: Implement `IsDeleted` and `DeletedAt` for imports to allow recovery and history tracking.
- [ ] **Status Management Standardization**: Create a centralized `StatusConverter` to map DB strings to internal enums consistently.
- [ ] **Intelligent Download Hub (Bento Refactor)**:
  - [ ] Implement `DownloadAnalyticsService` for Global ETA/Speed windowing.
  - [ ] Refactor `DownloadsPage.axaml` with Bento Grid header (Speed, ETA, Storage, Health).
  - [ ] Integrate micro-thumbnails (64px) into `DownloadItemViewModel`.
  - [ ] Add Disk-I/O bottleneck detection warnings.
- [ ] **Real-time Deduplication Sync**: Ensure `PlaylistTrack` and `LibraryEntry` states stay in sync immediately upon download failure/success.
- [ ] **Library Resilience**: Implement automated daily backups of `%appdata%/SLSKDONET/library.db`.
- [ ] **Batch Duplicate Fix**: Update `DownloadManager.QueueProject` to check against `addedInBatch` set during hash check loop.
- [ ] **UI Thread Safety**: Wrap `PlaylistTrackViewModel` property updates (via event bus) in `Dispatcher.UIThread.Post`.
- [ ] **Coordinate Precision**: Refactor `LibraryPage.axaml.cs` drag-drop to use `VisualRoot` / `PointToClient` for transformations.
- [ ] **Selection Robustness**: Replace `Task.Delay` in `LibraryViewModel` with a reactive "Wait until Project exists in collection" logic.
- [ ] **Source of Truth Sync**: Update `TrackListViewModel` to cross-reference `DownloadManager` for active tracks not yet in global index.
- [ ] **Shared Folder Logic**: Implement backend logic in `SoulseekAdapter` to actually share files from the configured Shared Folder (currently UI-only).

### 🎯 Metadata Gravity Well 2.0 - Refinements (SpotiSharp Integration)
**Source**: Deep dive analysis comparing ORBIT implementation with SpotiSharp reference architecture

#### Phase 1: Authentication & Connection (HIGH Priority)
- [x] ~~Implement `GetAuthenticatedClientAsync()` with proper PKCE authentication~~ (COMPLETE - Dec 22, 2025)
- [x] ~~Use `PKCEAuthenticator` for automatic token refresh~~ (COMPLETE - Dec 22, 2025)
- [x] ~~Fix Soulseek race condition: `LoggedIn` vs `Connected` check~~ (COMPLETE - Dec 22, 2025)
- [ ] **Ready Event Pattern**: Implement "Ready" event in `SoulseekAdapter` that `DownloadManager` listens for before popping tracks from queue.

#### Phase 2: Intelligence & Enrichment (HIGH Priority)
- [ ] **SpotifyUriResolver**: Create `Utils/SpotifyUriResolver.cs` with Regex to identify Track/Album/Playlist IDs from various formats.
  - Handle `open.spotify.com/track/...` mobile share links
  - Handle `spotify:track:...` URI strings  
  - Generic "Intake" flexibility for user input
- [ ] **Native Batch Enrichment**: Refactor `SpotifyMetadataService.cs` to use native library for ISRC.
  - Use `client.Tracks.GetSeveral(new TracksRequest(ids))` to capture ISRC field
  - Already using `client.Tracks.GetSeveralAudioFeatures()` for BPM/Key ✅
- [ ] **Smart Duration Gating**: Implement `SearchResultMatcher.cs` fuzzy duration logic.
  - If Spotify says 210s, create "Success Range" of 207-213s
  - Score 1.0 for in-range, 0.0 for outliers (e.g., 8min Extended Mix)
  - Prevents false matches on remixes/radio edits

#### Phase 3: File Integrity & Normalization (MEDIUM Priority)
- [ ] **Filename Normalization Service**: Create `Utils/FilenameNormalizer.cs`.
  - Strip illegal OS characters: `\ / : * ? " < > |`
  - Remove redundant "feat." tags from Title if already in Artist field
  - Implement `Cleanup()` method for artist names (SpotiSharp pattern)
- [ ] **FFmpeg Progress Feedback**: Enhance `SonicIntegrityService.cs` with real-time progress.
  - Capture `StandardError` stream during FFmpeg analysis
  - Parse `time=` output to calculate percentage
  - Fire `TrackProgressChangedEvent` for Downloads Page progress bar

#### UX Polish & Reliability (MEDIUM Priority)
- [ ] **Downloads Page Hydration**: Fix "Empty Downloads Page" bug.
  - On `DownloadCenterViewModel.Initialize()`, query database for current state:
    ```csharp
    ActiveDownloads = _db.Tracks.Where(t => t.State == Downloading).ToList();
    ```
  - Ensures UI reflects DB state, not just live events
- [ ] **Deterministic Track ID**: Implement hash-based deduplication.
  - Generate `TrackEntity.Id` using hash of ISRC (if available) or `artist-title` lowercase string
  - Prevents duplicate downloads across multiple playlist jobs
- [ ] **Circuit Breaker UI Indicator**: Add `_isSpotifyServiceDegraded` flag.
  - Set flag on 403 errors instead of just breaking loop
  - Bind "Warning Icon" in header to show "Spotify Sync Unavailable" status
  - User feedback instead of silent failures

  - User feedback instead of silent failures

---

## 🎯 "Biggers App" Search Architecture Refactoring (5-Week Initiative) - NEW STRATEGIC PRIORITY

**Philosophy Shift**: From "Everything gets a score" → "Filter strict trash, then rank by tier"  
**User Control**: From complex sliders → Clear intent-based strategies  
**Goal**: Eliminate false positives, magic numbers, and provide deterministic decision-making

### The Two-Stage Pipeline Architecture

#### Stage 1: The Gatekeeper (Strict Filtering)
- [ ] **Create `Services/SafetyFilterService.cs`** (Week 2)
  - [ ] **Integrity Gate**: Reject fake FLAC/upconverted MP3 (port `SonicIntegrityService` logic)
  - [ ] **Duration Gate**: `Math.Abs(candidate.Length - target.Length) > DurationTolerance` → REJECT
  - [ ] **Strict Token Match**: ALL query tokens must be present in filename (slsk-batchdl method)
  - [ ] **Ban List Gate**: Check `BlacklistedUsers` → REJECT
  - [ ] Unit test with messy filenames and edge cases

- [ ] **Implement `Utils/TokenMatcher.cs`** (Week 2)
  - [ ] Normalize query and filename (lowercase, strip punctuation, remove tags `[320]`, `(Official)`)
  - [ ] Split into token sets
  - [ ] Verify token intersection (ALL query tokens present)
  - [ ] Handle Unicode normalization (dashes, quotes)

#### Stage 2: Tiered Comparer (Ranking)
- [ ] **Create `Services/TieredTrackComparer.cs`** (Week 3)
  - [ ] **Tier 1 - Primary Goal**:
    - `QualityFirst`: `BitrateGap > SignificantBitrateGap` → Pick higher bitrate
    - `DjReady`: Has BPM/Key metadata? → Pick it → Else check quality
  - [ ] **Tier 2 - Reliability & Speed**:
    - Free slot available? → Instant win
    - `QueueGap > SignificantQueueGap` → Pick smaller queue
  - [ ] **Tier 3 - Polish (Tie-Breakers)**:
    - Format preference (FLAC over MP3 if user prefers)
    - Filename aesthetics (cleaner filename wins)

### New Data Structures

- [ ] **Define `Models/SearchPolicy.cs`** (Week 1)
  - [ ] `SearchPriority` enum (`QualityFirst`, `DjReady`)
  - [ ] Boolean modifiers (`PreferSpeedOverQuality`)
  - [ ] Safety gates (`EnforceFileIntegrity`, `EnforceStrictTitleMatch`, `EnforceDurationMatch`)
  - [ ] Thresholds (`DurationToleranceSeconds`, `SignificantBitrateGap`, `SignificantQueueGap`)

- [ ] **Create `Services/ConfigMigration.cs`** (Week 1)
  - [ ] Detect old `ScoringWeights` configuration
  - [ ] Heuristic mapping to `SearchPolicy`:
    - High `MusicalWeight` → `Priority = DjReady`
    - High `AvailabilityWeight` → `PreferSpeedOverQuality = true`
    - Else → `Priority = QualityFirst`
  - [ ] Archive old weights, persist new policy
  - [ ] Ensure backward compatibility

### UI Redesign

- [ ] **Delete "Mixing Desk" UI** (Week 4)
  - [ ] Remove `Advanced Ranking & Tuning` section from `SettingsPage.axaml`
  - [ ] Delete sliders for Musical/String/Availability/Quality weights
  - [ ] Remove "Live Preview" scoring points display

- [ ] **Create "Strategy Command" Card** (Week 4)
  - [ ] **Primary Goal Dropdown**:
    - "Audiophile (Quality First)" - Strict bitrate/lossless focus
    - "DJ Mode" - Prioritize BPM/Key metadata
  - [ ] **Behavior Modifiers Section**:
    - "Prefer Speed" toggle with tooltip explaining tolerance widening
  - [ ] **Safety Gates Section** (all default ON):
    - "Strict Name Matching" - Only download files with all query words
    - "Verify Integrity" - Reject fake/upscaled files
    - "Verify Duration" - Reject remixes/edits with different lengths

### Integration & Refactoring

- [ ] **Refactor `Services/SearchOrchestrationService.cs`** (Week 4)
  - [ ] Insert `SafetyFilterService.Filter()` before ranking
  - [ ] Replace `ResultSorter` with `TieredTrackComparer`
  - [ ] Remove all `SortingCriteria` and weight-based code
  - [ ] Update call sites to use new pipeline

- [ ] **Update `Configuration/AppConfig.cs`** (Week 1)
  - [ ] Add `SearchPolicy` property
  - [ ] Deprecate `ScoringWeights` (keep for migration)
  - [ ] Update serialization logic

### Testing & Validation

- [ ] **Side-by-Side Comparison Tests** (Week 5)
  - [ ] Test case: "Strobe - Deadmau5" (compare old vs new results)
  - [ ] Test case: "Contact - Daft Punk" (verify token matching)
  - [ ] Test case: Fake FLAC detection (ensure instant rejection)
  - [ ] Test case: Extended Mix vs Radio Edit (duration filter)

- [ ] **Migration Testing** (Week 5)
  - [ ] Verify settings don't get lost during migration
  - [ ] Test heuristic mapping accuracy with sample configs
  - [ ] Ensure UI reflects migrated policy correctly

### Implementation Timeline

- **Week 1**: Foundation
  - Define `SearchPolicy` class and enums
  - Create `ConfigMigration` service
  - Update `AppConfig` schema

- **Week 2**: The Gatekeeper
  - Implement `SafetyFilterService`
  - Port `SonicIntegrityService` into synchronous filter
  - Implement `TokenMatcher` with unit tests

- **Week 3**: The Comparer
  - Implement `TieredTrackComparer`
  - Refactor `ResultSorter`
  - Remove old scoring code

- **Week 4**: UI & Orchestration
  - Build Strategy Card UI
  - Update `SearchOrchestrationService`
  - Implement `DownloadAutomationService` (auto-queue vs review)

- **Week 5**: Testing & QA
  - Side-by-side testing
  - Migration validation
  - User acceptance testing

**Expected Impact**:
- ✅ Eliminates false positives from fake/upscaled files
- ✅ Provides transparent, deterministic decision-making
- ✅ Replaces magic numbers with clear user intent
- ✅ Reduces configuration complexity (2 dropdowns vs 8 sliders)
- ✅ Improves download success rate by 30-40% (based on user feedback)

---

### 🎯 UI Placeholder Refactor (The "Transparency" Phase) - IMMEDIATE PRIORITY
**Goal**: Eliminate "dead clicks" and connect backend logic to frontend placeholders.

#### 1. Track Inspector "Phantom" Data
- [ ] Update `TrackInspectorView` to hide empty "Energy/Danceability" fields if data missing.
- [ ] Add "Fetch Intelligence" button if fields are empty.
- [ ] Bind `AudioAnalysis` properties correctly.

#### 2. Home Page Bento Grid
- [x] Hook "Library Health" card to `CrashRecoveryJournal` queries.
- [x] Show "Active Recoveries" count if startup recovery ran.
- [ ] **Quick-Fix Action**: Add "Review/Clear Dead-Letters" button to Health Dashboard.
  - Allows retrying stuck items after user fixes disk issues.

#### 3. USB Export & Player Control
- [x] Set `IsEnabled="False"` on "Upgrade Scout" and "Sonic Integrity" (Placeholder UI Hardening).
- [ ] Disable Pitch Slider until `AudioPlayerService` supports stretching.
- [ ] **Rekordbox Stubbing**: Background task to pre-calculate XML structure for Gold Status tracks.
- [ ] Implement "Export to Rekordbox" UI (currently missing) before disabling it.

### 🏗️ Architectural Improvements & Advanced Features

#### Download Stability (HIGH Priority)
- [ ] **Stalled Download Fix**: Improve `SoulseekAdapter.cs` timeout logic.
  - Distinguish between `TransferStates.Queued` and `InProgress`
  - Disable 60-second timeout while `Queued` (can last hours on Soulseek)
  - Only start stall timer once state shifts to `InProgress`
  - Prevents premature download cancellation

#### Intelligence & Scoring (MEDIUM Priority)
- [ ] **Uploader Trust Scoring**: Add `QueueLength` and `HasFreeUploadSlot` to `ScoringWeights.cs`.
  - Rank 256kbps with open slot higher than 320kbps with 50-person queue
  - Optimize for "Time-to-Listen" vs absolute fidelity
  - Captures data already available in `ParseTrackFromFile()`
- [ ] **VBR Fraud Detection**: Extend `SonicIntegrityService` to detect upscaled files.
  - Check frequency cutoff (< 16kHz indicates upscaled 128kbps→FLAC)
  - Add `FidelityStatus` field: `Genuine | Upscaled | Suspicious`
  - Visual indicator in UI for low-quality masquerading files
- [ ] **Metadata Confidence Score**: Store `ConfidenceScore` (0.0-1.0) in `TrackEntity`.
  - Used as tie-breaker for Gold Status logic (e.g. 124.01 vs 124.0 BPM).
- [ ] **The Brain 2.0: Adaptive Intelligence**:
  - [ ] **Fuzzy Normalization**: Refactor `CalculateSimilarity` to handle Unicode dashes, curly quotes, and "feat." vs "ft." normalization.
  - [ ] **Relaxation Strategy**: Implement a "Match Relaxation" timer in `DownloadManager`. If no match found after 30s, retry with ±15s tolerance and lower bitrate threshold.
  - [ ] **User Trust & Availability**: Factor in `QueueLength` vs `Bitrate` trade-offs (Aggressive "interference" fix).
  - [ ] **BPM Intelligence**: Add `DjMode` toggle that forces search for "BPM" keywords in filename.
  - [ ] **Quality Hard-Gating**: Default ranking to "High Fidelty" (FLAC Preferred, 320kbps MP3 Floor).

#### Library UI & UX (HIGH Priority)
- [ ] **Draggable Column Reordering**: Implement drag-to-reorder for TreeDataGrid columns.
  - Allow users to prioritize Status, BPM, or other columns first
  - Persist column order to user preferences
  - Improve visual hierarchy and customization
- [ ] **Scoped Album Card ViewModels**: Refactor Bento Grid for performance.
  - Each `AlbumCard` gets its own scoped ViewModel
  - Handles own `DownloadCommand` instead of binding to central command
  - Prevents UI lag with 100+ albums updating simultaneously
- [ ] **Skeleton Screens**: Add loading placeholders during API/search latency.
  - Semi-transparent "ghost rows" during Spotify/Soulseek searches
  - Makes app feel 2x faster with perceived performance boost

#### Advanced Features (MEDIUM Priority)
- [ ] **Self-Healing Upgrade Scout**: Background worker to find better quality versions.
  - Scans library for tracks < 192kbps
  - Automatically initiates Soulseek search for 320kbps/FLAC upgrade
  - User approval required before replacing files

### What's Working
- ✅ Search with ranking (Soulseek P2P)
- ✅ Playlists (6 loaded from database)
- ✅ Library management
- ✅ Track playback (LibVLC)
- ✅ Download tracking
- ✅ Drag-drop reordering
- ✅ Import (Spotify URLs, CSV)
- ✅ Album grouping
- ✅ Context menu
- ✅ Keyboard shortcuts
- ✅ Window state persistence
- ✅ System tray

### Database
- ✅ SQLite with full history tracking
- ✅ PlaylistJobs, PlaylistTracks, ActivityLogs
- ✅ Automatic migrations
- ✅ Foreign keys, indexes

---

## 🌌 ANTIGRAVITY IMPLEMENTATION STRATEGY

**Critical Decision**: Build Spotify Metadata Foundation FIRST to avoid 12+ hours of rework

**Why**: Adding Spotify IDs later requires database migrations, import refactoring, and metadata backfill. Build the gravity well now, everything else orbits around it.

---

## Phase 0: Metadata Gravity Well (8-12 hours) 🔴⭐ FOUNDATION - COMPLETE ✅

### 0.1 Database Schema Evolution (3 hours)
**Priority**: ⭐⭐⭐ CRITICAL - LOAD BEARING - COMPLETE ✅

**What to Build**:
- [x] Add `SpotifyTrackId` (string, nullable) to `PlaylistTrackEntity`
- [x] Add `SpotifyAlbumId` (string, nullable)
- [x] Add `SpotifyArtistId` (string, nullable)
- [x] Add `AlbumArtUrl` (string, nullable)
- [x] Add `ArtistImageUrl` (string, nullable)
- [x] Add `Genres` (string, JSON array)
- [x] Add `Popularity` (int, 0-100)
- [x] Add `CanonicalDuration` (int, milliseconds)
- [x] Add `ReleaseDate` (DateTime, nullable)
- [x] Create database migration script

**Files to Modify**:
- `Data/Entities/PlaylistTrackEntity.cs`
- `Data/AppDbContext.cs` (add migration)
- `Models/PlaylistTrack.cs` (add properties)

**Implementation Steps**:
1. Add new columns to entity (all nullable for backward compatibility)
2. Create EF Core migration: `dotnet ef migrations add AddSpotifyMetadata`
3. Test migration on copy of database
4. Add properties to domain model
5. Update ViewModels to display new fields

**Risk Mitigation**:
- Backup database before migration
- Make all columns nullable (optional)
- Test on development database first

---

### 0.2 Spotify Metadata Service (4 hours)
**Priority**: ⭐⭐⭐ CRITICAL - COMPLETE ✅

**What to Build**:
- [x] `SpotifyMetadataService` class (`SpotifyEnrichmentService`)
- [x] `GetTrackMetadata(artist, title)` - lookup by search
- [x] `GetTrackById(spotifyId)` - lookup by ID
- [x] `EnrichPlaylistTrack(track)` - attach metadata to track
- [x] Metadata cache (via `IsEnriched` flag and DB storage)
- [x] Rate limiting (30 req/sec Spotify limit)
- [x] Batch requests (up to 50 tracks per call)

**Files to Create**:
- `Services/SpotifyMetadataService.cs` (new)
- `Data/Entities/SpotifyMetadataCacheEntity.cs` (new)
- `Models/SpotifyMetadata.cs` (new)

**Implementation Steps**:
1. Create metadata cache entity and migration
2. Build `SpotifyMetadataService` with SpotifyAPI.Web
3. Implement search endpoint wrapper
4. Add cache lookup before API calls
5. Implement rate limiting with `SemaphoreSlim`
6. Add batch request support
7. Test with real Spotify API

**API Endpoints Used**:
- `GET /search?q=artist:X track:Y&type=track` - Search for track
- `GET /tracks/{id}` - Get track by ID
- `GET /tracks?ids=X,Y,Z` - Batch get (up to 50)

---

### 0.3 Import Integration (3 hours)
**Priority**: ⭐⭐⭐ HIGH - COMPLETE ✅

**What to Build**:
- [x] Update `SpotifyImportProvider` to store Spotify IDs
- [x] Update `CsvImportProvider` to lookup metadata (via background worker)
- [x] Update `DownloadManager` to enrich post-download (integrated into Discovery)
- [x] Add "Fetching metadata..." status messages (via UI column)
- [x] Background metadata enrichment for existing tracks

**Files to Modify**:
- `Services/ImportProviders/SpotifyImportProvider.cs`
- `Services/ImportProviders/CsvImportProvider.cs`
- `Services/DownloadManager.cs`
- `ViewModels/ImportPreviewViewModel.cs`

**Implementation Steps**:
1. Inject `SpotifyMetadataService` into import providers
2. Call `EnrichPlaylistTrack()` during import
3. Add progress indicator for metadata fetch
4. Store Spotify IDs in database
5. Test CSV import with metadata lookup
6. Test Spotify import with ID preservation

---

---

### 0.4 Artwork Pipeline (2 hours)
**Priority**: ⭐⭐ MEDIUM

**What to Build**:
- [ ] Download and cache album art from Spotify URLs
- [ ] Store images in `%APPDATA%/QMUSICSLSK/artwork/`
- [ ] Image cache service
- [ ] Placeholder image for missing artwork

**Files to Create**:
- `Services/ArtworkCacheService.cs` (new)

**Implementation Steps**:
1. Create artwork cache directory
2. Download images from `AlbumArtUrl`
3. Save as `{spotifyAlbumId}.jpg`
4. Return local file path for UI binding

---

## Phase 12: Search Experience 2.0 (Technical Breakdown) 🟡 NEW
- [x] Basic Filter Implementation
- [x] Batch Download Actions (Selection Logic)
- [x] Advanced Filtering logic (SearchFilterViewModel)

### 12.1 Reactive Search (Streaming) (4 hours)
- [x] Refactor `SearchOrchestrationService` to return `IAsyncEnumerable<AlbumResultViewModel>` (Done previously)
- [x] Implement incremental results "pop-in" (500ms first paint) (Done previously)
- [x] Use `SourceList<T>` (DynamicData) for thread-safe UI updates
- [x] Search throttling (100ms buffer) to prevent UI stutter (Done via Smart Buffer)
- [x] **Album Cards**: Implement grid layout for album results (`AlbumCard.axaml`)
- [x] **Reactive Status**: Bind search results to `DownloadManager` for live status updates

### 12.2 Advanced Filtering HUD (3 hours)
- [x] Create `SearchFilterViewModel`
- [x] Add **Bitrate Slider** (range selector)
- [x] Add **Format Chips** (MP3/FLAC/WAV toggles)
- [x] Add **User Trust Toggle** (filter locked/slow peers)
- [x] Bind filter logic to `DynamicData` filter pipeline

### 12.3 Batch Actions & Selection (3 hours)
- [x] Enable `SelectionMode="Multiple"` in Search Grid
- [x] Implement "Floating Action Bar" (appears on selection > 1)
- [x] Add `DownloadSelectedCommand` (batch download)
- [ ] Add `AddToPlaylistCommand` (batch add)

### 12.4 Critical Fixes (2 hours)
- [ ] **Overlay Bug**: Replace `GetType().Name` calculation with explicit `IsImportOverlayActive` flags
- [ ] **Theming**: Replace `#1A1A1A` with `{DynamicResource RegionColor}`
- [ ] **Reflection Removal**: Replace `GetType().GetProperty("PlayerViewModel")` with properly injected `IPlayerService`

### 12.6 Search 2.0 Visual Hierarchy (December 26, 2025) ✅ COMPLETE
- [x] **Percentile-Based Scoring**: Relative ranking (top 5% = golden match)
- [x] **Golden Match Highlighting**: 🔥 emoji for top 5% results
- [x] **Heat-Map Opacity**: Visual fading (1.0/0.85/0.6) based on percentile
- [x] **Integrity Badges**: ✅⚠️🚫🔥 with tooltips (Verified/Warning/Suspect/HarmonicMatch)
- [x] **Hide Suspects Filter**: Checkbox in filter HUD (enabled by default)
- [x] **Multi-Line Row Templates**: Two-line layout (Artist-Title / Technical metadata)
  - Line 1: `Artist - Title` (bold) + free slot indicator
  - Line 2: `320 kbps MP3 • @username • 12.3 MB • Q:2` (gray monospace)
- [x] Commits: `20c8526`, `0b4f04a`, `b349c7b`

---


## Phase 0A: Speed & UX Optimization (Weeks 5-8) 🟡 MONTH 2 FOCUS

### 0A.1 UI Virtualization for Large Libraries (6 hours) - NEW ⭐⭐⭐ CRITICAL
**Problem**: 20,000+ track libraries cause UI stuttering and slow scrolling.  
**Solution**: Implement UI virtualization for all large lists.

**Implementation**:
- [ ] Replace standard `ItemsControl` with `VirtualizingStackPanel` in:
  - `LibraryPage.axaml` track list
  - `SearchPage.axaml` results grid
  - `DownloadsPage.axaml` active downloads
- [ ] Configure `VirtualizingPanel.IsVirtualizing="True"`
- [ ] Set `VirtualizingPanel.VirtualizationMode="Recycling"` for memory efficiency
- [ ] Test with library of 25,000+ tracks

**Impact**: Smooth 60 FPS scrolling even with massive libraries. Essential for scalability.

---

### 0A.2 Lazy Image Loading with Proxy Pattern (4 hours) - NEW ⭐⭐ HIGH
**Problem**: Loading 1000+ album arts simultaneously crashes UI.  
**Solution**: Lazy-load images only when visible in viewport.

**Implementation**:
- [ ] Create `Services/ArtworkProxy.cs`:
  ```csharp
  public class ArtworkProxy
  {
      private BitmapImage? _realImage;
      private readonly string _artworkUrl;
      private static readonly BitmapImage PlaceholderImage = LoadPlaceholder();
      
      public BitmapImage GetImage(bool isVisible)
      {
          if (!isVisible) return PlaceholderImage;
          if (_realImage == null)
              _realImage = LoadFromCache(_artworkUrl) ?? DownloadImage(_artworkUrl);
          return _realImage;
      }
  }
  ```
- [ ] Integrate with `AlbumCard.axaml` visibility events
- [ ] Use placeholder until card enters viewport

**Impact**: 80% reduction in memory usage, smooth library browsing.

---

### 0A.3 Progressive Interaction Design (5 hours) - NEW ⭐⭐ MEDIUM
**Problem**: Users perceive app as "slow" during API/search operations.  
**Solution**: Implement perceived performance improvements.

**Implementation**:
- [x] **Skeleton Screens**: Semi-transparent ghost rows during loading (Already planned line 286-288)
- [ ] **Progress Bars in Buttons**: "Download" button shows inline progress (0-100%)
  - Replace separate progress bar with button content transformation
  - Visual feedback: Button changes color/text as download progresses
- [ ] **Optimistic UI Updates**: Immediately reflect user actions before DB confirmation
  - Delete track → fade out immediately, rollback if DB fails
  - Rename file → show new name instantly, revert if operation fails
- [ ] **Smart Empty States**: Contextual messages instead of blank pages
  - Library: "Your library is empty — try importing a playlist" with action button
  - Downloads: "No active downloads" with "Search for tracks" button
  - Search: "No results found — check if Soulseek is connected"

**Impact**: App feels 2x faster through psychological perception improvements.

---

### 0A.4 System Health Panel (Dashboard Enhancement) (4 hours) - IMMEDIATE PRIORITY ⭐⭐⭐
**Problem**: Users don't know if Soulseek/Spotify are connected or having issues.
**Solution**: Real-time system diagnostics widget in Dashboard.

**Implementation**:
- [ ] Update `HomeViewModel.cs` to query `CrashRecoveryJournal` stats.
- [ ] Add health widget to `HomePage.axaml`:
  ```
  🟢 System Health: Excellent (0 Dead Letters)
  🟢 Soulseek: Connected (120ms latency)
  🟡 Disk I/O: Moderate (45% utilization)
  ```
- [ ] Implement "Dead Letter" review action (Unlock/Retry).
- [ ] Color-coded status: 🟢 Green (good), 🟡 Yellow (warning), 🔴 Red (error)

**Impact**: shows off the industrial reliability of the backend.

---

### 0A.5 Quality Badge Enhancements (3 hours) - ENHANCE EXISTING ⭐⭐ MEDIUM
**Current**: Basic quality badges exist (ROADMAP line 511-512)  
**Enhancement**: Add Fake FLAC detection and Upgrade Available badges

**Implementation**:
- [ ] Extend `QualityBadgeType` enum:
  ```csharp
  public enum QualityBadgeType
  {
      Flac,           // Purple
      High,           // Green (320kbps)
      Medium,         // Yellow (192-256kbps)
      Low,            // Orange (<192kbps)
      FakeFlac,       // Red with warning icon
      UpgradeAvailable // Blue with arrow icon
  }
  ```
- [ ] Integrate with `SonicIntegrityService.FidelityStatus`
- [ ] Add badge UI to `TrackListItem` template with tooltips

**Impact**: Instant visual clarity. Users can identify fake files at a glance.

---

## Phase 0B: Architectural Modernization (Weeks 9-12) 🔵 FOUNDATION LAYER

### 0B.1 Unified Pipeline Orchestrator (16 hours) - NEW ⭐⭐⭐ HIGH
**Problem**: Multiple orchestrators (MetadataEnrichment, SearchOrchestration, Import) manage pipelines independently → race conditions.  
**Solution**: Single orchestrator coordinating all 8 stages of download pipeline.

**Implementation**:
- [ ] Create `Services/DownloadPipelineOrchestrator.cs`:
  ```csharp
  public class DownloadPipelineOrchestrator
  {
      public async Task<PipelineResult> ProcessTrackAsync(Track track)
      {
          var context = new PipelineContext(track);
          await _discoveryStage.ExecuteAsync(context);      // 1. Soulseek search
          await _rankingStage.ExecuteAsync(context);        // 2. Score candidates
          await _downloadStage.ExecuteAsync(context);       // 3. Download file
          await _verificationStage.ExecuteAsync(context);   // 4. Verify integrity
          await _normalizationStage.ExecuteAsync(context);  // 5. Normalize filename
          await _importStage.ExecuteAsync(context);         // 6. Import to library
          await _indexingStage.ExecuteAsync(context);       // 7. Update search index
          await _notificationStage.ExecuteAsync(context);   // 8. Notify UI
          return PipelineResult.Success(context);
      }
  }
  
  public interface IPipelineStage
  {
      Task ExecuteAsync(PipelineContext context);
      Task RollbackAsync(PipelineContext context); // For crash recovery
  }
  ```
- [ ] Create individual stage classes for each step
- [ ] Integrate with Crash Recovery Journal (Phase 0.1) for resumability
- [ ] Update `DownloadManager` to use orchestrator

**Impact**: Eliminates race conditions, makes resumability trivial, cleaner architecture.

---

### 0B.2 Track Fingerprint Abstraction (8 hours) - NEW ⭐⭐⭐ HIGH
**Problem**: No canonical track identity → duplicates slip through, upgrade detection impossible.  
**Solution**: Composite fingerprint combining ISRC, Spotify ID, normalized metadata, duration bucket.

**Implementation**:
- [ ] Create `Models/TrackFingerprint.cs`:
  ```csharp
  public class TrackFingerprint
  {
      public string? SpotifyId { get; set; }
      public string? Isrc { get; set; }
      public string NormalizedArtist { get; set; }
      public string NormalizedTitle { get; set; }
      public int DurationBucket { get; set; } // 210s → bucket 21
      
      public string GetCompositeHash()
      {
          // Prioritize Spotify ID > ISRC > metadata hash
      }
  }
  ```
- [ ] Use for duplicate detection in `DownloadManager`
- [ ] Use for upgrade detection in Self-Healing Library
- [ ] Integrate with deterministic Track ID (line 92-94)

**Impact**: Powers duplicate detection, upgrade scout, and self-healing library.

---

### 0B.3 Strongly Typed Event Bus with Replay (6 hours) - ENHANCE EXISTING ⭐⭐ HIGH
**Current**: EventBus exists but events aren't strongly typed  
**Enhancement**: Add event replay capability and typed events

**Implementation**:
- [ ] Define strongly-typed events:
  ```csharp
  public class TrackImportedEvent
  {
      public string TrackId { get; set; }
      public string ProjectId { get; set; }
      public ImportSource Source { get; set; }
  }
  
  public class DownloadStateChangedEvent
  {
      public string TrackId { get; set; }
      public DownloadState OldState { get; set; }
      public DownloadState NewState { get; set; }
      public string? ErrorMessage { get; set; }
  }
  ```
- [ ] Enhance `EventBusService` to store event history (last 1000 events)
- [ ] Add `GetHistory(since, eventType)` method
- [ ] Add `ReplayEvents(events)` method for debugging
- [ ] Create Developer Tools page to inspect event stream

**Impact**: Far more debuggable during complex multi-stage imports.

---

### 0B.4 Profile-Based Tuning System (5 hours) - ENHANCE EXISTING ⭐⭐ MEDIUM
**Current**: Generic weight sliders (Phase 1.5)  
**Enhancement**: Pre-configured profiles for different user types

**Implementation**:
- [ ] Define profiles in `Configuration/ScoringProfiles.cs`:
  ```csharp
  public static class ScoringProfiles
  {
      public static RankingWeights AudiophileProfile => new()
      {
          LosslessScore = 300,
          BitrateWeight = 250,
          BpmMatchWeight = 50,
          DurationToleranceMs = 3000
      };
      
      public static RankingWeights DJProfile => new()
      {
          LosslessScore = 100,
          BitrateWeight = 150,
          BpmMatchWeight = 300,
          DurationToleranceMs = 10000
      };
  }
  ```
- [ ] Add profile selector to Settings UI
- [ ] Allow users to customize profiles and save as presets

**Impact**: Fulfills vision of "Soulseek client with a brain" tailored to user workflow.

---

## Phase 0C: Stabilization Tooling (8-Week Freeze Support) 🧪 DEV TOOLS

### 0C.1 Automated Stress Tests (12 hours) - NEW ⭐⭐⭐ CRITICAL for freeze
**Problem**: No automated validation of resilience improvements.  
**Solution**: Comprehensive test suite simulating real-world edge cases.

**Implementation**:
- [ ] Create `Tests/StressTests.cs`:
  - `Test_500TrackImport()` - Validate bulk import performance
  - `Test_NetworkDropDuringDownload()` - Simulate WiFi drop, verify resume
  - `Test_SlowPeer()` - Verify automatic peer switching
  - `Test_CorruptedFile()` - Validate SonicIntegrityService
  - `Test_DuplicateHeavyPlaylist()` - 100 tracks, 50% duplicates
- [ ] Configure GitHub Actions for nightly stress test runs
- [ ] Generate reports for failures

**Impact**: Validates all resilience work. Essential for 8-week stabilization freeze.

---

### 0C.2 Performance Overlay (Debug Mode) (4 hours) - NEW ⭐ LOW (Dev Tool)
**Problem**: No visibility into performance bottlenecks during development.  
**Solution**: Real-time performance HUD (FPS, DB queries, memory, events).

**Implementation**:
- [ ] Create `Views/PerformanceOverlay.axaml` (top-right corner overlay)
- [ ] Display metrics:
  - FPS (60 target)
  - DB queries per second
  - Memory usage (MB)
  - Event bus traffic
- [ ] Hook into rendering pipeline and DB interceptors
- [ ] Activate with Ctrl+Shift+P or Settings toggle

**Impact**: Helps catch bottlenecks instantly during development.

---

### 0C.3 Stability Mode Build Flag (2 hours) - NEW ⭐⭐ MEDIUM
**Problem**: Need enhanced logging during 8-week stabilization without affecting production builds.  
**Solution**: Conditional compilation flag for verbose diagnostics.

**Implementation**:
- [ ] Add to `.csproj`:
  ```xml
  <PropertyGroup Condition="'$(Configuration)' == 'Stability'">
      <DefineConstants>STABILITY_MODE</DefineConstants>
  </PropertyGroup>
  ```
- [ ] Wrap verbose logging in `#if STABILITY_MODE` blocks
- [ ] Enable crash recovery logging
- [ ] Build command: `dotnet build -c Stability`

---

## Phase 1.5: Advanced Ranking Configuration (4 hours) ✨ NEW

**Priority**: ⭐⭐ MEDIUM - User Control

**What to Build**:
- [ ] Settings page with ranking weight sliders
- [ ] Configuration storage in `AppConfig`
- [ ] Dynamic weight injection into `ResultSorter`
- [ ] Preset system ("Quality First", "DJ Mode", "Balanced")
- [ ] Real-time ranking preview with example files

**Files to Modify**:
- `Configuration/AppConfig.cs` - Add ranking weight properties
- `Services/ResultSorter.cs` - Accept configurable weights
- `Views/SettingsPage.axaml` - Add ranking configuration UI
- `ViewModels/SettingsViewModel.cs` - Bind sliders to config

**Configuration Schema**:
```csharp
public class RankingWeights
{
    public int BpmProximityWeight { get; set; } = 150;
    public int BitrateQualityWeight { get; set; } = 200;
    public int DurationMatchWeight { get; set; } = 100;
    public int TitleSimilarityWeight { get; set; } = 200;
}
```

**UI Components**:
- Slider: "BPM Match Importance" (0-500, default 150)
- Slider: "Bitrate Quality Importance" (0-500, default 200)
- Slider: "Duration Match Importance" (0-200, default 100)
- Button: "Reset to Defaults"
- ComboBox: Presets dropdown

**Impact**: Allows users to tune ranking for their use case (audiophile vs DJ)

---

## Phase 2: Code Quality & Maintainability (Refactoring) (8-12 hours) ✨ NEW

**Priority**: ⭐⭐ MEDIUM - Technical Debt Reduction

### 2.1 Extract Method - ResultSorter (2 hours)
**Reference**: [Refactoring.Guru - Extract Method](https://refactoring.guru/extract-method)

**Files to Modify**:
- `Services/ResultSorter.cs`
- `Services/DownloadDiscoveryService.cs`

**What to Extract**:
- [ ] `CalculateBitrateScore(Track track)` - Isolate bitrate quality calculation
- [ ] `CalculateDurationPenalty(Track result, Track target)` - Duration mismatch logic
- [ ] `EvaluateUploaderTrust(Track track)` - Free slot + queue length scoring
- [ ] `ExtractBpmFromFilename(string filename)` - Already exists, good example

**Impact**: Each scoring component becomes independently testable

---

### 2.2 Replace Magic Numbers (1 hour)
**Reference**: [Refactoring.Guru - Replace Magic Number](https://refactoring.guru/replace-magic-number-with-symbolic-constant)

**Files to Create**:
- `Configuration/ScoringConstants.cs` (new)

**Constants to Define**:
```csharp
public static class ScoringConstants
{
    // Duration Gating
    public const int DurationToleranceSeconds = 30;
    public const int SmartDurationToleranceSeconds = 15;
    
    // Scoring Weights (configurable via AppConfig)
    public const int BpmProximityWeight = 150;
    public const int BitrateQualityWeight = 200;
    public const int DurationMatchWeight = 100;
    public const int TitleSimilarityWeight = 200;
    
    // Filesize Validation
    public const int MinBytesPerSecond = 8000; // ~64kbps
    public const double FilesizeSuspicionThreshold = 0.5; // 50% of expected
}
```

---

### 2.3 Replace Conditional with Polymorphism - Tagger (3 hours)
**Reference**: [Refactoring.Guru - Replace Conditional](https://refactoring.guru/replace-conditional-with-polymorphism)

**Files to Create**:
- `Services/Tagging/IAudioTagger.cs` (interface)
- `Services/Tagging/Id3Tagger.cs` (MP3, AAC)
- `Services/Tagging/VorbisTagger.cs` (FLAC, OGG)
- `Services/Tagging/TaggerFactory.cs` (factory pattern)

**Files to Modify**:
- `Services/MetadataTaggerService.cs` - Use factory instead of conditionals

**Impact**: Easier to add new formats (WAV, ALAC, etc.)

---

### 2.4 Introduce Parameter Object (2 hours)
**Reference**: [Refactoring.Guru - Introduce Parameter Object](https://refactoring.guru/introduce-parameter-object)

**Files to Create**:
- `Models/TrackIdentityProfile.cs` (new)

**Schema**:
```csharp
public class TrackIdentityProfile
{
    public string Artist { get; set; }
    public string Title { get; set; }
    public string? Album { get; set; }
    public double? BPM { get; set; }
    public int? CanonicalDuration { get; set; }
    public string? MusicalKey { get; set; }
}
```

**Files to Modify**:
- `Services/SpotifyMetadataService.cs` - Accept `TrackIdentityProfile` instead of 5+ parameters

---

### 2.5 Extract Class - Orchestrator Split (3 hours)
**Reference**: [Refactoring.Guru - Extract Class](https://refactoring.guru/extract-class)

**Files to Create**:
- `Services/LibraryOrganizationService.cs` - File renaming and directory structure
- `Services/ArtworkPipeline.cs` - Album art fetching and caching
- `Services/MetadataPersistenceOrchestrator.cs` - DB + tag sync

**Files to Modify**:
- `Services/MetadataEnrichmentOrchestrator.cs` - Delegate to new services

**Impact**: Single Responsibility Principle, easier testing

---

### 2.6 Strategy Pattern - Ranking Modes (2 hours)
**Reference**: [Refactoring.Guru - Strategy](https://refactoring.guru/design-patterns/strategy)

**Files to Create**:
- `Services/Ranking/ISortingStrategy.cs` (interface)
- `Services/Ranking/AudiophileStrategy.cs` - Bitrate-heavy
- `Services/Ranking/DjStrategy.cs` - BPM/Key-heavy
- `Services/Ranking/BalancedStrategy.cs` - Default weights

**Files to Modify**:
- `Services/ResultSorter.cs` - Accept `ISortingStrategy` parameter
- `Configuration/AppConfig.cs` - Store selected strategy

**Impact**: Runtime switching between ranking modes

---

### 2.7 Observer Pattern - Event-Driven Architecture (2 hours)
**Reference**: [Refactoring.Guru - Observer](https://refactoring.guru/design-patterns/observer)

**Files to Modify**:
- `Services/EventBusService.cs` - Enhance existing event bus
- `Services/TrackAnalysisOrchestrator.cs` - Emit progress events
- `ViewModels/*` - Subscribe to events instead of direct calls

**Events to Define**:
```csharp
public class TrackAnalysisProgressEvent
{
    public string TrackId { get; set; }
    public int PercentComplete { get; set; }
    public string CurrentStep { get; set; } // "Extracting BPM", "Detecting Key"
}

public class DownloadProgressEvent
{
    public string JobId { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
}
```

**Impact**: Multi-core analysis engine decoupled from UI, multiple observers can listen

---

### 2.8 Null Object Pattern - Metadata Handling (1 hour)
**Reference**: [Refactoring.Guru - Null Object](https://refactoring.guru/introduce-null-object)

**Files to Create**:
- `Models/NullSpotifyMetadata.cs`

**Implementation**:
```csharp
public class NullSpotifyMetadata : SpotifyMetadata
{
    public NullSpotifyMetadata()
    {
        BPM = 0;
        MusicalKey = "Unknown";
        Confidence = 0.0;
        SpotifyId = null;
        AlbumArtUrl = null;
    }
    
    public override bool IsValid => false;
}
```

**Files to Modify**:
- `Services/SpotifyMetadataService.cs` - Return `NullSpotifyMetadata` instead of null
- `Services/ResultSorter.cs` - Remove null checks, use `metadata.IsValid`

**Impact**: Cleaner scoring logic, no `?.` operators, fewer `NullReferenceException` crashes

---

### 2.9 Command Pattern - Undo/Redo for Library (3 hours)
**Reference**: [Refactoring.Guru - Command](https://refactoring.guru/design-patterns/command)

**Files to Create**:
- `Commands/ILibraryCommand.cs` (interface)
- `Commands/DeleteTrackCommand.cs`
- `Commands/UpgradeTrackCommand.cs`
- `Commands/CommandHistory.cs` (undo stack)

**Implementation**:
```csharp
public interface ILibraryCommand
{
    void Execute();
    void Undo();
    string Description { get; }
}

public class DeleteTrackCommand : ILibraryCommand
{
    private readonly Track _track;
    private readonly string _originalPath;
    
    public void Execute()
    {
        // Delete file, mark as deleted in DB
    }
    
    public void Undo()
    {
        // Restore from recycle bin or backup
    }
}
```

**Impact**: Ctrl+Z support for Self-Healing Library, safer bulk operations

---

### 2.10 Proxy Pattern - Lazy-Loading Artwork (2 hours)
**Reference**: [Refactoring.Guru - Proxy](https://refactoring.guru/design-patterns/proxy)

**Files to Create**:
- `Services/ArtworkProxy.cs`

**Implementation**:
```csharp
public class ArtworkProxy
{
    private BitmapImage? _realImage;
    private readonly string _artworkUrl;
    private static readonly BitmapImage PlaceholderImage = LoadPlaceholder();
    
    public BitmapImage GetImage(bool isVisible)
    {
        if (!isVisible) return PlaceholderImage;
        
        if (_realImage == null)
        {
            _realImage = LoadFromCache(_artworkUrl) ?? DownloadImage(_artworkUrl);
        }
        
        return _realImage;
    }
}
```

**Impact**: Smooth scrolling in 1000+ track libraries, 80% reduction in memory usage

---

### 2.11 Template Method - Import Provider Skeleton (2 hours)
**Reference**: [Refactoring.Guru - Template Method](https://refactoring.guru/design-patterns/template-method)

**Files to Create**:
- `Services/ImportProviders/BaseImportProvider.cs`

**Implementation**:
```csharp
public abstract class BaseImportProvider
{
    // Template method
    public async Task<List<Track>> ImportAsync()
    {
        var rawTracks = await ParseSourceAsync(); // Abstract
        var enrichedTracks = await EnrichWithSpotifyAsync(rawTracks); // Concrete
        await PersistToDatabase(enrichedTracks); // Concrete
        return enrichedTracks;
    }
    
    protected abstract Task<List<Track>> ParseSourceAsync();
}
```

**Impact**: All import providers automatically follow "Gravity Well" enrichment

---

### 2.12 State Pattern - Download Job State Machine (3 hours)
**Reference**: [Refactoring.Guru - State](https://refactoring.guru/design-patterns/state)

**Files to Create**:
- `Services/DownloadStates/IDownloadState.cs`
- `Services/DownloadStates/QueuedState.cs`
- `Services/DownloadStates/DownloadingState.cs`
- `Services/DownloadStates/VerifyingState.cs`
- `Services/DownloadStates/EnrichingState.cs`

**Implementation**:
```csharp
public interface IDownloadState
{
    Task HandleAsync(DownloadJob job);
    IDownloadState GetNextState(DownloadJob job);
}

public class DownloadingState : IDownloadState
{
    public async Task HandleAsync(DownloadJob job)
    {
        // Download file logic
    }
    
    public IDownloadState GetNextState(DownloadJob job)
    {
        return job.DownloadComplete 
            ? new VerifyingState() 
            : this;
    }
}
```

**Impact**: Cleaner state transitions, easier to add VBR verification step

---

## Phase 4: Performance Optimization (Multi-core & Hardware Acceleration) (6-8 hours) ✨ NEW

**Priority**: ⭐⭐ HIGH - Critical for Phase 5 (Self-Healing Library)

### 4.1 Parallel Library Scanning (2 hours)
**Reference**: [.NET 8 Parallel Programming](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/)

**Files to Modify**:
- `Services/LibraryService.cs` - Refactor scan loops to use `Parallel.ForEachAsync`

**Implementation**:
```csharp
await Parallel.ForEachAsync(tracks, new ParallelOptions
{
    MaxDegreeOfParallelism = Environment.ProcessorCount,
    CancellationToken = cancellationToken
}, async (track, ct) =>
{
    await AnalyzeTrackAsync(track, ct);
});
```

**Impact**: 10,000 track scan: 2 hours → 10 minutes

---

### 4.2 Background Worker Architecture (3 hours)

**Files to Create**:
- `Services/TrackAnalysisOrchestrator.cs` - Channel-based worker pool
- `Services/Workers/AnalysisWorker.cs` - Isolated analysis thread

**Architecture**:
```csharp
public class TrackAnalysisOrchestrator
{
    private readonly Channel<TrackAnalysisRequest> _queue;
    private readonly SemaphoreSlim _workerSemaphore;
    
    public async Task EnqueueAsync(Track track)
    {
        await _queue.Writer.WriteAsync(new TrackAnalysisRequest(track));
    }
    
    private async Task WorkerLoop()
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // UI priority
        // Process queue...
    }
}
```

**Impact**: UI never freezes during heavy operations

---

### 4.3 Performance Mode Settings (1 hour)

**Files to Modify**:
- `Configuration/AppConfig.cs` - Add `PerformanceMode` enum
- `Views/SettingsPage.axaml` - Add performance mode selector

**Modes**:
- **Eco**: `MaxDegreeOfParallelism = 2` (silent)
- **Balanced**: `MaxDegreeOfParallelism = ProcessorCount / 2` (default)
- **Turbo**: `MaxDegreeOfParallelism = ProcessorCount` (fastest)

---

### 4.4 Memory Mapped Files (2 hours)

**Files to Modify**:
- `Services/MetadataService.cs` - Use `MemoryMappedFile` for large file analysis

**Implementation**:
```csharp
using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open);
using var accessor = mmf.CreateViewAccessor();
// Direct memory access without loading entire file
```

**Impact**: 50% reduction in RAM usage for batch operations

---

### 4.5 Hardware Acceleration (Optional, 2-4 hours)

**Files to Create**:
- `Services/HardwareAcceleration/GpuTranscoder.cs` - FFmpeg with NVENC/QuickSync

**Platform Detection**:
```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // Use NVENC or QuickSync
}
else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    // Use VideoToolbox
}
```

**Impact**: Real-time preview generation, faster fingerprinting

---
5. Add placeholder for missing artwork

---

## Phase 0B: OAuth UI Completion (3 hours) 🟡 HIGH

### 0B.1 OAuth PKCE Core Service ✅ COMPLETE
**Priority**: ⭐⭐⭐ CRITICAL

**Status**: ✅ All core services implemented

**Completed**:
- [x] PKCE code verifier generation (128 chars, base64url)
- [x] PKCE code challenge generation (SHA256)
- [x] Authorization URL builder with scopes
- [x] Token exchange endpoint integration
- [x] Refresh token logic
- [x] Token expiration detection

**Files Created**:
- ✅ `Utils/PKCEHelper.cs`
- ✅ `Services/LocalHttpServer.cs`
- ✅ `Services/SpotifyAuthService.cs`
- ✅ `Services/ISecureTokenStorage.cs`
- ✅ `Services/Platform/WindowsTokenStorage.cs`
- ✅ `Services/Platform/MacOSTokenStorage.cs`
- ✅ `Services/Platform/LinuxTokenStorage.cs`
- ✅ `Services/SecureTokenStorageFactory.cs`

---

### 0B.2 UI Integration (3 hours)
**Priority**: ⭐⭐⭐ HIGH

**What to Build**:
- [ ] "Sign In with Spotify" button in Settings
- [ ] OAuth flow dialog with status messages
- [ ] User profile display when authenticated
- [ ] "Sign Out" button
- [ ] "My Playlists" button in Import page
- [ ] "My Saved Tracks" button in Import page

**Files to Modify**:
- `Views/Avalonia/SettingsPage.axaml` (add Spotify auth section)
- `ViewModels/SpotifyImportViewModel.cs` (add auth commands)

**Files to Create**:
- `Views/Avalonia/SpotifySignInDialog.axaml` (new)

**Implementation Steps**:
1. Add Spotify section to Settings page
2. Create `SpotifySignInDialog` with status messages
3. Add `SignInCommand` to `SpotifyImportViewModel`
4. Add `SignOutCommand` to `SpotifyImportViewModel`
5. Add `IsAuthenticated` property with UI binding
6. Add `UserDisplayName` property to show logged-in user
7. Wire up "My Playlists" and "Saved Tracks" buttons
8. Test sign-in/sign-out flow in UI

---

### 0.4 Spotify API Integration (2 hours)
**Priority**: ⭐⭐⭐ HIGH

**What to Build**:
- [ ] User profile endpoint (`/me`)
- [ ] User playlists endpoint (`/me/playlists`)
- [ ] User saved tracks endpoint (`/me/tracks`)
- [ ] Search endpoint with user context
- [ ] Authenticated SpotifyClient factory

**Files to Modify**:
- `Services/InputParsers/SpotifyInputSource.cs` (add user endpoints)
- `Services/ImportProviders/SpotifyImportProvider.cs` (use auth when available)

**Implementation Steps**:
1. Inject `SpotifyAuthService` into `SpotifyInputSource`
2. Add `GetCurrentUserAsync()` method
3. Add `GetUserPlaylistsAsync()` method
4. Add `GetUserSavedTracksAsync()` method
5. Update `CreateClientAsync()` to use auth tokens when available
6. Add fallback to Client Credentials for public playlists
7. Test with authenticated Spotify account

---

### 0.5 Configuration & Testing (1 hour)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Add OAuth settings to AppConfig
- [ ] Update Settings UI with redirect URI config
- [ ] Add Spotify Developer Dashboard setup docs
- [ ] End-to-end testing checklist

**Files to Modify**:
- `Configuration/AppConfig.cs` (add OAuth properties)
- `README.md` (add OAuth setup instructions)

**Files to Create**:
- `DOCS/SPOTIFY_OAUTH_SETUP.md` (new - setup guide)

**Implementation Steps**:
1. Add `SpotifyRedirectUri` to AppConfig (default: localhost:5000)
2. Add `SpotifyCallbackPort` to AppConfig
3. Add `SpotifyRememberAuth` toggle to AppConfig
4. Create setup guide for Spotify Developer Dashboard
5. Document redirect URI configuration
6. Create testing checklist
7. Test on Windows, macOS, and Linux

---

## Phase 1: Critical Features ✅ 80% COMPLETE (was 0%)

### 1.1 Queue Management ✅ 90% COMPLETE (was 0%)
**Priority**: ⭐⭐⭐ CRITICAL

**Completed Today**:
- [x] Play queue view (QueuePanel.axaml in right sidebar)
- [x] Add to queue button (context menu + 📋 button + drag-drop)
- [x] Clear queue button
- [x] Drag-drop reorder queue (custom ghost items)
- [x] Remove from queue
- [x] Drag-to-player for immediate playback
- [x] MoveTrack(globalId, targetIndex) method

**Remaining**:
- [ ] Persist queue order to database (10%)
- [ ] Restore queue on app restart

**Files Created**:
- ✅ `Views/Avalonia/Controls/QueuePanel.axaml`
- ✅ `Views/Avalonia/Controls/QueuePanel.axaml.cs`
- ✅ `Services/DragAdornerService.cs`
- ✅ `Views/Avalonia/PlayerControl.axaml.cs`

**Backend Status**: ✅ Complete (PlayerViewModel has full queue logic)

---

### 1.2 Now Playing View (3 hours)
**Priority**: ⭐⭐⭐ CRITICAL

**What to Build**:
- [ ] Full-screen now playing view
- [ ] Large album art display (300x300px)
- [ ] Track progress bar with time
- [ ] Next/Previous buttons
- [ ] Shuffle toggle
- [ ] Repeat toggle (off/one/all)
- [ ] Volume slider
- [ ] Like/favorite button

**Files to Create/Modify**:
- `Views/Avalonia/NowPlayingPage.axaml` (new)
- `PlayerViewModel.cs` (add shuffle, repeat logic)
- `MainViewModel.cs` (add navigation to now playing)

**Backend Status**: ✅ PlayerViewModel ready

**Implementation Steps**:
1. Create `NowPlayingPage.axaml` with large layout
2. Add album art image binding
3. Implement progress bar with seek
4. Add shuffle/repeat state to PlayerViewModel
5. Wire up all playback controls
6. Add navigation from mini player
7. Test all playback modes

---

### 1.3 Playlist History UI (30 minutes)
**Priority**: ⭐⭐⭐ QUICK WIN

**What to Build**:
- [ ] Small 📜 icon next to each playlist name
- [ ] Click → show activity log panel
- [ ] Timeline view with timestamps
- [ ] Filter by action type
- [ ] Show: Added, Removed, Moved, Created, Deleted

**Files to Create/Modify**:
- `Views/Avalonia/PlaylistHistoryPanel.axaml` (new)
- `ViewModels/PlaylistHistoryViewModel.cs` (new)
- `LibraryPage.axaml` (add history icon)

**Backend Status**: ✅ ActivityLogs table ready

**Implementation Steps**:
1. Create `PlaylistHistoryViewModel` loading from `ActivityLogs`
2. Create `PlaylistHistoryPanel.axaml` with timeline UI
3. Add small icon to playlist list items
4. Wire up click event
5. Test with existing activity data

---

### 1.4 Album Detail View (45 minutes)
**Priority**: ⭐⭐ QUICK WIN

**What to Build**:
- [ ] Click album in search results → show detail
- [ ] Album detail panel/page
- [ ] Track list with all album tracks
- [ ] Download all button
- [ ] Album metadata (artist, year, track count)

**Files to Create/Modify**:
- `Views/Avalonia/AlbumDetailPanel.axaml` (new)
- `ViewModels/AlbumDetailViewModel.cs` (new)
- `SearchPage.axaml` (add click handler)

**Backend Status**: ✅ AlbumResults ready

**Implementation Steps**:
1. Create `AlbumDetailViewModel` with track list
2. Create `AlbumDetailPanel.axaml` UI
3. Add click event to album grid
4. Show panel/navigate to detail
5. Test download all functionality

---

## Phase 2: Enhanced Features (6 hours) 🟡

### 2.1 Search Filters (1 hour)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Format filter dropdown (MP3, FLAC, AAC, etc.)
- [ ] Bitrate filter (128k, 192k, 320k, lossless)
- [ ] File size filter
- [ ] Duration filter
- [ ] Clear filters button

**Files to Modify**:
- `SearchPage.axaml` (add filter UI)
- `MainViewModel.cs` (add filter logic)

**Backend Status**: ✅ Filter logic exists

---

### 2.2 Smart Playlists (3 hours)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Recently Added (last 30 days)
- [ ] Most Played (track play count)
- [ ] Favorites/Liked Songs
- [ ] Failed Downloads
- [ ] High Quality (FLAC only)
- [ ] Auto-update on library changes

**Files to Create**:
- `ViewModels/SmartPlaylistViewModel.cs`
- `Services/SmartPlaylistService.cs`

**Backend Status**: ⚠️ Need play count tracking

**Implementation**:
1. Add `PlayCount` to PlaylistTrack entity
2. Create smart playlist logic
3. Add to library sidebar
4. Auto-refresh on changes

---

### 2.3 Social Features (2 hours)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Export playlist to M3U/JSON
- [ ] Import playlist from M3U/JSON
- [ ] Share playlist (copy to clipboard)
- [ ] Collaborative playlists (advanced)

**Files to Create**:
- `Services/PlaylistExportService.cs`
- `Services/PlaylistImportService.cs`

---

## Phase 2.5: Download Orchestration & Active Management (10-12 hours) 🟠 HIGH PRIORITY

**Goal**: Transform download system from "Import → Fire-and-Forget" to "Active Orchestration" with concurrency control, resumability, and real-time visibility.

### 2.5.1 Orchestration Engine: Throttling & Queuing (3 hours)
**Priority**: ⭐⭐⭐ CRITICAL

**What to Build**:
- [ ] `SemaphoreSlim(4, 4)` throttling in `DownloadManager` to limit concurrent downloads to 4
- [ ] `ProcessQueueLoop` with fire-and-forget pattern for continuous queue processing
- [ ] Folder creation logic: `DefaultLibraryPath/ArtistName/AlbumName/` before first track starts
- [ ] `.part` file handling for resumable downloads (download to `.tmp` → rename on completion)
- [ ] File size verification for resume support (check existing `.part` file size)

**Concurrency Strategy**:
```csharp
private readonly SemaphoreSlim _downloadSemaphore = new(4, 4);

public async Task ProcessQueueLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // 1. Wait for a track to be available in the ConcurrentQueue
        if (_downloadQueue.TryDequeue(out var ctx))
        {
            // 2. Wait for one of the 4 slots to open up
            await _downloadSemaphore.WaitAsync(ct);

            // 3. Fire and forget the download task so the loop can pick up the next track
            _ = Task.Run(async () => 
            {
                try 
                {
                    await ProcessTrackWithFolderLogicAsync(ctx);
                }
                finally 
                {
                    _downloadSemaphore.Release(); // Free up the slot for the next track
                }
            }, ct);
        }
        else
        {
            await Task.Delay(500, ct); // Idle wait
        }
    }
}
```

**Files to Modify**:
- `Services/DownloadManager.cs` - Add semaphore throttling
- `Services/DownloadManager.cs` - Implement `ProcessQueueLoop`
- `Services/DownloadManager.cs` - Add `ProcessTrackWithFolderLogicAsync`

**Implementation Steps**:
1. Add `SemaphoreSlim _downloadSemaphore` field
2. Implement continuous `ProcessQueueLoop` method
3. Add folder structure creation: `Path.Combine(LibraryRoot, Artist, Album)`
4. Implement `.part` file logic for resumability
5. Add file size check for partial downloads
6. Test with 50+ track batch to verify only 4 download simultaneously

---

### 2.5.2 Persistent Transfer State & Resumability (2 hours)
**Priority**: ⭐⭐⭐ CRITICAL

**What to Build**:
- [ ] Download to `.part` or `.tmp` files to prevent corruption
- [ ] Check for existing `.part` files before starting download
- [ ] Resume logic: if `.part` exists, request remaining bytes (HTTP Range header)
- [ ] Hash verification for partial files (optional: skip if protocol doesn't support)
- [ ] Atomic rename on completion: `TrackName.mp3.part` → `TrackName.mp3`
- [ ] Database state: persist `Paused` state to DB on app close
- [ ] Auto-resume: on app restart, scan DB for `Paused`/`Pending` tracks and re-enqueue

**Resumability Logic**:
```csharp
private async Task DownloadFileAsync(PlaylistTrack track, string targetPath)
{
    var partPath = targetPath + ".part";
    long startPosition = 0;
    
    // Check if partial file exists
    if (File.Exists(partPath))
    {
        var fileInfo = new FileInfo(partPath);
        startPosition = fileInfo.Length;
        _logger.LogInformation($"Resuming download from byte {startPosition}");
    }
    
    // Download with resume support
    // (Implementation depends on Soulseek API support for partial transfers)
    await DownloadFromPositionAsync(track, partPath, startPosition);
    
    // Atomic rename on success
    File.Move(partPath, targetPath, overwrite: false);
}
```

**Files to Modify**:
- `Services/DownloadManager.cs` - Add `.part` file handling
- `Services/DownloadManager.cs` - Implement resume logic
- `Data/Entities/PlaylistTrackEntity.cs` - Ensure `Paused` state persists
- `App.axaml.cs` - Add startup logic to re-enqueue paused/pending tracks

**Implementation Steps**:
1. Modify download logic to use `.part` extension
2. Add file existence + size check before download
3. Implement HTTP Range header support (if protocol allows)
4. Add atomic rename on successful completion
5. Update `PlaylistTrackState` to persist `Paused` state
6. Add `RestorePendingDownloadsAsync()` method called on app startup
7. Test resume after app crash/restart

---

### 2.5.3 Global Download Dashboard UI (4 hours)
**Priority**: ⭐⭐⭐ HIGH

**What to Build**:
- [ ] **Global Status Bar**: Persistent download indicator in sidebar/top nav
  - Visual: Download icon (↓) with badge showing active count (e.g., "↓ 4")
  - Click → opens Download Center overlay
- [ ] **Download Center View**: New overlay/page aggregating all active/pending tracks
  - **Active List**: 4 tracks currently "In Flight" with real-time progress bars
  - **Pending Queue**: Scrollable list of "Next Up" tracks
  - **Global Controls**: "Pause All" / "Resume All" buttons
  - **Individual Controls**: Pause/Cancel specific tracks
- [ ] **Continue Logic**: When user hits "Continue" on paused album
  - Re-add tracks to `_downloadQueue`
  - `DownloadFileAsync` checks for existing `.part` files
  - Attempt to append or verify stream to prevent full re-download

**UI Components**:
```xml
<!-- Global Status Indicator (TopBar or Sidebar) -->
<Button Classes="download-indicator">
    <StackPanel Orientation="Horizontal">
        <PathIcon Data="{StaticResource DownloadIcon}" />
        <TextBlock Text="{Binding ActiveDownloadCount}" />
    </StackPanel>
</Button>

<!-- Download Center Panel -->
<Panel IsVisible="{Binding IsDownloadCenterOpen}">
    <!-- Active Downloads (max 4) -->
    <ItemsControl Items="{Binding ActiveDownloads}">
        <ItemTemplate>
            <Border Classes="download-card">
                <ProgressBar Value="{Binding PercentComplete}" />
                <TextBlock Text="{Binding TrackTitle}" />
            </Border>
        </ItemTemplate>
    </ItemsControl>
    
    <!-- Pending Queue -->
    <ItemsControl Items="{Binding PendingDownloads}">
        <!-- Pending track list -->
    </ItemsControl>
    
    <!-- Global Controls -->
    <StackPanel Orientation="Horizontal">
        <Button Command="{Binding PauseAllCommand}">Pause All</Button>
        <Button Command="{Binding ResumeAllCommand}">Resume All</Button>
    </StackPanel>
</Panel>
```

**Files to Create**:
- `Views/Avalonia/DownloadCenterPanel.axaml` (new)
- `ViewModels/DownloadCenterViewModel.cs` (new)

**Files to Modify**:
- `Views/Avalonia/MainWindow.axaml` - Add global download indicator
- `ViewModels/MainViewModel.cs` - Add `IsDownloadCenterOpen` property
- `Services/DownloadManager.cs` - Add `GetActiveDownloads()` and `GetPendingDownloads()` queries

**Implementation Steps**:
1. Create `DownloadCenterViewModel` that subscribes to `TrackStateChangedEvent`
2. Filter events to only show tracks with state `Downloading` or `Pending`
3. Create `DownloadCenterPanel.axaml` with active/pending sections
4. Add global status indicator to main window sidebar
5. Implement "Pause All" / "Resume All" commands
6. Add "Continue" button logic to re-enqueue paused tracks
7. Test with 50 tracks to verify only 4 active at once

---

### 2.5.4 Album Card Progress Indicators (1 hour)
**Priority**: ⭐⭐ MEDIUM

**What to Build**:
- [ ] **Summary Progress**: Add "12/15 Tracks Downloaded" text to album cards
- [ ] **Visual Progress Bar**: Small progress bar at bottom of album card
- [ ] **State Badge**: Color-coded badge (Downloading/Paused/Complete/Failed)
- [ ] **Hover Details**: Tooltip showing detailed download status

**Files to Modify**:
- `Views/Avalonia/LibraryPage.axaml` - Update album card template
- `ViewModels/ProjectViewModel.cs` - Add `DownloadProgress` computed property
- `Services/DownloadManager.cs` - Publish album-level progress events

**Implementation Steps**:
1. Add `TracksDownloaded` and `TotalTracks` properties to `ProjectViewModel`
2. Create computed property: `DownloadProgressText` (e.g., "12/15")
3. Add progress bar to album card XAML
4. Bind progress bar to `(TracksDownloaded / TotalTracks) * 100`
5. Test with multiple albums downloading simultaneously

---

### 2.5.5 Pause/Resume Persistence (2 hours)
**Priority**: ⭐⭐ MEDIUM

**What to Build**:
- [ ] Update `PlaylistTrackState` in DB when "Paused" is clicked
- [ ] On app startup, scan DB for `Paused` or `Pending` tracks
- [ ] Auto re-enqueue paused/pending tracks into `_downloadQueue`
- [ ] Show "Resuming X downloads..." notification on startup

**Files to Modify**:
- `Services/DownloadManager.cs` - Add `PauseTrackAsync(trackId)` method
- `Services/DownloadManager.cs` - Add `RestorePendingDownloadsAsync()` method
- `Services/DatabaseService.cs` - Add `GetPausedTracksAsync()` query
- `App.axaml.cs` - Call `RestorePendingDownloadsAsync()` on startup

**Implementation Steps**:
1. Create `PauseTrackAsync` that updates DB state to `Paused`
2. Create `GetPausedTracksAsync()` query in `DatabaseService`
3. Implement `RestorePendingDownloadsAsync()` to re-enqueue tracks
4. Call restore method in `App.OnStartup`
5. Add notification message: "Resuming 12 paused downloads..."
6. Test by pausing downloads, closing app, and restarting

---

**Total Estimated Time**: 10-12 hours  
**Impact**: Complete overhaul of download system with professional-grade orchestration, reliability, and user visibility

---

## Phase 3: Architecture & Refactoring (12 hours) 🟣 TECHNICAL

### 3.1 DownloadManager Refactor (4 hours)
**Priority**: ⭐⭐⭐ HIGH
**Goal**: Split "God Object" into focused services.

**What to Build**:
- [ ] `DownloadDiscoveryService`: Handle search & selection logic
- [ ] `MetadataEnrichmentOrchestrator`: dedicated background enrichment
- [ ] `DownloadStateMachine`: Manage Pending -> Searching -> Downloading transitions

**Files**:
- `Services/DownloadDiscoveryService.cs` (new)
- `Services/MetadataEnrichmentOrchestrator.cs` (new)
- `Services/DownloadManager.cs` (cleanup)

### 3.2 Event-Driven UI Architecture (3 hours)
**Priority**: ⭐⭐⭐ HIGH
**Goal**: Decouple logic from UI threads and eliminate stuttering.

**What to Build**:
- [ ] Migrate `DownloadManager` to publish `TrackStatusChangedEvent`
- [ ] Migrate `LibraryService` to publish `LibraryUpdatedEvent`
- [ ] Update ViewModels to subscribe via `IEventBus`
- [ ] Remove `Dispatcher.UIThread` calls from Services

**Files**:
- `Services/EventBusService.cs` (existing)
- `Events/TrackEvents.cs` (new)
- `ViewModels/PlaylistTrackViewModel.cs` (subscribe)

### 3.3 Data Access Layer Thinning (1 hour)
**Priority**: ⭐⭐ MEDIUM
**Goal**: Cleaner service code by extracting mapping logic.

**What to Build**:
- [ ] `Extensions/MappingExtensions.cs`
- [ ] Move `EntityToPlaylistTrack` logic to extensions
- [ ] Move `PlaylistTrackToEntity` logic to extensions

### 3.4 Generic OAuth Loopback (2 hours)
**Priority**: ⭐⭐ MEDIUM
**Goal**: Reusable OAuth listener for future integrations.

**What to Build**:
- [ ] Generic `OAuthLoopbackServer`
- [ ] Configurable HTML success/failure pages
- [ ] `NameValueCollection` return type from `WaitForCallbackAsync`

### 3.5 Input Processing Service (3 hours)
**Priority**: ⭐ LOW
**Goal**: Decouple ViewModels from specific input formats.

**What to Build**:
- [ ] `InputAbstractionService`: Unify CSV, Clipboard, and URL handling
- [ ] `SearchIntent`: Standardized input object

---

## Phase 4: Polish Features (22 hours) 🟢

### 3.1 Lyrics Display (4 hours)
- [ ] Fetch lyrics from API (Genius, Musixmatch)
- [ ] Display in now playing view
- [ ] Scroll sync (optional)
- [ ] Cache lyrics locally

### 3.2 Crossfade (4 hours)
- [ ] Crossfade duration setting (0-12s)
- [ ] Smooth transitions between tracks
- [ ] LibVLC audio mixing

### 3.3 Equalizer (6 hours)
- [ ] 10-band EQ UI
- [ ] Presets (Rock, Pop, Jazz, etc.)
- [ ] Save custom presets
- [ ] LibVLC audio filters

### 3.4 Visualizer (8 hours)
- [ ] Audio spectrum analyzer
- [ ] Waveform display
- [ ] Multiple visualizer styles
- [ ] SkiaSharp rendering

---

## Phase 2B: Data Management & Resilience (8 hours) 🟡

### 2.4 DB Backup & Restore (2 hours)
**Priority**: ⭐⭐⭐ CRITICAL

**What to Build**:
- [ ] Backup library.db to external drive
- [ ] Backup config.ini with database
- [ ] Restore from backup
- [ ] Scheduled auto-backup option
- [ ] Backup verification

**Files to Create**:
- `Services/BackupService.cs` (new)
- `Views/Avalonia/BackupPage.axaml` (new)

**Implementation**:
1. Use SQLite `VACUUM INTO` or `File.Copy`
2. Create BackupService with atomic backup
3. Add backup UI to Settings page
4. Implement restore with validation
5. Add auto-backup scheduler

---

### 2.5 DB Import/Export JSON (2 hours)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Export all playlists to JSON
- [ ] Export library entries to JSON
- [ ] Import from JSON (merge or replace)
- [ ] Portable library format

**Files to Modify**:
- `Services/LibraryService.cs` (add Export/Import methods)

**Implementation**:
1. Serialize PlaylistJob + PlaylistTrack to JSON
2. Include metadata and relationships
3. Import with duplicate detection
4. Validate JSON schema on import

---

### 2.6 Library Indexing Optimization (1 hour)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Add database indexes for faster queries
- [ ] Index on PlaylistId, Status, TrackUniqueHash
- [ ] Index on CreatedAt for sorting
- [ ] Measure query performance improvement

**Files to Modify**:
- `Data/AppDbContext.cs` (add indexes)

**Implementation**:
```csharp
modelBuilder.Entity<PlaylistTrackEntity>()
    .HasIndex(t => t.PlaylistId);
modelBuilder.Entity<PlaylistTrackEntity>()
    .HasIndex(t => t.Status);
modelBuilder.Entity<PlaylistTrackEntity>()
    .HasIndex(t => t.TrackUniqueHash);
modelBuilder.Entity<PlaylistJobEntity>()
    .HasIndex(j => j.CreatedAt);
```

---

### 2.7 True Pause/Resume Downloads (3 hours)
**Priority**: ⭐⭐⭐

**What to Build**:
- [ ] Check for .part files on resume
- [ ] Resume from file size offset
- [ ] Validate partial downloads
- [ ] Handle corrupted .part files

**Files to Modify**:
- `Services/DownloadManager.cs`
- `Services/SoulseekAdapter.cs` (add startOffset support)

**Implementation**:
1. Check if .part file exists before download
2. Get file size as resume offset
3. Pass offset to Soulseek download
4. Verify file integrity after resume
5. Delete .part if corrupted

---

## Phase 4: Advanced Features (Future)

### 4.1 Rekordbox Export
- [ ] Export to Rekordbox XML
- [ ] Preserve playlists, cues, metadata

### 4.2 Mobile Sync
- [ ] Export library to mobile device
- [ ] Sync playlists

### 4.3 Cloud Backup
- [ ] Backup library to cloud
- [ ] Restore from backup

---

## Phase 5: Quality Control & Replacement System (16 hours) 🔵

### 5.1 Audio Fingerprinting (4 hours)
**Priority**: ⭐⭐⭐ ADVANCED

**What to Build**:
- [ ] Integrate SoundFingerprinting library
- [ ] Generate fingerprint hash for each track
- [ ] Store fingerprint in database
- [ ] Compare fingerprints for duplicates

**Files to Create**:
- `Services/FingerprintService.cs` (new)
- Add NuGet: `SoundFingerprinting` (MIT license)

**Implementation**:
1. Add SoundFingerprinting NuGet package
2. Create FingerprintService
3. Generate hash post-download
4. Store in LibraryEntry.FingerprintHash
5. Compare hashes for perceptual duplicates

---

### 5.2 Track Quality Data Model (2 hours)
**Priority**: ⭐⭐⭐

**What to Build**:
- [ ] Add FingerprintHash to LibraryEntry
- [ ] Add BitrateScore property
- [ ] Add FingerprintQuality property
- [ ] Add QualityFlags enum

**Files to Modify**:
- `Models/LibraryEntry.cs`
- `Models/PlaylistTrack.cs`
- `Data/Entities/` (corresponding entities)

**Implementation**:
```csharp
public string? FingerprintHash { get; set; }
public int BitrateScore { get; set; }
public double FingerprintQuality { get; set; }
public QualityFlags Flags { get; set; }
```

---

### 5.3 Quality Analysis Service (6 hours)
**Priority**: ⭐⭐⭐

**What to Build**:
- [x] **Phase 5: Industrial Reliability (Hardening)**
  - [x] **Ghost File Prevention**: FileLockMonitor with Pre-Flight Retry & Deferral Persistence.
  - [x] **Spotify Quota**: Circuit Breaker & Cache-First Proxy for Inspector.
  - [x] **Atomic Swaps**: Same-drive MFT vs Cross-drive Verify-Copy checks.
  - [x] **Phase 5C: Industrial Hardening**
    - [x] **Security**: Implement DPAPI for token storage.
    - [x] **Resources**: Implement Zombie Process killer for FFmpeg.
    - [x] **Database**: Implement WAL Checkpoint on shutdown.
    - [x] **Stability**: Implement UI Throttling and Semaphore Timeouts.view

- [ ] **Phase 6: Mission Control "Command Center"**
  - [ ] **Architecture**
    - [ ] **Schema**: Create `DashboardSnapshots` table (JSON blobs for pre-computed stats).
    - [ ] **Service**: Implement `MissionControlService` (Aggregator Facade).
    - [ ] **Throttling**: Ensure UI only receives updates at 4fps via BatchedAdapter.
  - [ ] **Features**
    - [ ] **Live Ops Grid**: Virtualized panel for active transfers/searches.
    - [ ] **Genre Galaxy**: Integrate LiveCharts2 for library visualization.
    - [ ] **Missions**: Implement "Monthly Drop" command journey.
    - [ ] **Vibe Search**: NLP parser for context queries.
- [ ] Suggest higher quality replacements
- [ ] Auto-replace option (with confirmation)

**Files to Create**:
- `Services/QualityAnalysisService.cs` (new)

**Implementation**:
1. Run analysis after download completes
2. Check fingerprint against all LibraryEntry
3. Compare quality metrics (bitrate, format, length)
4. Flag duplicates with lower quality
5. Suggest replacement candidates
6. Log analysis results

---

### 5.4 Quality Review UI (4 hours)
**Priority**: ⭐⭐

**What to Build**:
- [ ] "Library Health" page
- [ ] List flagged duplicates
- [ ] Show quality comparison
- [ ] Replace/Keep/Delete actions
- [ ] Batch operations

**Files to Create**:
- `Views/Avalonia/QualityReviewPage.axaml` (new)
- `ViewModels/QualityReviewViewModel.cs` (new)

**Implementation**:
1. Create QualityReviewViewModel
2. Load flagged tracks from database
3. Display side-by-side comparison
4. Implement replace logic
5. Add to navigation

---

### 5.5 Advanced Audio Analysis (Musical Key & Cues) (8 hours)
**Priority**: ⭐⭐⭐ DIFFERETIATOR

**What to Build**:
- [ ] Chromagram Analysis (Essentia/LibRosa wrapper)
- [ ] Key Detection (Camelot wheel notation)
- [ ] Cue Point Extraction (Rekordbox XML & ID3 GEOB tags)
- [ ] `CuePoint` data model

**Files to Create**:
- `Services/AudioAnalysisService.cs` (new)
- `Services/RekordboxXmlImporter.cs` (new)
- `Models/CuePoint.cs` (new)

**Implementation**:
1. Integrate audio analysis library (Essentia/LibRosa)
2. Implement key detection algorithm
3. Store Average Harmonic Profile in database
4. Extract cue points from existing files/XML
5. Parse `Pioneer DJ Cue` GEOB tags from ID3

---

### 5.6 Self-Healing Migration System (10 hours)
**Priority**: ⭐⭐⭐ REVOLUTIONARY

**What to Build**:
- [ ] "The Replacement Logic": Move cues from MP3 to FLAC
- [ ] Cross-Correlation time alignment (fix silence offsets)
- [ ] Automatic cue point shifting (+/- ms)
- [ ] Rekordbox XML Export with new file paths

**Implementation Advice**:
1. **Acoustic Match**: Verify `FingerprintService` match first.
2. **Time Alignment**: Calculate offset between signals.
3. **Migration**: Apply offset to old cue points -> save to new track.
4. **Validation**: "Library Health" dashboard approval step.


---

## Implementation Priority

### Week 1 (6 hours)
1. Queue management (2h)
2. Now playing view (3h)
3. Playlist history UI (30m)
4. Album detail view (45m)

**Result**: 90% Spotify feature parity

### Week 2 (6 hours)
5. Search filters (1h)
6. Smart playlists (3h)
7. Social features (2h)

**Result**: Enhanced library features

### Week 3-4 (22 hours)
8. Lyrics (4h)
9. Crossfade (4h)
10. Equalizer (6h)
11. Visualizer (8h)

**Result**: Premium features

---

## Technical Debt

### Code Quality
- [ ] Add unit tests for ViewModels
- [ ] Add integration tests for services
- [ ] Document all public APIs
- [ ] Code coverage >80%

### Performance
- [ ] Lazy load large playlists
- [ ] Virtualize track lists
- [ ] Optimize database queries
- [ ] Cache album art

### UX
- [ ] Loading indicators
- [ ] Error messages
- [ ] Tooltips everywhere
- [ ] Keyboard shortcuts for all actions

---

## Success Metrics

### Current
- 70% feature parity with Spotify
- 6 playlists loaded
- Full database tracking
- Cross-platform ready

### After Phase 1
- 90% feature parity
- Queue management
- Full now playing view
- History tracking UI

### After Phase 2
- 95% feature parity
- Smart playlists
- Advanced filters
- Social features

### After Phase 3
- 100% feature parity
- Premium features
- Professional polish

---

## 📊 "The Brain" Enhancements

### Live Scoring Preview (4 hours) - NEW ⭐⭐ MEDIUM
**Problem**: Users don't understand WHY one search result ranks higher than another.  
**Solution**: Real-time transparency into "The Brain's" decision-making.

**Implementation**:
- [ ] Add hover tooltip/flyout to search result items showing score breakdown:
  ```
  SCORE BREAKDOWN
  + 2000  Free Upload Slot
  - 120   Queue Length (6 people)
  + 300   Bitrate (320kbps = excellent)
  + 150   BPM Match (128.0 vs 128.0)
  - 50    Duration Mismatch (+2s difference)
  + 200   String Similarity (95% match)
  ───────────────────────────────────
  = 2480  TOTAL SCORE (Rank #1)
  ```
- [ ] Create `Models/ScoringBreakdown.cs` with component list
- [ ] Integrate with `ResultSorter` to expose detailed scoring data
- [ ] Add flyout UI to `SearchPage.axaml`

**Impact**: Builds trust and transparency. Users understand and trust the system's decisions.

---

## 🚫 Deferred Features (Post-v1.0 Stabilization)

> [!CAUTION]
> **8-Week Feature Freeze**: The following features are valuable but DEFERRED until after v1.0 stabilization is complete. Focus on reliability and speed first.

### Deferred UX Enhancements
- **Draggable Column Reordering** (TODO line 278-281) - User customization, but not critical
- **Download Timeline Visualization** - Nice-to-have transparency feature
- **Scoring Sandbox** (Dev Tool) - Power user feature, low priority

### Deferred Search Features
- **Advanced Filtering HUD** - Bitrate sliders, format chips, user trust toggle
- **Batch Actions & Selection** - Multi-select download/add to playlist
- **Overlay Bug Fix** - Non-blocking if no user reports

### Deferred Advanced Features
- **Self-Healing Upgrade Scout UI** - Backend exists, defer UI polish
- **DJ-Focused Visuals** - Camelot wheel, waveform preview
- **Rekordbox/Denon USB Export** - Workflow integration, not core functionality

**Rationale**: These features require significant development time but don't impact core reliability. Early adopters must experience a bug-free, fast app before we add complexity.

**Re-evaluation Date**: After 8-week stabilization period (February 2026)

---

##Unique Advantages Over Spotify

1. **P2P Downloads** - Own your music forever
2. **No Subscription** - Free forever
3. **High Quality** - FLAC support
4. **Smart Ranking** - Quality detection
5. **Offline by Default** - Already downloads
6. **Privacy** - No tracking
7. **Open Source** - Full control

---

## Notes

- All backend logic is production-ready
- Database handles 1000s of tracks
- Cross-platform (Windows, macOS, Linux)
- Modern Avalonia UI
- Clean MVVM architecture

**Next Step**: Implement Phase 1 (6 hours) for 90% parity

---

# 🧬 PHASE 17: THE EDM SCIENTIST

> *"From Generic Music Analysis to Genre-Aware Intelligence"*
> 
> January 9, 2026 - A pivotal discovery session

---

## 📖 The Story

### Where We Were: The Blind Approach

Before today, our analysis pipeline was **genre-agnostic**. We used a handful of Essentia models:

- `discogs-effnet` - 519-class general genre classification
- `voice_instrumental` - Vocal detection
- `danceability` - Single danceability score
- 6 individual `mood_*` models - Happy, Sad, Aggressive, etc. (slow, redundant)

**The Problem**: This approach treated DnB the same as Jazz. It couldn't:
- Distinguish between House and Techno (both are "Electronic")
- Identify DJ Tools vs. full tracks
- Provide meaningful emotional mapping for dance music

### The Discovery: EDM Specialist Models

We analyzed our model inventory and discovered **hidden gems**:

```
Tools/Essentia/models/
├── genre_electronic-musicnn-msd-2.pb   <-- EDM SPECIALIST (DnB/House/Techno/Trance/Ambient)
├── tonal_atonal-musicnn-msd-2.pb       <-- DJ TOOL DETECTOR
├── emomusic-msd-musicnn-2.pb           <-- Better than 6 mood models
└── 38 other models...
```

### The Upgrade: Arousal/Valence 2D Vibe Map

Instead of discrete labels ("Happy", "Sad"), we now use **Russell's Circumplex Model**:

```
                    HIGH AROUSAL (9)
                         ↑
              ┌─────────────────────────┐
              │ Dark     │   Festival   │
              │ (Techno) │   (Mainstage)│
              │          │              │
LOW VALENCE ←─┼──────────┼──────────────┼→ HIGH VALENCE
    (1)       │          │              │      (9)
              │Melancholic│    Chill    │
              │(Downtempo)│  (Sunset)   │
              └─────────────────────────┘
                         ↓
                    LOW AROUSAL (1)
```

---

## 🔧 What Was Implemented

### 1. Updated `profile.yaml`

```yaml
highlevel:
    compute: true
    tensorflow_models:
        # CORE BASICS
        - "models/discogs-effnet-bs64-1.pb"
        - "models/voice_instrumental-msd-musicnn-1.pb"
        - "models/danceability-msd-musicnn-1.pb"
        
        # EDM SPECIALISTS (NEW)
        - "models/genre_electronic-musicnn-msd-2.pb"
        - "models/tonal_atonal-musicnn-msd-1.pb"
        - "models/arousal_valence-muse-musicnn-msd-1.pb"  # Missing - needs download
```

### 2. New `AudioFeaturesEntity` Properties

| Property                       | Type        | Source Model     | Purpose                        |
| ------------------------------ | ----------- | ---------------- | ------------------------------ |
| `Arousal`                      | float (1-9) | arousal_valence  | Energy/Intensity axis          |
| `Valence`                      | float (1-9) | arousal_valence  | Emotional tone axis            |
| `ElectronicSubgenre`           | string      | genre_electronic | "DnB", "House", "Techno", etc. |
| `ElectronicSubgenreConfidence` | float       | genre_electronic | Confidence score               |
| `IsDjTool`                     | bool        | tonal_atonal     | True if Atonal > 0.8           |
| `TonalProbability`             | float       | tonal_atonal     | Melodic content score          |

### 3. Updated `EssentiaAnalyzerService.cs`

New helper methods parse the model outputs:

- `ExtractElectronicSubgenre()` - Picks highest confidence genre
- `ExtractTonalAtonal()` - Returns tonal/atonal probabilities
- `ExtractArousalValence()` - Returns 2D coordinates
- `MapArousalValenceToMood()` - Maps quadrant to label (Festival/Dark/Chill/Melancholic)

---

## 📦 Model Inventory (41 Files)

### Currently Integrated
| Model                                 | Size   | Status   |
| ------------------------------------- | ------ | -------- |
| `discogs-effnet-bs64-1.pb`            | 18 MB  | ✅ Active |
| `voice_instrumental-msd-musicnn-1.pb` | 82 KB  | ✅ Active |
| `danceability-msd-musicnn-1.pb`       | 82 KB  | ✅ Active |
| `genre_electronic-musicnn-msd-2.pb`   | 3.2 MB | ✅ NEW    |
| `tonal_atonal-musicnn-msd-2.pb`       | 3.2 MB | ✅ NEW    |

### Available But Not Used
| Model                                   | Size   | Potential Use                |
| --------------------------------------- | ------ | ---------------------------- |
| `emomusic-msd-musicnn-2.pb`             | 82 KB  | Alternative emotion detector |
| `genre_discogs400-discogs-effnet-1.pb`  | 2 MB   | 400-class genre (overkill)   |
| `mtg_jamendo_genre-discogs-effnet-1.pb` | 2.8 MB | Jamendo tagging              |
| `crepe-full-1.pb`                       | 89 MB  | Pitch detection (slow)       |
| `spleeter-5s-3.pb`                      | 197 MB | Stem separation (very slow)  |

### ⚠️ Missing (Needs Download)
| Model                                   | Purpose                                  |
| --------------------------------------- | ---------------------------------------- |
| `arousal_valence-muse-musicnn-msd-1.pb` | 2D Vibe Map (referenced but not present) |

## 🧠 Musical Brain 2026: Mission Control & Scalability

**Goal**: Transform the analysis system into a world-class, fluid Pro-DJ command center that handles 100k+ tracks with DAW-level visual fidelity.

### Phase 25: Mission Control Modernization
- [ ] **Forensic Monitor**: "Human-in-the-loop" UI to explain AI rankings and allow manual score overrides for low-confidence detections.
- [ ] **Glass Engine Dashboard**: Visual "Vibe Clusters" showing real-time batch progress with glowing 60fps animations (Avalonia Composition API).
- [ ] **Semantic Theming**: Dynamic row colors based on AI Confidence Scores (High Confidence = Green, Review Needed = Amber).
- [ ] **Command Bar (K-Style)**: `Ctrl+K` palette for operations like "Analyze last import", "Clear failed", or "Boost priority: Bass House".
- [ ] **Sonic Health Gauge**: UI widget showing % of library with High-Fidelity vs. Low-Fidelity metadata/integrity status.

### Phase 26: Cloud-Scale Performance (100k+ Tracks)
- [ ] **Virtualization 2.0**: Shimmer loading, asynchronous prefetching, and delta-updates to ensure fluid scrolling in massive libraries.
- [ ] **Headless Worker**: Decouple `AnalysisQueueService` into a background process that survives main UI crashes.
- [ ] **Batch Refiner**: Bulk correction UI to "re-bias" AI scores for hundreds of tracks simultaneously.

### Phase 27: Forensic Safety & Feedback
- [ ] **Forensic Lockdown**: UI "Safe Mode" that auto-throttles heavy AI tasks if CPU load from the Audio Player/ASIO is too high.
- [ ] **Spectrogram Comparison**: Forensic view to deep-dive into AI detection logic vs. raw spectral signal.
- [ ] **Haptic/Audio Feedback**: Subtle auditory cues when large analysis batches complete.

---

## 🔮 What's Next: Phase 17.1

### Genre-Aware Cue Points (Completed Architecture)

Created entities and services for genre-specific DJ cue generation:

| Component                                               | Status    |
| ------------------------------------------------------- | --------- |
| `TrackPhraseEntity.cs`                                  | ✅ Created |
| `GenreCueTemplateEntity.cs`                             | ✅ Created |
| `PhraseDetectionService.cs`                             | ✅ Created |
| `CueGenerationService.cs`                               | ✅ Created |
| Default Templates (DnB, House, Techno, Dubstep, Trance) | ✅ Created |
| UI Integration                                          | ⏳ Pending |

### Phase 17.2: Forensic Lab Upgrades

- [ ] **Vibe Radar**: 2D scatter plot of Arousal vs. Valence
- [ ] **Subgenre Badge**: Show detected electronic subgenre
- [ ] **DJ Tool Flag**: Visual indicator for rhythmic-only tracks
- [ ] **Cue Point Timeline**: Visual phrase markers on waveform

---

## 🎯 Key Learnings

1. **MAEST ≠ Maersk**: The "Maersk" model is actually MAEST (Music Audio Efficient Spectrogram Transformer) - a heavy Transformer model, not a shipping company.

2. **Specialist > Generalist**: `genre_electronic` is better for EDM than `discogs-effnet` because it's trained only on electronic music.

3. **Continuous > Discrete**: `arousal_valence` (1 model) replaces 6 `mood_*` models and provides richer data.

4. **Tonal/Atonal = DJ Tool Detection**: A brilliant hack - if a track is >80% atonal, it's likely a drum loop or DJ tool.

---

## ✅ Build Status

```
Build succeeded with 15 warning(s) in 12.1s
0 Errors
```

All new code is integrated and compiling. Ready for runtime testing.
