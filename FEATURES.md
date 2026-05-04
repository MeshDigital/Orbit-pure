# 🎵 ORBIT-Pure Features

## High-Fidelity P2P Music Workstation

ORBIT-Pure combines Soulseek network integration with professional audio analysis tools, prioritizing **audio integrity**, **metadata accuracy**, and **performance optimization** for music professionals, DJs, and audio engineers.

---

## 🎛️ Core Features

### Audio Integrity & Forensic Analysis
- **Spectral Analysis**: NWaves-powered frequency analysis detecting transcoding artifacts
- **Forensic Logging**: Detailed integrity metrics with dB measurements and energy ratios
- **Quality Verification**: Automatic detection of fake lossless files and audio manipulation
- **Health Reports**: Comprehensive library integrity assessments with actionable insights

### Professional Library Management
- **Delta Scanning**: Lightning-fast incremental library synchronization
- **Dual-Truth Metadata**: Preserves original data alongside user corrections
- **Smart Organization**: Intelligent file detection and metadata enrichment
- **Large Collection Support**: Optimized for libraries with 10,000+ tracks
- **Context Menu Actions**: Right-click any track for Play Track, Queue Track, Queue Selected, 🔬 Analyse, Hard Retry, Open Folder, Export CSV, Remove — all actions fall back to the current selection when invoked from the Avalonia popup visual tree (where element-name bindings are unavailable)
- **Selection FAB**: Floating bottom bar appears when ≥1 track is selected — one-click ▶️ Play, ⊕ Add to Queue, 🔬 Analyse, ✏️ Tag Edit, 📤 Rekordbox export, and ✕ Clear selection without opening context menus

### Enhanced Export Capabilities
- **Forensic CSV Export**: Professional-grade playlist exports with integrity metrics
- **Comprehensive Metadata**: Includes BPM, key, energy, and spectral data
- **Rekordbox Compatibility**: Native XML export for professional DJ software
- **Batch Processing**: Efficient export of large playlists and collections

### Intelligent Search & Discovery
- **Multi-Source Search**: Simultaneous querying of local library and Soulseek network
- **Metadata Enrichment**: Spotify and MusicBrainz integration for accurate tagging
- **Harmonic Matching**: Key-based track recommendations for DJ workflows
- **Quality Filtering**: Pre-download verification of file authenticity

---

## 🎧 Audio Playback & Analysis

### Professional Player Interface
- **High-Fidelity Engine**: NAudio-powered playback with low-latency monitoring
- **Format Support**: MP3, FLAC, WAV, OGG, M4A, and more
- **Real-Time VU Meters**: Professional dual-channel peak monitoring
- **Waveform Visualization**: Interactive seekbar with detailed audio representation

### Advanced Audio Analysis
- **Stem Separation**: Real-time vocal/accompaniment isolation (optional ONNX/Spleeter) with model-version-aware caching
- **Stem Cache Versioning**: Cache keys include model tag (e.g. `spleeter-5stems!{hash}_{start}_{dur}_{stem}.wav`); `PurgeStaleEntriesAsync` auto-evicts stems from superseded models on upgrade
- **Spectral Forensics**: Frequency cutoff detection and energy distribution analysis
- **Quality Metrics**: Dynamic range, loudness, and true peak measurements
- **Integrity Verification**: Automatic detection of audio file manipulation

---

## 🔄 Download Management

### Resilient P2P Operations
- **Multi-Lane Downloads**: Parallel transfer optimization for maximum speed
- **Crash Recovery**: Journal-first logging with 15-second heartbeat checkpoints
- **Connection Resilience**: Exponential backoff reconnection for network stability
- **Integrity Verification**: Post-download verification and automatic retry

### Smart Download Intelligence
- **Pre-Download Analysis**: Mathematical verification of file size vs. bitrate/duration
- **Quality Filtering**: Automatic rejection of impossible or suspicious files
- **Duplicate Prevention**: Intelligent detection of existing tracks
- **Bandwidth Optimization**: Adaptive scheduling based on network conditions

---

## 🎚️ DJ & Production Tools

### Creative Workstation Features
- **Harmonic Mixing**: Camelot wheel-based key compatibility recommendations
- **Tempo Matching**: BPM synchronization with ±6% tolerance ranges
- **Energy Flow**: Directional mixing guidance (build → peak → cooldown)
- **Style Recommendations**: Genre-based track suggestions
- **Direct Player Handoff**: The current track can jump straight from the player into Workstation, Flow, Stems, or a target deck for faster prep
- **Search-to-Mix Staging**: Multi-selected Soulseek search results can be sent directly into the mix-building workflow with a batch Add to Mix action
- **Live Prep Visibility**: Player and Workstation headers surface cue, stem, routing, transition, and analysis-lane readiness summaries
- **Session Persistence**: Workstation state (loaded tracks, deck positions, active mode, timeline zoom/offset) is autosaved to `%APPDATA%\Antigravity\workstation-session.json` using atomic temp-file swap writes — survives crashes, power loss, and normal app close; fully restored on next launch including cue points and stem preferences
- **Analyse Track**: Single-track audio analysis trigger from the library right-click context menu (`🔬 Analyse Track`)

### AI Automix Engine
- **Similarity Search**: Cosine-distance matching over 128-dim audio embeddings stored per-track — `SimilarityIndex` with 1-hour TTL cache and thread-safe lazy-load
- **Playlist Optimization**: Greedy nearest-neighbour graph over Camelot distance, BPM delta, and EnergyScore with configurable per-factor weights (`PlaylistOptimizer`)
- **Energy Curve Sequencing**: Post-ordering pass reshapes any playlist into `Rising`, `Wave` (arch), or `Peak` (low-body + high-energy spike) energy profiles
- **Max-BPM-Jump Guard**: Configurable penalty rejects transitions wider than a set BPM range, preventing jarring key-tempo collisions
- **Seeded Ordering**: Optional fixed start/end track constraints for opening and closing track pinning

### Background Processing
- **Job Queue**: `Channel<T>`-backed unbounded job queue (`BackgroundJobQueue`) with configurable concurrency
- **Progress Reporting**: Per-job `IProgress<JobProgress>` with fraction, description, and error capture — UI can subscribe to live progress events
- **Graceful Cancellation**: All analysis and stem jobs respect `CancellationToken` top-to-bottom; worker shuts down cleanly on app exit

### Professional Export Suite
- **Rekordbox XML**: Full Pioneer DJ export — `POSITION_MARK` hot-cue/memory-cue nodes (R/G/B color, pad slots 0-7), `TEMPO` beat-grid nodes (`Inizio`, `Bpm`, `Metro`, `Battito`); cues sourced from `CuePointEntity` DB rows and per-track `CuePointsJson` (50 ms dedup window)
- **Forensic CSV**: Professional analysis data for music librarians
- **Batch Operations**: Efficient processing of large track collections
- **Metadata Preservation**: Complete fidelity in export operations

---

## 🔧 System Architecture

### Cross-Platform Compatibility
- **Avalonia UI**: Native performance on Windows, macOS, and Linux
- **.NET 9.0 Runtime**: Modern JIT optimization and async performance
- **SQLite Database**: WAL-mode optimized for concurrent operations
- **Dependency Injection**: Clean service architecture and testability

### Performance Optimization
- **UI Virtualization**: Smooth scrolling through massive collections
- **Background Processing**: Non-blocking analysis and downloads
- **Memory Management**: Efficient resource usage for large libraries
- **Delta Synchronization**: Sub-second updates for incremental changes

---

## 🛡️ Reliability & Security

### Error Handling & Recovery
- **Global Exception Handling**: User-friendly crash reporting system
- **Automatic Recovery**: Seamless continuation after interruptions
- **Comprehensive Logging**: Detailed diagnostics for troubleshooting
- **Beta Testing Tools**: Structured feedback collection and analysis

### Privacy & Security
- **Local Operation**: No telemetry or external data collection
- **VPN Recommended**: Network privacy protection for P2P operations
- **Secure Storage**: Optional encryption for sensitive configuration
- **Integrity Verification**: Cryptographic checking of downloaded files

---

## 📊 Data Management

### Intelligent Metadata
- **Multi-Source Enrichment**: Spotify, MusicBrainz, and local analysis integration
- **Conflict Resolution**: Smart merging of conflicting metadata sources
- **User Corrections**: Preservation of manual metadata overrides
- **Batch Processing**: Efficient metadata operations for large collections

### Database Optimization
- **Indexed Queries**: Fast searches across large music libraries
- **Concurrent Access**: WAL-mode SQLite for multi-threaded operations
- **Migration Support**: Seamless schema updates and data preservation
- **Backup Integration**: Automatic database integrity checking

---

## 🔌 Integration Ecosystem

### API Integrations
- **Spotify Web API**: PKCE OAuth authentication with metadata enrichment
- **MusicBrainz**: Comprehensive music metadata and relationship data
- **Soulseek Network**: P2P file sharing with integrity verification
- **FFmpeg**: Professional media processing and format conversion

### External Tools
- **Rekordbox**: Native XML export for professional DJ workflows
- **Audio Analysis**: Essentia framework for ML-powered music analysis
- **Stem Separation**: Optional AI-powered vocal isolation
- **Spectral Analysis**: NWaves library for detailed frequency analysis

---

## 🎯 Use Cases

### For DJs & Producers
- **Harmonic Mixing**: Key-based track recommendations and compatibility
- **Quality Assurance**: Forensic verification of audio file integrity
- **Professional Exports**: Rekordbox XML and forensic CSV generation
- **Large Collection Management**: Efficient handling of extensive music libraries

### For Music Librarians
- **Integrity Verification**: Comprehensive audio quality assessment
- **Metadata Enrichment**: Automated tagging and organization
- **Forensic Reporting**: Detailed analysis reports for collection management
- **Batch Operations**: Efficient processing of large music archives

### For Audio Engineers
- **Spectral Analysis**: Detailed frequency domain inspection
- **Quality Metrics**: Technical measurements of audio characteristics
- **Format Verification**: Detection of transcoding and manipulation
- **Professional Tools**: Industry-standard export and analysis capabilities

---

## 🚀 Performance Benchmarks

### Library Operations
- **Initial Scan**: Comprehensive analysis with progress feedback
- **Delta Sync**: < 30 seconds for incremental changes
- **Search Response**: < 100ms for queries in 10,000+ track libraries
- **Export Speed**: Efficient generation of forensic CSV reports

### Network Performance
- **Download Resilience**: Automatic recovery from connection interruptions
- **Multi-Lane Transfer**: Parallel optimization for maximum throughput
- **Quality Filtering**: Pre-download verification prevents wasted bandwidth
- **Connection Management**: Intelligent handling of network conditions

### System Resources
- **Memory Efficient**: Optimized for large collections without excessive RAM usage
- **CPU Management**: Background processing prevents UI freezing
- **Storage Optimized**: Efficient database design and indexing
- **Cross-Platform**: Native performance on all supported operating systems

---

*ORBIT-Pure represents the evolution from basic file sharing to professional music workstation, combining network efficiency with audio integrity verification and comprehensive analysis tools.*</content>
<parameter name="filePath">c:\Users\quint\OneDrive\Documenten\GitHub\ORBIT-Pure\FEATURES.md
