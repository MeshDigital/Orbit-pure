# 🛰️ ORBIT-Pure Architecture

## System Overview

ORBIT-Pure is a high-fidelity P2P music workstation that combines Soulseek network integration with professional audio analysis tools. The system prioritizes audio integrity, metadata accuracy, and performance optimization for music professionals.

```
┌─────────────────────────────────────────────────────────────┐
│                    🎵 ORBIT-Pure UI Layer                    │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              MainWindow (Avalonia)                  │    │
│  │  ┌─────────────────────────────────────────────────┐ │   │
│  │  │         Navigation & Layout Shell              │ │   │
│  │  │  • Sidebar Navigation                          │ │   │
│  │  │  • Player Controls                             │ │   │
│  │  │  • Status Indicators                           │ │   │
│  │  └─────────────────────────────────────────────────┘ │   │
│  └─────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              Page Views (MVVM)                     │    │
│  │  • Library Browser                                 │    │
│  │  • Search Interface                                │    │
│  │  • Download Manager                                │    │
│  │  │  • Forensic Analysis                            │    │
│  │  └─────────────────────────────────────────────────┘    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────┬───────────────────────────────────────┘
                      │
        ┌─────────────▼─────────────────────────────────────┐
        │         🎛️ Application Services Layer              │
        │  ┌─────────────────────────────────────────────┐  │
        │  │         Core Business Logic                 │  │
        │  │  • LibraryService (Data Management)        │  │
        │  │  • DownloadManager (P2P Operations)        │  │
        │  │  • AudioIntegrityService (Forensic Analysis)│  │
        │  │  • SearchOrchestrator (Query Processing)   │  │
        │  │  • PlaylistExportService (Data Export)     │  │
        │  └─────────────────────────────────────────────┘  │
        │  ┌─────────────────────────────────────────────┐  │
        │  │         Integration Services                │  │
        │  │  • SoulseekAdapter (Network Protocol)      │  │
        │  │  • SpotifyMetadataService (API Integration)│  │
        │  │  • MusicBrainzService (Metadata Enrichment)│  │
        │  │  • NotificationService (User Feedback)     │  │
        │  └─────────────────────────────────────────────┘  │
        └─────────────────┬───────────────────────────────────┘
                          │
        ┌─────────────────▼───────────────────────────────────┐
        │         🗄️ Infrastructure & Data Layer               │
        │  ┌─────────────────────────────────────────────┐   │
        │  │         Data Persistence                     │   │
        │  │  • AppDbContext (EF Core)                   │   │
        │  │  • LibraryEntryEntity (Core Data Model)    │   │
        │  │  • TrackEntity (Download Queue)            │   │
        │  │  • Forensic Log Entities                   │   │
        │  └─────────────────────────────────────────────┘   │
        │  ┌─────────────────────────────────────────────┐   │
        │  │         External Dependencies               │   │
        │  │  • SQLite Database (WAL Mode)              │   │
        │  │  • Essentia (Audio Analysis)               │   │
        │  │  • NWaves (Spectral Processing)            │   │
        │  │  • FFmpeg (Media Processing)               │   │
        │  └─────────────────────────────────────────────┘   │
        └─────────────────────────────────────────────────────┘
```

---

## 🏗️ Core Architecture Principles

### 1. **Audio Integrity First**
- Every audio file undergoes spectral analysis to detect transcoding artifacts
- Forensic logging captures detailed integrity metrics
- User-friendly error reporting for corrupted or suspicious files

### 2. **Metadata Dual-Truth System**
- Preserves original source metadata alongside user corrections
- Multiple enrichment sources (Spotify, MusicBrainz, local analysis)
- Intelligent conflict resolution and verification

### 3. **Performance Optimization**
- Delta scanning for efficient library synchronization
- Background processing for CPU-intensive analysis
- Memory-efficient data structures for large collections

### 4. **Professional Reliability**
- Global exception handling with user-friendly error dialogs
- Crash recovery with journal-first logging
- Comprehensive error reporting for beta testing

---

## 🔧 Key Components

### UI Layer (Avalonia)
- **Cross-platform XAML interface** supporting Windows, macOS, Linux
- **MVVM architecture** with reactive ViewModels
- **Custom controls** for audio visualization and forensic displays
- **Responsive design** adapting to different screen sizes

### Application Services
- **LibraryService**: Manages the core music collection database
- **DownloadManager**: Handles P2P file transfers with resilience
- **AudioIntegrityService**: Performs spectral analysis and forensic checks
- **SearchOrchestrationService**: Executes planned search lanes, bounded accumulation, and final winner ranking
- **PlaylistExportService**: Generates professional CSV exports with forensic data
- **SimilarityIndex** (`Services/Similarity/`): In-memory cosine-similarity index over `AudioAnalysisEntity.VectorEmbeddingJson` (128-dim float[]). Lazy-load, 1h TTL, `SemaphoreSlim` thread safety. `GetSimilarTracksAsync(hash, topN)` returns ranked matches.
- **PlaylistOptimizer** (`Services/Playlist/`): Greedy nearest-neighbour graph ordering. Edge cost = `camelotDist × wH + bpmDiff/10 × wT + energyDiff × wE + jumpPenalty`. Post-pass applies `EnergyCurvePattern` (Rising/Wave/Peak). Config via `PlaylistOptimizerOptions`.
- **BackgroundJobQueue / BackgroundJobWorker** (`Services/Jobs/`): `Channel<BackgroundJob>` queue with `IBackgroundJobQueue` interface. `BackgroundJobWorker` is an `IHostedService` with `SemaphoreSlim(MaxConcurrency)` gate, `IProgress<JobProgress>` reporting per job.

### Infrastructure
- **SQLite Database**: WAL-mode optimized for concurrent operations
- **Entity Framework Core**: Type-safe data access with migrations
- **Dependency Injection**: Clean service composition and testing
- **Configuration Management**: Environment-specific settings

---

## 🎯 Data Flow Architecture

### Library Ingestion Pipeline
```
Audio File → Spectral Analysis → Metadata Extraction → Integrity Verification → Database Storage
     ↓             ↓              ↓                    ↓              ↓
  File System   NWaves/Essentia   Multiple APIs      Forensic Rules   SQLite WAL
```

### Search & Discovery Flow
```
User Query → Query Parsing → Multi-Source Search → Ranking Algorithm → UI Display
     ↓            ↓                ↓                  ↓              ↓
  Natural Language  Tokenization   Soulseek/Local     ML Scoring     Virtualized Grid
```

### Reactive Search Runtime Flow
```
User Query
   │
   ▼
SearchViewModel
  • session ownership
  • stop-listening control
  • telemetry
   │
   ▼
SearchNormalizationService
  • TargetMetadata
  • SearchPlan
  • Strict / Standard / Desperate lanes
   │
   ▼
SearchOrchestrationService
  • lane execution
  • bounded accumulation
  • blend ranking
   │
   ▼
SoulseekAdapter
  • callback → async stream bridge
  • filtering + dedup
  • callback drain sync
  • hard-cap circuit breaker
   │
   ▼
Rx / DynamicData Pipeline
  • background projection
  • 250ms or 50-item batching
  • incremental bound updates
   │
   ▼
Avalonia Search Grid
  • stable rows
  • hidden reasons
  • stream/idle status
```

### Export Pipeline
```
Playlist Selection → Forensic Data Retrieval → CSV Generation → Integrity Validation → File Output
        ↓                    ↓                      ↓                  ↓              ↓
   Database Query      Spectral Metrics        Field Escaping     Format Check    User Download
```

---

## 🔎 Reactive Search Runtime Architecture

The March 2026 search upgrade introduced a layered runtime built specifically for Soulseek’s firehose-style search behavior.

### Design goals

- keep the UI responsive during bursty searches,
- make cancellation final for the active stream,
- bound pathological search growth,
- preserve explainability from search to discovery to download provenance,
- avoid full-grid rebuilds while results continue to arrive.

### Runtime layers

#### 1. `SearchViewModel`: session owner

The view-model now owns explicit session state:

- active cancellation token,
- current search session identifier,
- `SerialDisposable` ownership for stream and idle-monitor subscriptions,
- operator-visible telemetry via `ResultsPerSecond`, `TotalResultsReceived`, and `LastResultAtUtc`.

This is what makes `STOP LISTENING` a real runtime cancellation boundary.

#### 2. `SearchNormalizationService`: query planner

Searches are now represented with structured planning models:

- `TargetMetadata`
- `SearchPlan`
- `SearchQueryLane`
- `PlannedSearchLane`

The planner emits deterministic lane order:

- `Strict`
- `Standard`
- `Desperate`

This gives the rest of the stack a stable search intent model instead of a loose list of variations.

#### 3. `SearchOrchestrationService`: lane coordinator

The orchestrator now:

- executes lane plans in order,
- delays or skips desperate fallback when appropriate,
- applies bounded accumulation windows,
- short-circuits on near-ideal winners,
- ranks emitted winners through the shared blend model.

#### 4. `SoulseekAdapter`: protocol boundary

The adapter remains the bridge between callback-driven Soulseek responses and ORBIT async streams.

Key protections now include:

- serialized outbound search dispatch for correctness,
- callback-drain synchronization before final completion,
- explicit hard file/result caps,
- surfaced `SearchLimitExceededException` and `SearchHardCapTriggeredEvent` for transparency.

#### 5. Shared ranking and explainability

The stack now shares one ranking vocabulary through:

- `SearchCandidateFitScorer`
- `SearchCandidateRankingPolicy`
- `SearchBlendReasonFormatter`

This lets search, discovery, audits, and persistence all agree on:

- fit score,
- reliability weighting,
- final blended score,
- compact preferred reason text.

#### 6. Buffered UI ingestion

The UI no longer relies on manual clear-and-rebuild behavior for live search results.

Instead, incoming results are:

- projected on a background scheduler,
- buffered by time or count,
- appended in batches,
- exposed through DynamicData-bound filtered/sorted collections.

This greatly reduces dispatcher churn under sustained result inflow.

### Search runtime sequence

```
┌──────────────┐
│ Operator UI  │
└──────┬───────┘
       │ search query
       ▼
┌────────────────────────┐
│ SearchViewModel        │
│ • begin session        │
│ • reset telemetry      │
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│ SearchNormalizationSvc │
│ • build SearchPlan     │
└──────────┬─────────────┘
           │ lanes
           ▼
┌─────────────────────────────┐
│ SearchOrchestrationService  │
│ • execute lanes             │
│ • accumulate/rank winners   │
└──────────┬──────────────────┘
           │ network stream
           ▼
┌────────────────────────┐
│ SoulseekAdapter        │
│ • callback bridge      │
│ • hard caps            │
└──────────┬─────────────┘
           │ tracks
           ▼
┌────────────────────────┐
│ Rx Buffer Pipeline     │
│ • background mapping   │
│ • 250ms / 50 batching  │
└──────────┬─────────────┘
           │ batch add
           ▼
┌────────────────────────┐
│ DynamicData Bound View │
│ • filter/sort/bind     │
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│ Avalonia Search Grid   │
│ • stable rows          │
│ • hidden reasons       │
│ • stream idle state    │
└────────────────────────┘
```

### Key properties after the upgrade

- cancellation stops future arrivals for the active session,
- broad searches are bounded by circuit-breaker caps,
- visible rows are updated in chunks rather than per item,
- the final buffered batch drains before normal completion cleanup,
- search and discovery use the same explainability model.

### Related files

- `ViewModels/SearchViewModel.cs`
- `Views/Avalonia/SearchPage.axaml`
- `Services/SearchNormalizationService.cs`
- `Services/InputParsers/SearchPlanningModels.cs`
- `Services/SearchOrchestrationService.cs`
- `Services/SoulseekAdapter.cs`
- `Services/SearchCandidateFitScorer.cs`
- `Services/SearchCandidateRankingPolicy.cs`
- `Services/SearchBlendReasonFormatter.cs`
- `Tests/SLSKDONET.Tests/ViewModels/SearchViewModelTests.cs`
- `Tests/SLSKDONET.Tests/Services/SearchOrchestrationServiceTests.cs`

---

## 🔒 Security & Privacy

### Network Security
- **IP Visibility**: Soulseek P2P network exposes user IP addresses
- **VPN Recommendation**: Strong recommendation for privacy protection
- **No Data Collection**: Local-only operation, no telemetry or tracking

### Data Integrity
- **Cryptographic Verification**: File integrity checking during downloads
- **Forensic Analysis**: Detection of audio manipulation and transcoding
- **Secure Storage**: SQLite encryption options available for sensitive data

---

## 🚀 Performance Characteristics

### Library Operations
- **Initial Scan**: Comprehensive analysis with progress reporting
- **Delta Sync**: Sub-second updates for incremental changes
- **Search Performance**: Sub-100ms response for large collections
- **Export Speed**: Efficient CSV generation with forensic data

### Memory Management
- **Virtualization**: UI virtualization for 10,000+ track displays
- **Background Processing**: Non-blocking analysis operations
- **Resource Cleanup**: Proper disposal of audio processing resources

### Network Resilience
- **Connection Recovery**: Automatic reconnection with exponential backoff
- **Download Integrity**: Resume capability for interrupted transfers
- **Rate Limiting**: Respectful API usage with circuit breaker patterns

---

## 🧪 Quality Assurance

### Automated Testing
- **Unit Tests**: Core business logic validation
- **Integration Tests**: Service interaction verification
- **UI Tests**: User interface functionality checks

### Beta Testing Program
- **Error Reporting**: Structured crash reporting system
- **Performance Metrics**: User-reported timing and resource usage
- **Feature Validation**: Real-world usage pattern analysis

---

## 📈 Scaling Considerations

### Large Library Support
- **Database Optimization**: Indexed queries for fast searches
- **UI Virtualization**: Smooth scrolling through massive collections
- **Background Processing**: Non-blocking analysis for large imports

### Network Performance
- **Multi-lane Downloads**: Parallel transfer optimization
- **Bandwidth Management**: Adaptive download scheduling
- **Connection Pooling**: Efficient network resource usage

### Audio Processing
- **Batch Analysis**: Efficient processing of multiple files
- **Resource Limiting**: CPU and memory usage controls
- **Progress Tracking**: Real-time analysis status reporting

---

## 🤖 Phase 13C: AI Layer & Stem Separation

### Overview

Phase 13C introduces machine-learning inference directly into the ORBIT-Pure analysis
pipeline.  All AI work is performed on background threads and is transparent to the user
through the **Glass Box** status stream powered by `AnalysisQueueService`.

### Design Principles

- **Glass Box Transparency** – every queued, running, and completed analysis job is
  published as an `AnalysisQueueStatusChangedEvent` so the UI can reflect the queue
  state in real time.
- **Stealth Mode** – when enabled, a 250 ms yield is inserted between jobs so the UI
  scheduler always gets CPU time before the next analysis begins.
- **Predictive Metadata** – AI models output *probability distributions* (not just
  labels), stored as floating-point values (e.g. `MoodHappy.Probability = 0.83`).
  This enables nuanced "Vibe" searches and richer Smart Crate criteria.
- **Hardware Acceleration First** – inference prefers the DirectML GPU execution
  provider and falls back to the ONNX CPU provider transparently.

### Data Models (`Data/Essentia/EssentiaModels.cs`)

Essentia's JSON output is deserialized into a typed hierarchy:

```
EssentiaOutput
├── RhythmData   – BPM, danceability, onset rate, BPM histogram (Phase 13A)
├── TonalData    – key/scale (EDMA + Krumhansl), chord extraction
├── LowLevelData – loudness, spectral centroid/complexity, RMS
└── HighLevelData (Phase 13C)
    ├── VoiceInstrumental  → ModelPrediction { Value, Probability, All.Voice/Instrumental }
    ├── Danceability       → ModelPrediction { Value, Probability, All.Danceable/NotDanceable }
    ├── MoodHappy          → ModelPrediction { Value, Probability }
    ├── MoodAggressive     → ModelPrediction
    ├── MoodSad            → ModelPrediction
    ├── MoodRelaxed        → ModelPrediction
    ├── MoodParty          → ModelPrediction
    └── MoodElectronic     → ModelPrediction
```

`ModelPrediction.Probability` is the raw confidence score from the TensorFlow model,
stored verbatim so downstream features can apply their own thresholds.

### Stem Separation (`Services/StemSeparationService.cs`)

`StemSeparationService` coordinates a three-tier provider chain:

```
Track File
    │
    ▼
1. SpleeterCliSeparator   (highest accuracy – requires spleeter Python CLI)
    │  on failure ↓
2. OnnxStemSeparator      (DirectML GPU – requires spleeter-5stems.onnx model)
    │  on failure ↓
3. Mock Fallback           (zero external dependencies – silent WAV stubs)
```

Separated stem WAV files are cached under `%APPDATA%/Antigravity/Stems/<trackId>/`
and reused on subsequent requests (`HasStems` check).

#### Spleeter CLI Provider (`Services/Audio/Separation/SpleeterCliSeparator.cs`)

- Invokes the `spleeter separate -p spleeter:4stems` command.
- Checks availability with `spleeter --version` before dispatch.
- Streams stdout/stderr to the console for real-time diagnostics.
- Cancellation is forwarded via `CancellationToken` → `Process.Kill()`.

#### ONNX DirectML Provider (`Services/Audio/Separation/OnnxStemSeparator.cs`)

- Loads `Tools/Essentia/models/spleeter-5stems.onnx`.
- Appends the **DirectML** execution provider (`AppendExecutionProvider_DML(deviceId: 0)`)
  for GPU inference; silently falls back to CPU if DirectML is unavailable.
- De-interleaves stereo audio into a `[samples, 2]` tensor, runs inference, and
  re-interleaves each stem output back to WAV.

#### DSP Support (`Services/Audio/Separation/DSP/`)

- **`ExactSTFT`** – Short-Time Fourier Transform matching librosa's default parameters
  (n_fft=4096, hop_length=1024, symmetric Hann window, reflect padding).  Used by
  STFT-based ONNX model variants.
- **`TensorUtils`** – helpers for building ONNX input tensors from complex spectrograms.

### Background Job Orchestration (`Services/AnalysisQueueService.cs`)

```
TrackAnalysisRequestedEvent (IEventBus)
    │
    ▼
AnalysisQueueService
    ├── _queuedCount++
    ├── PublishStatus()           → AnalysisQueueStatusChangedEvent (Glass Box)
    └── DispatchAnalysisJobAsync()
            │
            ├── [Stealth Mode ON]  → Task.Delay(250 ms)   // yield to UI
            ├── _processedCount++
            └── PublishStatus()   → AnalysisQueueStatusChangedEvent (Glass Box)
```

`SetStealthMode(true)` can be toggled at runtime; the change is reflected in the
next published status event without restarting any in-flight jobs.

### Structural Analysis (`Services/StructuralAnalysisEngine.cs`)

The engine uses the **energy curve** stored in the database (computed from raw PCM
during library ingestion) to detect drops without re-reading audio files.

Algorithm:
1. `ComputePhraseBoundaries(bpm, duration)` – generates 16-bar grid timestamps.
2. `ComputeNovelty(energyCurve)` – first-order derivative; negative deltas clamped to 0.
3. `FindDrops(energy, phrases, bpm)` – matches novelty peaks to phrase boundaries and
   validates that energy remains above 60 % of peak for ≥ 8 bars (anti-fake-drop guard).
4. Returns up to `MaxDrops = 3` candidates ordered by confidence.

This is fully deterministic and I/O-free, making it straightforward to unit-test.

### Hardware Acceleration

| Provider | Requirement | Activation |
|---|---|---|
| DirectML (GPU) | Windows + DirectX 12 GPU | `AppendExecutionProvider_DML(0)` |
| CPU (fallback) | None | automatic |

The NuGet package `Microsoft.ML.OnnxRuntime.DirectML 1.23.0` is already a project
dependency and handles the GPU ↔ CPU fallback at the ONNX Runtime level.

### Related Files

- `Data/Essentia/EssentiaModels.cs`
- `Services/StemSeparationService.cs`
- `Services/Audio/Separation/IStemSeparator.cs`
- `Services/Audio/Separation/SpleeterCliSeparator.cs`
- `Services/Audio/Separation/OnnxStemSeparator.cs`
- `Services/Audio/Separation/DSP/ExactSTFT.cs`
- `Services/Audio/Separation/DSP/TensorUtils.cs`
- `Services/AnalysisQueueService.cs`
- `Services/StructuralAnalysisEngine.cs`
- `Tests/SLSKDONET.Tests/Services/AnalysisQueueTests.cs`
- `Tests/SLSKDONET.Tests/Services/StructuralAnalysisEngineTests.cs`

---

## 🔄 Development Workflow

### Version Control
- **Git Flow**: Feature branches with pull request reviews
- **Semantic Versioning**: Major.minor.patch with prerelease tags
- **Changelog**: Comprehensive change documentation

### Continuous Integration
- **Automated Builds**: Multi-platform compilation verification
- **Test Execution**: Automated test suite running
- **Code Quality**: Static analysis and style checking

### Release Process
- **Beta Releases**: Regular beta builds for testing
- **Stable Releases**: Thoroughly tested production versions
- **Documentation**: Updated guides for each release

---

## 📚 Related Documentation

- **[README.md](README.md)**: Project overview and quick start
- **[DOCS/REACTIVE_SEARCH_RUNTIME_TECHNICAL_2026-03-22.md](DOCS/REACTIVE_SEARCH_RUNTIME_TECHNICAL_2026-03-22.md)**: Deep technical guide for the new reactive search runtime
- **[DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md](DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md)**: Search firehose hardening plan and acceptance criteria
- **[BETA_TESTER_GUIDE.md](BETA_TESTER_GUIDE.md)**: Comprehensive testing guide
- **[RECENT_CHANGES.md](RECENT_CHANGES.md)**: Development changelog
- **[TODO.md](TODO.md)**: Development roadmap and backlog

---

*This architecture document reflects the current state of ORBIT-Pure as of March 2026. The system is designed for reliability, performance, and professional audio integrity verification.*
