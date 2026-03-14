# ORBIT: 2026 Strategic Master Plan

**Status**: Active  
**Objective**: Evolve from a "High-Fidelity Downloader" into a "Creative Workstation" (DAW).

---

## 📅 H1 2026: The "Little DAW" Evolution
*Focus: Creative Workflow, Real-Time Audio, and Visual Editing.*

### Phase 1: The "Little DAW" (Current)
*   [x] Unified Theater Mode (Visuals + Controls).
*   [x] Stem Separation Integration.
*   [ ] **Basic Cue Points**: User-placed markers saved to database.
*   [ ] **Simple Multi-Track Mixing**: Volume/Mute/Solo per stem.
*   [ ] **Interactive Waveform**: Seek, Zoom, Scrub.

### Phase 2: Analysis & "Smart" Cues (Comparable to Mixed In Key)
*   [x] **Analysis Service 2.0**: Run Essentia to get Beats, Keys, Key Changes.
*   [ ] **Smart Markers**: Auto-generate Cue Points at "Drop" and "Breakdown".
*   [ ] **Key Overlay**: Show color-coded regions on the waveform where key modulation occurs.
*   [ ] **Drop Detection Engine**: Implement full climax-detection algorithm from Phase 4.2.
*   [ ] **Rekordbox Phrase Parsing**: Extract binary phrase markers (Chorus/Build) from USB drives.
*   [ ] **Semantic Waveform Zones**: Color-block the waveform by musical phrases (Intro/Build/Drop/Outro) with magnetic, AI-generated cue sliders.

### Phase 3: The Editor
*   [x] **Timeline View (Phase 4)**: Basic Zoomable/Scrollable Multi-track canvas.
*   [ ] **Slicing**: Allow user to split Stem waveform objects.
*   [ ] **Warping**: Elastic Audio (Time stretching) to align tracks to a master BPM.
*   [ ] **Surgical Processing**: Enable FFmpeg-based direct track editing (trimming/fading) from the UI.
*   [ ] **Set Intelligence**: Automated Flow-Builder using HarmonicMatchService and Energy curves.

### Phase 4: Mashup Lab & Flow Builder (The "MIK Killer")
*   [ ] **Mashup Lab Sandbox**: Dual-slot "A/B" environment with real-time key-shifting and demucs-powered Stem-Swap sliders to instantly preview mashups.
*   [ ] **Flow Builder Timeline**: Horizontal playlist curation with colored "Bridges" between tracks showing Harmonic/Energy compatibility.
*   [ ] **Forensic Matching**: Visual proof/alignment logs for every suggested transition.
*   [ ] **X-Ray Sparklines**: Render miniature "Energy Contour" sparkline graphs directly inside the Library List view for instant track shape recognition.
*   [ ] **Automated Native Export**: A single "Sync to USB" button that writes perfect Rekordbox XML and Serato GEOB tags, fully preparing USB sticks without external software.

---

## 🛠️ Active Backlog (Carried Over)
*Focus: Finishing valid features from 2025.*

### 🎨 Library & UX
- [x] **Layout & Proportion Scale-up**: Increased Sidebar (420px) and List widths to reduce cramped layout constraints. Responsive radar containers.
- [ ] **Rating & Likes System**:
    - [x] Backend Schema (Rating, IsLiked).
    - [x] UI Control (`StarRatingControl`).
    - [ ] **Integration**: Bind to `PlaylistTrackViewModel`, add to Track Rows and Now Playing.
    - [ ] **"Liked Songs" Playlist**: Auto-generated playlist logic.
- [ ] **UI Virtualization** (Critical):
    - [ ] Refactor Library List to use `ItemsRepeater` or `VirtualizingStackPanel` for performance with >10k tracks.
- [ ] **Smart Playlists**:
    - [x] Logic/Service.
    - [ ] **UI**: Visual editor for criteria (BPM > 120 AND Genre = 'Techno').

### ⚙️ Performance & Quality Assurance
- [ ] **Unit Testing** (Phase 7):
    - [ ] Create `SLSKDONET.Tests`.
    - [ ] Critical path tests (Drop Detection, Download Orchestration).
- [ ] **Essentia Hardening**:
    - [ ] Process management (Watchdog, Pool).
    - [ ] Binary distribution/checking.
- [ ] **Database Optimization**:
    - [x] Indexes (Done in Phase 1A).
    - [ ] Soft Deletes (Done in Phase 1C).
    - [ ] **Caching**: Implement `CachedLibraryService` to reduce DB hits.

---

## ✅ 2025 Achievements (Archive Summary)

### Core Infrastructure
- **P2P Engine**: Robust Soulseek client resilience.
- **Database**: SQLite WAL mode, Atomic Transactions.
- **Spotify**: OAuth 2.0 PKCE.

### Audio & Intelligence
- **Analysis Pipeline**: FFmpeg + Essentia integration.
- **Sonic Integrity**: Frequency cutoff detection.
- **Stem Separation**: Demucs powered separation.

### Library
- **Self-Healing**: 8-step upgrade workflow (MP3 -> FLAC).
- **Rekordbox Import**: Parsing `.ANLZ` files.
- **Search 2.0**: Ranked results, Visual Hierarchy.

---

*For detailed technical breakdown of the DAW Evolution, see `DOCS/DAW_EVOLUTION_PLAN.md` (Merged into this roadmap).*
