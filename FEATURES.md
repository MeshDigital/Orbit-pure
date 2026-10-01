# 🎵 ORBIT-Pure Features

## High-Fidelity P2P Music Workstation

ORBIT-Pure combines Soulseek network integration with professional audio analysis tools, prioritizing **audio integrity**, **metadata accuracy**, and **performance optimization** for music professionals, DJs, and audio engineers.

---

## 🎛️ Core Features

### Audio Integrity & Forensic Analysis
- **Spectral Analysis**: NWaves-powered frequency analysis detecting transcoding artifacts
- **Fake-Lossless / VBR Fraud Detection**: every completed lossless download is run through a
  real FFT pipeline (Hann-windowed spectrum over 4096-sample frames) that measures spectral
  cutoff and rolloff steepness to catch transcoded "fake FLAC" files — not a heuristic, a
  genuine authenticity verdict shown as a badge on the track row, toggleable in Settings
- **Forensic Logging**: Detailed integrity metrics with dB measurements and energy ratios
- **Health Reports**: Comprehensive library integrity assessments with actionable insights

### Professional Library Management
- **Delta Scanning**: Lightning-fast incremental library synchronization
- **Dual-Truth Metadata**: Preserves original data alongside user corrections
- **Smart Organization**: Intelligent file detection and metadata enrichment
- **Large Collection Support**: Optimized for libraries with 10,000+ tracks
- **Context Menu Actions**: Right-click any track for Play Track, Queue Track, Queue Selected, 🔬 Analyse, Hard Retry, Open Folder, Export CSV, Remove — all actions fall back to the current selection when invoked from the Avalonia popup visual tree (where element-name bindings are unavailable)
- **Selection FAB**: Floating bottom bar appears when ≥1 track is selected — one-click ▶️ Play, ⊕ Add to Queue, 🔬 Analyse, ✏️ Tag Edit, 📤 Rekordbox export, and ✕ Clear selection without opening context menus
- **Playlist view that makes room for tracks**: the header collapses to a slim bar (▶, shuffle, name, Discover) while scrolling; search, status chips, filters, Mix and Columns share a single toolbar row; columns you hide take no space, and when the list is narrow (e.g. context panel open) the least important columns step aside (Forensics → Rating → Duration → Format → Energy)
- **Resizable context panel**: drag the right-hand panel's left edge; the width is remembered (`[Layout] ContextPanelWidth`)

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
- **Playlist Discover**: A ✨ Discover button on a playlist opens a sidepanel tab with tracks to add. It draws on new releases by the playlist's artists and the genre top 100 from Beatport, plus Deezer's related artists. It hides what you already own and ranks the rest by BPM (half/double time included), key and genre fit. Tracks both Beatport and Deezer suggest get a ★ and rank first. Each suggestion has a preview clip, **Queue** (adds it to the playlist and downloads it via Soulseek), a Soulseek search, and a Beatport buy link.

---

## 🎧 Audio Playback & Analysis

### Professional Player Interface
- **High-Fidelity Engine**: NAudio-powered playback with low-latency monitoring
- **Format Support**: MP3, FLAC, WAV, OGG, M4A, and more
- **Real-Time VU Meters**: Professional dual-channel peak monitoring
- **Waveform Visualization**: Interactive seekbar with detailed audio representation

### Advanced Audio Analysis
- **Real AI Genre & Mood Classification**: genre (87-class MTG-Jamendo taxonomy) and 5-way mood
  scoring (Happy/Sad/Relaxed/Party/Aggressive) computed in-process via ONNX Runtime
  (DiscogsEffnet embeddings + classifier heads), shown as ranked confidence bars in the Track
  Inspector rather than a single winner-takes-all label — replaces a previously silently-dead
  Essentia TensorFlow layer that produced empty results for every track
  (see [ARCHITECTURE.md](ARCHITECTURE.md) → Real-Time Genre, Mood & Embeddings)
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

## 🤝 Social Layer

- **Real Serving**: ORBIT isn't a pure download client — it serves files back to the Soulseek
  network from your shared folder, so your reported share count is real, not cosmetic
- **Contacts**: every peer you've downloaded from becomes a contact — see their online status,
  browse their full shared library, and view your download history with them
- **1:1 & Room Chat**: persistent chat with individual peers or public Soulseek rooms, including
  image attachments (built on the file-serving pipeline, since the Soulseek protocol itself has
  no attachment support)
- **Presence**: live online/away status for watched contacts, plus setting your own status
- **Notification Center**: persistent history (bell icon) for finished downloads and incoming
  messages, alongside OS-level Windows Action Center toasts — separate from the in-app toast
  popup
- **Known-Good-Peer Ranking**: peers you've successfully downloaded a track from before get a
  ranking bonus next time a search finds them again

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

### Mix & Cue Workspace (Flow Builder)
- **All-in-one transition editor**: click the ⇄ between two cards to open both decks' waveforms, transition presets and custom effects (EQ swap, echo, filter, Double Drop loop), mix-point picking and crossfade preview in one place
- **Set navigation**: ◀ Previous / Next ▶ transition, "Transition N of M", and a mini strip of the whole set — click any ⇄ to jump to that transition
- **In-place cue editing** (✏ Edit cues): drag cues on the waveform (snapped to the track's beat / bar / phrase grid), right-click to add, and edit name, role (with Rekordbox colour), hot-cue pad A–H or memory cue, colour, beat/bar nudges and 4/8/16-bar loops, with undo/redo; "⇥ Use as mix point" sets the transition from a cue, and the mix point follows a cue you drag
- **Auto-save**: cue edits save when moving to another transition or closing the editor
- **Play from a track**: the playlist Play button starts at the selected track; with **+ Mix** on, Play Track continues the mix from that track through the rest of the playlist with each pair's saved transition

### Cue Tools
- **One-click drops** (◆ Drop, key **D**, right-click "Drop here"; **1** / **2** force Drop 1 / Drop 2) in Cue Forge and the Flow Builder cue editor:
  - **Snap:** the drop lands on a bar line.
  - **Numbering:** drops are numbered by position.
  - **Build-in cues** are placed before each drop from the cue template: **[IN -16] [IN -8] [DROP 1]** on pads A–C and **[DROP 2]** on D–F.
  - **[OUT]** goes on pad G, 32 bars after the last drop.
  - **Auto cues** from analysis are removed. Placing, adding or moving a cue yourself hands the track to your cues; undo brings the auto cues back.
- **Linked build-in cues**: dragging a drop moves its build-in cues with it. A build-in cue you moved or renamed yourself is unlinked. [OUT] follows the last drop until you move it.
- **Cue templates** (`[Cues] DropCountdownMode`), also used by automatic cue generation:
  - **DnB / Bass** −16 −8
  - **Long Blend / House** −32 −16
  - **Quick Mix / Hip-Hop** −8 −4
  - **Custom** (`[Cues] CustomCountdownBars`)
  - **Off**
  - **Auto**: DnB for drum & bass, hardstyle and techno; Long Blend for house, tech house and trance; Quick Mix for hip-hop, pop and R&B
- **Manual cues are kept**: background re-analysis never adds auto cues back to a track where you placed drops yourself.
- **Fine-tune by ear**: ← → move the selected cue 1 beat, Shift 1 bar, Ctrl 10 ms; every press replays from the cue. In Flow Builder a playing mix pauses while you audition and resumes when the cues are saved
- **Precise beat grids**: BPM and grid are fitted to the beat tracker's ticks (no more whole-number or wrongly doubled BPMs), the downbeat comes from the bass structure, and Rekordbox's own grid is used for tracks it has analysed
- **Cue accuracy benchmark**: `Tests/CueBenchmark` scores auto cues against hand-placed Rekordbox cues, Rekordbox's own analysis, or your own hand-set drops

### AI Automix Engine
- **Similarity Search**: Cosine-distance matching over 128-dim audio embeddings stored per-track — `SimilarityIndex` with 1-hour TTL cache and thread-safe lazy-load
- **Playlist Optimization**: Greedy nearest-neighbour graph over Camelot distance, BPM delta, and EnergyScore with configurable per-factor weights (`PlaylistOptimizer`)
- **Energy Curve Sequencing**: Post-ordering pass reshapes any playlist into `Rising`, `Wave` (arch), or `Peak` (low-body + high-energy spike) energy profiles
- **Max-BPM-Jump Guard**: Configurable penalty rejects transitions wider than a set BPM range, preventing jarring key-tempo collisions
- **Seeded Ordering**: Optional fixed start/end track constraints for opening and closing track pinning
- **Structure-planned mixes**: playback mixes from each track's intro/outro sections in whole 8/16/32-bar phrases. Auto picks **Rolling** (the incoming drop lands as the outgoing main section ends) or **Relaxed**; **Drop Sync** (double drop) is a preset only. Vocal clashes are avoided, tempos are matched up to 6%, and an automatic mix never starts before half the outgoing track has played

### Background Processing
- **Job Queue**: `Channel<T>`-backed unbounded job queue (`BackgroundJobQueue`) with configurable concurrency
- **Progress Reporting**: Per-job `IProgress<JobProgress>` with fraction, description, and error capture — UI can subscribe to live progress events
- **Graceful Cancellation**: All analysis and stem jobs respect `CancellationToken` top-to-bottom; worker shuts down cleanly on app exit

### Professional Export Suite
- **Rekordbox XML**: Full Pioneer DJ export — `POSITION_MARK` hot-cue/memory-cue nodes (R/G/B color, pad slots 0-7) with hot-cue → memory-cue dual write, a real multi-anchor `TEMPO` beat-grid derived from ORBIT's own computed beatgrid (not a flat guess), nested playlist folders, and a colour-tag picker (8 Rekordbox swatches, right-click any track)
- **Merge-Mode Re-Export**: re-exporting to a `rekordbox.xml` that already exists merges into it instead of overwriting — Rating/Colour/Comments/cues you've since edited *inside* Rekordbox survive a re-export after new downloads land; only file-derived/analysis-owned fields (metadata, tempo grid, location) always refresh from ORBIT
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
