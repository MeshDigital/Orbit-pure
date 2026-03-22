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
