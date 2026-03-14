# ORBIT v1.0 - Unified Development Roadmap  
**Last Updated**: December 28, 2025  
**Overall Completion**: 78%  

---

## 🎯 Strategic Status: Stabilization Phase (8-Week Focus)

**Current Priority**: Build trust through reliability before adding features  
**Next Milestone**: Speed & UX Optimization (Month 2)

---

## ✅ **FOUNDATION TIER** (100% Complete)

### Core Infrastructure
- [x] **Database**: SQLite with WAL mode, 6 performance indexes, automatic backups
- [x] **P2P Engine**: Robust Soulseek client with connection resilience
- [x] **Event Bus**: Strongly-typed event pub/sub architecture
- [x] **DI Container**: Avalonia + Microsoft.Extensions.DependencyInjection
- [x] **Crash Recovery**: Atomic file operations with `.part` workflow and journal
- [x] **Spotify Auth**: PKCE OAuth 2.0 with platform-specific secure token storage (Windows DPAPI, macOS Keychain, Linux Secret Service)

### Download System (Mission Critical)
- [x] **Queue Orchestr ation**: 3-lane priority (Express/Standard/Background) with lazy hydration
- [x] **Health Monitor**: Automatic stall detection, peer switching, blacklisting
- [x] **Atomic Downloads**: Verified transactions with resume capability
- [x] **Concurrency Control**: Dynamic semaphore (1-20 slots) with UI slider
- [x] **State Persistence**: Priority levels survive app restarts

### Analysis Pipeline (**NEW - Dec 28**)
- [x] **Waveform Generation**: Integrated into download completion flow
- [x] **FFmpeg Technical Analysis**: LUFS, dynamic range, integrity checks
- [x] **Essentia Musical Intelligence**: BPM, Key, Energy, Danceability
- [x] **Event-Driven Refresh**: Track Inspector auto-updates on analysis completion
- [x] **Glass Box Queue**: Observable service with 45s timeout watchdog, pause/resume capability
- [x] **Global Status Bar**: Animated queue visibility in MainWindow with ETA calculation
- [ ] **Forensic Monitor**: "Human-in-the-loop" UI to explain AI rankings and allow manual score overrides
- [ ] **Forensic Lockdown**: UI "Safe Mode" to auto-throttle heavy AI tasks during active DJ sets/playback

---

## 🧠 **INTELLIGENCE TIER** (90% Complete)

### Metadata Enrichment
- [x] **Spotify Integration**: Track/Album/Playlist imports with ISRC, artwork, genres
- [x] **Background Worker**: Priority-based enrichment (Playlists first, then library)
- [x] **Batch Optimization**: 50-track chunking with smart retry logic
- [x] **Musical Features**: Energy, Valence, Danceability from Spotify Audio Features API
- [x] **Global Circuit Breaker**: 5min backoff on 403 errors

### Search Intelligence ("The Brain 2.0")
- [x] **Ranking Engine**: Bitrate × BPM × Availability × Match Quality scoring
- [x] **Smart Presets**: Balanced, DJ Mode, Audiophile, Data Saver (Dec 28)
- [x] **Real-Time Control Surface**: Ranking sliders with auto-save (Dec 28)
- [x] **Visual Hierarchy**: Gold/Silver/Bronze badges with heatmap opacity
- [x] **Integrity Badges**: Gatekeeper verification status with tooltips
- [x] **Bi-Directional Sync**: Search tokens ↔ Filter HUD synchronization
- [ ] **Fuzzy Duration Matching**: ±15s tolerance for remix detection (90%)
- [ ] **VBR Fraud Detection**: Frequency cutoff analysis for upscaled files

### Harmonic Matching (DJ Features)
- [x] **HarmonicMatchService**: Camelot Wheel theory implementation
- [x] **Key Compatibility**: Perfect, compatible, relative major/minor detection
- [x] **BPM Matching**: ±6% beatmatching range
- [x] **Mix Helper Sidebar**: Real-time suggestions with 250ms debounce
- [ ] **Smart Playlist Generator**: Auto-create DJ sets from library

---

## 📊 **LIBRARY & UX TIER** (85% Complete)

### Library Management
- [x] **Multi-Select**: Batch operations for tracks and playlists
- [x] **Drag-Drop Import**: Spotify URLs with ghost row visualization
- [x] **Real-Time Status**: Live badges for download/enrichment progress (Dec 28)
- [x] **Enhanced Track Cards**: Integrity shields, enrichment sparkles (Dec 28)
- [x] **Duration & File Size Display**: Added to track rows (Dec 28)
- [x] **Harmonic Sidebar**: Key/BPM compatibility visualization
- [ ] **Column Reordering**: Draggable TreeDataGrid columns with persistence
- [ ] **UI Virtualization**: VirtualizingStackPanel for 50k+ track libraries (CRITICAL)
- [ ] **Virtualization 2.0**: Shimmer loading, asynchronous prefetching, and delta-updates for viewport-only data flow

### Player & Playback
- [x] **NAudio Engine**: ASIO/WASAPI support replacing LibVLC
- [x] **VU Meters**: Real-time L/R peak monitoring with exponential decay
- [x] **Pitch Control**: Hardware-style tempo (0.9x - 1.1x)
- [x] **Queue Management**: Drag-drop reorder, add/remove, persist order
- [x] **Waveform Control**: Avalonia component for professional rendering
- [ ] **Now Playing Page**: Full-screen view with large artwork (Deferred)
- [ ] **Hot Cues UI**: Interactive waveform with Rekordbox `.ANLZ` markers

### Search & Discovery UX
- [x] **Three-Zone Dashboard**: Initiator → Brain → Gatekeeper layout (Dec 28)
- [x] **Multi-Line Templates**: Dense metadata display (Artist-Title / Technical)
- [x] **Album Bento Grid**: Glassmorphism cards with reactive status
- [x] **Skeleton Screens**: Shimmer placeholders for perceived performance
- [x] **Downloads Transparency**: Failure reasons visible, Force Retry support
- [ ] **Vibe Search**: Natural language parsing ("late night 124bpm flac")

---

## 🏗️ **DJ INTEGRATION TIER** (70% Complete)

### Rekordbox Export
- [x] **XML Generation**: Streaming writer for efficient performance
- [x] **Monthly Drop**: Export recent tracks to hardware-ready XML
- [x] **Key Normalization**: Standard → Camelot → OpenKey conversion
- [x] **Metadata Safety**: XmlSanitizer for hardware compatibility
- [ ] **Settings Hardening**: Interactive path validation with LED status
- [ ] **USB Sync**: FAT32 translation UI (C:/ → /Volumes/USB/)
- [ ] **RekordboxStatus Column**: 🔵 Synced, ⚪ Pending, 🔴 Missing badges

### Rekordbox Analysis Preservation (RAP)
- [x] **Binary `.ANLZ` Parsing**: Read `.DAT`, `.EXT`, `.2EX` files
- [x] **Supported Tags**: PQTZ (beat grid), PCOB (cues), PWAV (waveform), PSSI (structure)
- [x] **XOR Descrambling**: Song structure phrases (Intro/Verse/Chorus/Outro)
- [x] **Companion Probing**: Auto-discover analysis in `ANLZ` subfolders
- [ ] **Metadata Cloner Integration**: Preserve during quality upgrades

---

## 🔄 **SELF-HEALING TIER** (95% Complete)

### Automatic Quality Upgrades
- [x] **LibraryScanner**: Batch processing with 50 tracks/batch yield pattern
- [x] **8-Step Atomic Swap**: Lock → Search → Download → Clone → Journal → Backup → Swap → Update
- [x] **State Machine**: 9 states with rollback logic
- [x] **MetadataCloner**: Cross-format transfer (ID3 ↔ Vorbis ↔ APE)
- [x] **FileLockMonitor**: Dual-layer safety (Player + OS exclusive lock)
- [x] **FLAC-Only Mode**: Conservative strategy (128/192 MP3 → FLAC)
- [x] **7-Day Cooldown**: Prevents redundant rescanning
- [x] **Gold Status Exclusion**: Respects user-verified tracks
- [ ] **Upgrade Scout UI**: Progress visualization in Library

---

## 🚀 **OPTIMIZATION & SCALE TIER** (Next Priority - 45% Complete)

### Performance (CRITICAL for Scalability)
- [x] **WAL Mode**: Write-Ahead Logging for concurrency
- [x] **Throttled Updates**: 4 FPS dashboard refresh to prevent UI flooding
- [x] **Connection Pooling**: Dedicated journal write connection
- [x] **Index Audit**: Covering indexes for library queries
- [ ] **UI Virtualization**: VirtualizingStackPanel for large libraries ⭐⭐⭐ URGENT
- [ ] **Lazy Image Loading**: ArtworkProxy for viewport-based loading
- [ ] **Parallel Library Scan**: Multi-core processing (2h → 10min for 10k tracks)
- [ ] **Memory-Mapped Files**: 50% RAM reduction for batch operations

### UX Polish
- [ ] **Progressive Interaction**: Button progress bars, optimistic updates
- [ ] **Smart Empty States**: Contextual messages with action buttons
- [ ] **Health Dashboard**: Live DeadLetter status with one-click recovery
- [ ] **Performance Overlay**: Debug mode FPS/memory/query monitor (Ctrl+Shift+P)
- [ ] **Glass Engine Dashboard**: Visual "Vibe Clusters" showing real-time batch progress with glowing 60fps animations
- [ ] **Semantic Theming**: Dynamic row colors based on AI Confidence Scores (High Confidence = Green, Review Needed = Amber)
- [ ] **Command Bar (Ctrl+K)**: Command palette for power-user library management (e.g., "Analyze last import", "Boost priority")

---

## 🌌 **ADVANCED FEATURES TIER** (Future - 20% Complete)

### Intelligence
- [ ] **Track Fingerprint**: Composite hash (ISRC + Spotify ID + normalized metadata)
- [ ] **Duplicate Detection**: Hash-based deduplication across playlists
- [ ] **Uploader Trust Scoring**: Queue length + free slot prioritization
- [ ] **Confident Metadata**: Store 0.0-1.0 confidence scores
- [ ] **Genre Galaxy**: Visualization of library by genres
- [ ] **Spectrogram Comparison**: Forensic view for deep-diving into AI detection logic vs. raw audio signal
- [ ] **Haptic/Audio Feedback**: Subtle auditory cues when large analysis batches complete

### Architectural
- [ ] **Unified Pipeline Orchestrator**: 8-stage download coordination
- [ ] **Event Replay**: Debug tool with last 1000 events
- [ ] **Profile-Based Tuning**: Audiophile/DJ/Balanced scoring presets
- [ ] **Command Pattern**: Undo/Redo for library operations (Ctrl+Z)
- [ ] **State Pattern**: Download state machine for cleaner transitions
- [ ] **Headless Worker**: Decouple `AnalysisQueueService` into a process that survives UI crashes

### Testing & Stability
- [ ] **Automated Stress Tests**: 500-track import, network drops, corrupt files
- [ ] **Stability Mode Build**: Conditional verbose logging for 8-week freeze
- [ ] **Nightly CI**: GitHub Actions for regression testing

---

## 📈 **COMPLETION PERCENTAGES BY AREA**

| Area | Complete | Notes |
|------|----------|-------|
| **Foundation** | 100% | ✅ Solid base |
| **Download System** | 95% | VBR detection pending |
| **Analysis Pipeline** | 95% | ✅ NEW (Dec 28) |
| **Search Intelligence** | 90% | Fuzzy matching pending |
| **Library UI** | 85% | Virtualization CRITICAL |
| **DJ Integration** | 70% | USB sync pending |
| **Self-Healing** | 95% | UI polish pending |
| **Performance** | 45% | ⚠️ URGENT PRIORITY |
| **Advanced Features** | 20% | Post-stabilization |

---

## 🎯 **STRATEGIC PRIORITIES (Next 30 Days)**

### Week 1-2: Performance & Scale
1. ⭐⭐⭐ **UI Virtualization** (6h) - Support 50k+ libraries
2. ⭐⭐⭐ **Lazy Image Loading** (4h) - 80% memory reduction
3. ⭐⭐ **Parallel Library Scan** (2h) - 12x faster scanning

### Week 3-4: UX Polish
4. ⭐⭐⭐ **Health Dashboard Integration** (4h) - Visualize recovery status
5. ⭐⭐ **Progressive Interaction** (5h) - Skeleton screens, button progress
6. ⭐⭐ **Smart Empty States** (2h) - Contextual action prompts

### Optional Enhancements
7. ⭐ **Column Reordering** (3h) - User customization
8. ⭐ **Now Playing Page** (3h) - Full-screen player view
9. ⭐ **Upgrade Scout UI** (2h) - Self-healing progress

---

## 🔬 **TECHNICAL DEBT REGISTER**

### High Priority
- [ ] **N+1 Query Pattern**: Eager loading for project track counts
- [ ] **Soft Deletes**: `IsDeleted` + `DeletedAt` for audit trail
- [ ] **Duplicate batch detection**: Check `addedInBatch` set during import
- [ ] **UI Thread Safety**: Wrap all event bus property updates in Dispatcher

### Medium Priority
- [ ] **Drag-Drop Positioning**: Fix high-DPI transformations (VisualRoot/PointToClient)
- [ ] **Selection Robustness**: Replace `Task.Delay` with reactive "Wait until exists" logic
- [ ] **Shared Folder Logic**: Implement backend for configured sharing (UI-only currently)
- [ ] **Status Converter**: Centralized DB string → enum mapping

---

## 📊 **METRICS DASHBOARD**

| Metric | Target | Current | Status |
|--------|--------|--------|--------|
| **Startup Time** | \u003c 2s | ~1.5s | ✅ Excellent |
| **Crash Recovery** | 100% | 100% | ✅ Verified |
| **UI FPS** | 60 | 30 | ⚠️ Needs virtualization |
| **Search Speed** | \u003c 5s | ~2-4s | ✅ Good |
| **Memory Usage** | \u003c 500MB | ~300-600MB | ⚠️ Variable |
| **Queue Throughput** | 20 tracks/min | 15 tracks/min | ✅ Good |

---

## 🏆 **RECENT ACHIEVEMENTS (December 2025)**

### December 28 (**TODAY**)
- ✅ **Audio Analysis Pipeline**: End-to-end FFmpeg + Essentia integration
- ✅ **Glass Box Queue**: Observable service with pause/resume, timeout watchdog
- ✅ **Global Status Bar**: Animated pulse, ETA calculation, event-driven updates
- ✅ **Library Display**: Duration and file size now visible
- ✅ **Track Inspector Auto-Refresh**: Real-time analysis completion updates

### December 26-27
- ✅ **Search 2.0**: Control Surface with ranking sliders, presets, visual hierarchy
- ✅ **Library Cards**: Real-time badges, integrity shields, enrichment indicators
- ✅ **Download Center**: Album grouping with aggregate progress

### December 23-25
- ✅ **Self-Healing Library**: 8-step atomic upgrades with state machine
- ✅ **Rekordbox Preservation**: Binary `.ANLZ` parsing with XOR descrambling
- ✅ **High-Fidelity Player**: NAudio engine with VU meters
- ✅ **Background Enrichment**: Spotify features (Energy/Valence/Danceability)

---

## 🎨 **FUTURE VISION (Post-1.0)**

### Mobile Companion (Q2 2026)
- Remote queue management
- Push notifications for completion
- Mobile-optimized search

### Hardware Export
- Denon Engine Prime export
- Pioneer rekordbox.xml advanced features
- Traktor NML export

### Social Features
- Share playlists via ORBIT links
- Collaborative playlist building
- Community quality ratings

---

**Maintained By**: MeshDigital & AI Development Team  
**License**: GPL-3.0  
**Repository**: https://github.com/MeshDigital/ORBIT
