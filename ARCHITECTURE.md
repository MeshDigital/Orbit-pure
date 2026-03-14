# SLSKDONET Architecture & Data Flow

## System Overview

```
┌─────────────────────────────────────────────────────────────┐
│                         UI Layer                            │
│  ┌─────────────────────────────┐   ┌──────────────────────┐ │
│  │ MainWindow (navigation shell)│  │ Avalonia Pages (Search,│ │
│  │ ├─ NavigationService         │  │ Library, Downloads,   │ │
│  │ ├─ PlayerViewModel           │  │ Settings, TrackInspector)│ │
│  │ └─ Drag-and-Drop Adorners    │  │                      │ │
│  └──────────────┬───────────────┘   └───────────┬──────────┘ │
│                 │                               │            │
│                 ▼                               ▼            │
│        ┌─────────────────────────────────────────────────┐   │
│        │              MainViewModel (Coordinator)        │   │
│        │  - Coordinates navigation & global state        │   │
│        │  - Delegates to child ViewModels                │   │
│        └───────────────┬─────────────────────────────────┘   │
└────────────────────────┼──────────────────────────────────────┘
                         │
        ┌────────────────▼────────────────┐
        │   Application Services          │
        │  DownloadManager (Multi-Lane) 🚥│
        │  DownloadHealthMonitor 💚      │
        │  AnalysisQueueService 🧠        │
        │  EssentiaAnalyzerService 🎛️     │
        │  CrashRecoveryService 🛡️        │
        │  SonicIntegrityService ✨       │
        │  PersonalClassifierService (ML) 🧠|
        │  SearchOrchestrator 🧠          │
        │  LibraryService                 │
        │  RekordboxService 📀           │
        └────────────┼────────────────┘────┘
                     │
        ┌────────────▼────────────────┐
        │ Infrastructure Layer        │
        │  SoulseekAdapter            │
        │  CrashRecoveryJournal 🛡️    │
        │  DatabaseService (EF Core)  │
        │  ConfigManager              │
        └─────────────────────────────┘
```

🛡️ **Phase 2A (Recovery)** | 💚 **Phase 3B (Health Monitor)** | 📀 **Phase 4 (Rekordbox Export)** | ✨ **Phase 8 (Sonic Integrity)** | 🧠 **Phase 1 (The Brain)**

---

## 🧠 The Brain: Intelligent Ranking Strategy

ORBIT replaces the standard "sort by bitrate" logic with a sophisticated **Strategy Pattern** implementation.

### Ranking Components
1.  **SearchOrchestrator**: Manages search sessions and results.
2.  **ISortingStrategy**: Interface for swapping ranking logic.
    *   `BalancedSortingStrategy`: (Default) Quality > Speed > Metadata.
    *   *Upcoming*: `AudiophileStrategy` (FLAC only), `SpeedStrategy` (Fastest only).
3.  **ScoringConstants**: Centralized configuration for weights (e.g., `FLAC_SCORE = 450`).

### The Scoring Pipeline
Every search result flows through a weighted scoring system:
```
Total Score = (BitrateScore * Weight) 
            + (AvailabilityScore * Weight) 
            + (MusicalMatchScore * Weight) 
            - (PenaltyScore)
```
*   **Tier 1 (Quality)**: Prioritizes 320kbps/FLAC.
*   **Tier 2 (Intelligence)**: Boosts matches for BPM/Key (if Spotify enrichment active).
*   **Tier 3 (Gating)**: Hides "Fake" files (VBR detected) or duration mismatches.

---

## 🔍 Search Results UI Pipeline

The Search page uses a reactive pipeline to keep the TreeDataGrid synchronized without cross-thread updates.

```
SearchOrchestrationService
    │
    ▼
SourceList<AnalyzedSearchResultViewModel>
    │  Filter + Sort (SearchFilterViewModel)
    ▼
ReadOnlyObservableCollection (public results)
    │  Sync to writable view collection
    ▼
SearchResultsView (ObservableCollection)
    │
    ▼
TreeDataGrid (FlatTreeDataGridSource)
```

### Key Constraints
- **UI Thread Safety**: Result batches are marshaled to the UI thread before mutating the underlying SourceList.
- **TreeDataGrid Binding**: Column getters use simple member accessors (no method calls or inline formatting) to avoid expression parsing failures.

## 🧠 The Cortex: ML.NET Engine (Phase 15.5)

Phase 15.5 introduced a local machine learning layer that runs parallel to the rule-based ranking system.

*   **Engine**: Microsoft ML.NET (v3.0) + LightGBM.
*   **Input**: 128-dimensional EffNet-b0 embeddings from Essentia.
*   **Function**: Predicts "Style/Vibe" based on user-trained buckets (Style Lab).
*   **Service**: `PersonalClassifierService` acts as the inference engine.
*   **Data**: Stores embeddings in `AudioFeaturesEntity.AiEmbeddingJson` to allow instant retraining without re-analyzing audio.

> **Deep Dive**: See [ML_ENGINE_ARCHITECTURE.md](DOCS/ML_ENGINE_ARCHITECTURE.md) for the full breakdown.

---

## 🛡️ Ironclad Recovery Architecture

Phase 2A introduced a database-journaled recovery system to ensure **zero data loss**.

### Journal-First Pattern
All destructive operations follow the **Prepare → Log → Execute → Commit** lifecycle:
1.  **Prepare**: Gather state (target paths, timestamps).
2.  **Log**: Write serialized intent to `CrashRecoveryJournal` (SQLite).
3.  **Execute**: Perform volatile I/O (Download, File.Move).
4.  **Commit**: Delete journal entry upon success.

### Components
*   **CrashRecoveryJournal**:
    *   Backed by a dedicated `Microsoft.Data.Sqlite` connection.
    *   Runs in **WAL (Write-Ahead Logging)** mode for non-blocking writes.
    *   Uses **Prepared Statements** for <1ms latency.
*   **DownloadHeartbeat**:
    *   `DownloadManager` runs a `PeriodicTimer` (15s) for active downloads.
    *   Updates journal with bytes received using **Interlocked** thread safety.
    *   **Stall Detection**: Flags downloads with 0 progress over 60s.
*   **SafeWriteService**:
    *   Wraps tag writing in atomic transactions.
    *   Ensures timestamps are preserved even after crash recovery.

---

## 🔊 Audio Layer (High-Fidelity Engine)

Phase 5B introduced a low-latency audio processing pipeline powered by **NAudio**.

### Signal Chain
```
[File] → (AudioFileReader) → [Sample Data] → (SampleChannel) → (MeteringSampleProvider) → [Output Device]
                                                                        │
                                                                        └──▶ [AudioLevelsChanged Event]
                                                                                      │
                                                                        ┌─────────────▼─────────────┐
                                                                        │      PlayerViewModel      │
                                                                        │ (Peak Left / peak Right)  │
                                                                        └─────────────┬─────────────┘
                                                                                      ▼
                                                                        [UI: Dual VU Meters]
```

### Components
- **IAudioPlayerService**: Abstracted interface for playback controls and metrics.
- **NAudio Provider**: Uses `WaveOutEvent` with custom latency settings (100ms) to balance CPU usage and UI responsiveness.
- **MeteringSampleProvider**: A non-blocking wrapper that calculates RMS and Peak levels on a secondary thread.
- **Custom WaveformControl**: A direct-drawing Avalonia control that renders pre-parsed Rekordbox `PWAV` data or generates peaks on-the-fly.

### Analysis Preservation (RAP)
The audio layer is deeply integrated with the `Rekordbox.AnlzFileParser`:
- **Probing Service**: Automatically scans for `.DAT/.EXT` companion files.
- **Binary Extraction**: Extracts high-resolution waveforms and cue points without re-analyzing audio.
- **XOR Descrambler**: Decrypts phrase data for song structure visualization.

---

## 🚥 Multi-Lane Priority Engine (Phase 3C)

The `DownloadManager` uses a weighted semaphore system to prevent "Traffic Jams" (large imports blocking single downloads).

### Priority Lanes
1.  **Lane A (Express)**: `Priority = 0`. Reserved for user-initiated single downloads or "Prioritized" playlists.
    *   **Allocation**: 2 slots guaranteed + can steal unused Standard/Background slots.
    *   **Preemption**: If all 4 slots are full, a High Priority task will **PAUSE** the lowest priority active task to force its way in.
2.  **Lane B (Standard)**: `Priority = 1`. Default for CSV/Spotify imports.
    *   **Allocation**: Max 2 slots concurrent.
3.  **Lane C (Background)**: `Priority >= 10`. Large bulk tasks.
    *   **Allocation**: Fills remaining capacity only.

### Logic Flow
`ProcessQueueLoop` → `SelectNextTrackWithLaneAllocation` → Check Slots → `WaitAsync(Semaphore)` → `Start Download`.

---

## 💾 Persistence Layer

### Database Strategy
ORBIT uses a dual-connection strategy to separate high-frequency journaling from standard metadata queries.

1.  **AppDbContext (EF Core)**:
    *   Manages Library, Playlists, and History.
    *   Optimized for complex LINQ queries.
2.  **CrashRecoveryJournal (Valid ADO.NET)**:
    *   Manages `RecoveryCheckpoints`.
    *   Optimized for raw write speed and concurrency.

### Configuration
*   **Mode**: `JournalMode=WAL` (Write-Ahead Logging).
*   **Sync**: `Synchronous=NORMAL` (Balances safety with speed).
*   **Cache**: 10MB shared cache.
*   **Auto-Checkpoint**: Triggered at 1000 pages.

---

## 🎧 Phase 8: Sonic Integrity Architecture

### Producer-Consumer Pattern
```
Search Result
    │
    ▼
SonicIntegrityService
    │ (Channel<T> Queue)
    ▼
[Worker 1] [Worker 2] ...
    │
    ▼
FFmpeg Process (Spectral Analysis)
    │
    ▼
Trust Score Update (DB)
```
*   **Non-Blocking**: UI remains responsive while background workers analyze files.
*   **Graceful Degradation**: If FFmpeg is missing, analysis is skipped without crashing.

---

## 🧪 Audio Analysis & Cue Generation (Essentia Sidecar)

### Pipeline
```
Download Completed
    │
    ▼
AnalysisQueueService (Channel<T>)
    │ (SemaphoreSlim(2) workers)
    ▼
EssentiaAnalyzerService (FFmpeg + Essentia sidecar)
    │
    ├─ AudioFeaturesEntity (BPM, Key, Energy, Danceability)
    ├─ DropDetectionEngine (loudness + spectral + onset)
    └─ CueGenerationEngine (32-bar phrase, beat-aligned)
    ▼
Library Updates + TrackInspector auto-refresh
```

### Resilience
- 45s watchdog kills hung external processes
- Atomic DB writes (features + waveform in a single transaction)
- Correlated forensic logging for every track (TrackForensicLogger)

### User Experience
- Glass Box status bar: "🧠 Analyzing: N remaining ~ ETA"
- Pause/Resume support on the queue
- Track Inspector live-refresh when analysis completes

---

## 🔄 Import Pipeline

```
User Action
    │
    ▼
ImportOrchestrator
    │
    ├─ SpotifyImportProvider (PKCE Auth)
    ├─ CsvImportProvider
    └─ ManualImportProvider
    │
    ▼
DownloadManager.QueueProject
```

---

## 🔊 Audio Playback System

*   **NAudio**: Low-latency playback (MP3, FLAC, WAV) with hardware-style pitch.
*   **PlayerViewModel**: Manages transport controls (Play/Pause/Seek).
*   **State Machine**: Handles transition between `Stopped` → `Buffering` → `Playing`.
*   **WaveformControl**: Renders Rekordbox PWAV data or locally generated peaks.

---

## 📱 Navigation & ViewModels

*   **Coordinator Pattern**: `MainViewModel` coordinates global state.
*   **Composition**: Feature-specific logic lives in child ViewModels (`LibraryViewModel`, `DownloadViewModel`).
*   **EventBus**: Decoupled communication using typed events (`TrackUpdatedEvent`, `RecoveryCompletedEvent`).

---

## 🔍 Diagnostics

*   **Dead-Letter Queue**:
    *   Failed recovery operations are retried 3 times.
    *   After 3 strikes, they are moved to `%AppData%/SLSKDONET/dead_letters.log`.
*   **Logging**: Serilog writes to both Console (Debug) and Rolling File (Release).

---

## 📚 Documentation Map

The following technical guides provide deep-dives into specific subsystems:

| Subsystem | Documentation | Description |
| :--- | :--- | :--- |
| **DJ Companion** | [DJ_COMPANION_ARCHITECTURE.md](DOCS/DJ_COMPANION_ARCHITECTURE.md) | Professional mixing workspace with 4 parallel AI recommendation engines. |
| **Resilience** | [RESILIENCE.md](DOCS/RESILIENCE.md) | Overview of the crash recovery system and standard patterns. |
| **Download Health** | [DOWNLOAD_RESILIENCE.md](DOCS/DOWNLOAD_RESILIENCE.md) | Adaptive timeout logic and stall detection (Phase 3B). |
| **Atomic Downloads** | [ATOMIC_DOWNLOADS.md](DOCS/ATOMIC_DOWNLOADS.md) | Details the "Trust Journal, Truncate Disk" resume pattern. |
| **SafeWrite** | [SAFE_WRITE.md](DOCS/SAFE_WRITE.md) | Explains the ACID transaction wrapper for file operations. |
| **Database Schema** | [DATABASE_SCHEMA.md](DOCS/DATABASE_SCHEMA.md) | Dual-Truth model with IntegrityLevel enum (Phase 3B). |
| **The Brain** | [THE_BRAIN_SCORING.md](DOCS/THE_BRAIN_SCORING.md) | Breakdown of the scoring algorithms, tiers, and point values. |
| **Rekordbox Export** | [PRO_DJ_TOOLS.md](DOCS/PRO_DJ_TOOLS.md) | XML generation, key conversion, and URI normalization (Phase 4). |
| **Spotify Auth** | [SPOTIFY_AUTH.md](DOCS/SPOTIFY_AUTH.md) | PKCE authentication flow and token security. |
