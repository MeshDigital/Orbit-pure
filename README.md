<div align="center">
  <h1>🛰️ ORBIT</h1>
  <p><strong>Organized Retrieval & Batch Integration Tool</strong></p>
  <p>A technical, reliability-focused P2P client and music workstation</p>
</div>

ORBIT interfaces with the Soulseek network but prioritizes strict file verification, structural analysis, and metadata fidelity over blind downloading. Originally designed as a "High-Fidelity Downloader," the project is actively evolving into a "Creative Workstation" (DAW lite), integrating ML audio analysis to support DJ and curation workflows.

> **LEGAL & PRIVACY NOTICE**
> ORBIT connects to the Soulseek P2P network. Your IP address is visible to other peers. We strongly advise using a VPN. This tool is provided for educational purposes and managing legally acquired content.

---

## Technical Overview

Traditional file sharing clients treat music as opaque blobs. ORBIT analyzes content at ingestion and post-download stages to ensure structural integrity and enrich files with musical metadata.

### Core Architecture
- **UI Framework**: Avalonia (cross-platform XAML)
- **Backend & Networking**: .NET 9.0 (C#) / Soulseek.NET
- **Database**: SQLite (WAL mode, optimized for concurrent reads/writes)
- **Audio Processing**: NAudio, Xabe.FFmpeg, Essentia, NWaves
- **Machine Learning**: Microsoft ML.NET (LightGBM classifiers for 512-dimensional Essentia BLOBs)

### Key Features
1. **Pre-Download Heuristics**: Calculates expected file size vs actual size (Bitrate × Duration / 8) to preemptively filter mathematically impossible files (e.g., upscaled 64kbps disguised as 320kbps).
2. **Audio Spectral Analysis**: FFmpeg and Essentia sidecars analyze downloaded audio for frequency cutoffs to detect fake lossless (FLAC) files.
3. **Crash Recovery**: Enforces Journal-first logging. Downloads write 15-second heartbeat checkpoints to SQLite; operations continue seamlessly across unexpected reboots or crashes.
4. **API Integration**: Connects with the Spotify API (PKCE OAuth) and MusicBrainz to fetch accurate ID3 tags, ISRC codes, and deep producer/label relationships.
5. **Creative Workstation Capabilities**: Includes interactive multi-track timelines (`WaveformControl`) and SkiaSharp-powered visualizers (Vibe Radar, Genre Galaxy) for setlist preparation.
6. **Professional Export Tools**: Enhanced CSV export with forensic metrics for music library analysis and integrity verification.
7. **Delta Library Syncing**: Intelligent incremental scanning that dramatically improves performance for large music collections.
8. **Beta-Ready Error Handling**: User-friendly crash reporting with technical diagnostics for professional beta testing.

---

## Current Status & Recent Updates (March 2026)

The project has reached **beta-ready status** with comprehensive error handling, professional export capabilities, and optimized performance. Phase 12 implementation is complete, making ORBIT suitable for professional music library management and beta distribution.

**Latest Technical Deliverables** (Phase 12: Professional Distribution & Beta Launch):
- **Global Exception Handling**: User-friendly crash reporting with system diagnostics and clipboard integration for beta testing feedback
- **Enhanced CSV Export with Forensic Data**: Professional-grade playlist export including spectral analysis metrics (HighFreqEnergyDb, LowFreqEnergyDb, EnergyRatio, IsTranscoded, ForensicReason)
- **Delta Scan Optimization**: Intelligent library syncing that only scans folders modified since last sync, dramatically improving performance for large collections
- **Error Report Dialog**: Avalonia-based crash reporting UI with technical details and system information display
- **Build System Cleanup**: Resolved compilation issues and optimized dependencies for production deployment

**Phase 11 Features**:
- **Orphaned Tracks Management**: Complete "Ghost File" purge system with physical library synchronization
- **Forensic Report Dialog**: Enhanced spectral energy analysis showing dB levels for audio integrity verification
- **Exponential Backoff Reconnection**: Network resilience with progressive retry delays for Soulseek disconnections
- **Performance Optimization**: Background threading for CPU-intensive spectral analysis

**Phase 10 & Earlier**:
- **512D Essentia Integration**: Rebuilt similarity algorithms supporting 512-vector ML BLOBs for precise harmonic matching
- **Mission Control**: Centralized system health monitoring with thread workloads and recovery queues
- **Vibe Radar**: Custom SKCanvasView for visualizing rhythmically and harmonically similar tracks
- **Forensic Librarian**: "Report as Fraud" pipelines with local quarantines and database cleanup
- **Contextual Sidebar**: Dynamic folding library navigation with context-aware commands

---

## Installation & Setup

1. Clone and build:
   ```bash
   git clone https://github.com/MeshDigital/ORBIT.git
   cd ORBIT
   dotnet restore
   dotnet build
   dotnet run
   ```
2. **First Run**: Configure your Soulseek credentials in the Settings menu.
3. **Dependencies**: Requires `ffmpeg` installed locally and in your PATH for spectral analysis services to function correctly.

## Project Structure
- `Views/Avalonia/` - XAML views and controls
- `ViewModels/` - Reactive state logic
- `Services/` - Core daemon services (DownloadManager, SonicIntegrityService, MissionControl)
- `Models/` - Database schemas and event records
- `DOCS/` & `TODO.md` - Advanced strategy plans and backlog items

## Contributing
Contributions for code refactoring, performance improvements, and algorithm optimization are welcome. Please ensure new logic adheres to atomic state patterns to prevent mid-download corruption.

License: GPL-3.0
