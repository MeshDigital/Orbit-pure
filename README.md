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
- **Backend & Networking**: .NET 8.0 (C#) / Soulseek.NET
- **Database**: SQLite (WAL mode, optimized for concurrent reads/writes)
- **Audio Processing**: NAudio, Xabe.FFmpeg, Essentia
- **Machine Learning**: Microsoft ML.NET (LightGBM classifiers for 512-dimensional Essentia BLOBs)

### Key Features
1. **Pre-Download Heuristics**: Calculates expected file size vs actual size (Bitrate × Duration / 8) to preemptively filter mathematically impossible files (e.g., upscaled 64kbps disguised as 320kbps).
2. **Audio Spectral Analysis**: FFmpeg and Essentia sidecars analyze downloaded audio for frequency cutoffs to detect fake lossless (FLAC) files.
3. **Crash Recovery**: Enforces Journal-first logging. Downloads write 15-second heartbeat checkpoints to SQLite; operations continue seamlessly across unexpected reboots or crashes.
4. **API Integration**: Connects with the Spotify API (PKCE OAuth) and MusicBrainz to fetch accurate ID3 tags, ISRC codes, and deep producer/label relationships.
5. **Creative Workstation Capabilities**: Includes interactive multi-track timelines (`WaveformControl`) and SkiaSharp-powered visualizers (Vibe Radar, Genre Galaxy) for setlist preparation.

---

## Current Status & Recent Updates (February 2026)

The project is currently ~75% complete against its v1.0 goals. We recently implemented cross-component UI consolidation and deep audio analysis capabilities.

**Latest Technical Deliverables**:
- **512D Essentia Integration**: Rebuilt similarity algorithms to support 512-vector ML BLOBs for precise harmonic and "Vibe" matching.
- **Mission Control**: Centralized system health aggregator monitoring thread workloads, dead-letter recovery queues, and library schema integrity.
- **Vibe Radar**: A custom `SKCanvasView` utilizing normalized multi-dimensional mapping to visualize rhythmically and harmonically similar tracks based on mathematical proximity.
- **Forensic Librarian**: "Report as Fraud" pipelines handling local quarantines and DB cleanup for discovered file integrity violations.
- **Contextual Sidebar**: Dynamic folding library navigation reflecting context-aware commands (Mashups, Forensics, Metadata).

**Immediate Backlog**:
- Automated Rekordbox phrase parsing (extracting binary struct markers from USBs)
- Drop Detection engine for automatic climax-based cue points
- Surgical Processing (FFmpeg direct track truncation)

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
