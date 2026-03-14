# ORBIT (formerly SLSKDONET): v1.0 Feature Roadmap

**Last Updated**: January 6, 2026
**Repository**: https://github.com/MeshDigital/ORBIT
**Current Status**: Phase 9 (Forensic) / Phase 10 (Prep Pipeline) Stabilized

---

## 🗺️ Completed Phases (Foundations)

### ✅ Phase 0: Foundation & Connectivity
**Goal**: Core infrastructure and secure connectivity.
*   **P2P Engine**: Robust Soulseek client (`SoulseekAdapter.cs`) handling search, download, and connection management.
*   **Spotify Integration**: OAuth 2.0 with PKCE (`SpotifyAuthService.cs`) and secure token storage (DPAPI/Keychain).
*   **Database**: SQLite with Write-Ahead Logging (WAL) for high concurrency (`AppDbContext.cs`).
*   **Atomic Operations**: ACID-compliant file operations (`SafeWriteService.cs`) preventing corruption.

### ✅ Phase 1: "The Brain" (Ranking & Search)
**Goal**: Intelligent result curation.
*   **Scoring Engine**: Strategy-based ranking (`QualityFirstStrategy`) weighing Bitrate, BPM, and Availability.
*   **Search Normalization**: Stripping noise ("Official Video", "Lyrics") for accurate matching.
*   **Contextual Search**: Adjusting ranking based on intent (e.g., "DJ Mode").

### ✅ Phase 2: Crash Resilience
**Goal**: Zero data loss.
*   **Recovery Journal**: Logs file operations to `CrashRecoveryJournal.cs` for startup recovery.
*   **Dead Letter Queue**: Quarantines repeat-crash causing files.

### ✅ Phase 3: Advanced Orchestration (Traffic Control)
**Goal**: Traffic Jam prevention.
*   **Multi-Lane Queue**: Express (P0), Standard (P1), Background (P10) lanes (`DownloadOrchestrationService`).
*   **Stall Detection**: `DownloadHealthMonitor` auto-retries stalled peers (>60s).
*   **Preemption**: High-priority tasks pause background downloads.

### ✅ Phase 4: Pro DJ Tools
**Goal**: Rekordbox/Serato Competitor.
*   **Harmonic Mixing**: Camelot Wheel integration (`HarmonicMatchService`) for Key/BPM compatibility.
*   **Rekordbox Export**: Streaming XML generation for Pioneer CDJs (`RekordboxService`).
*   **Monthly Drop**: Automated recent-tracks playlist generation.
*   **Binary Parsing**: Analyzing `.DAT/.EXT` files for beat grids and waveforms (`AnlzFileParser`).

### ✅ Phase 5: Self-Healing Library
**Goal**: Automated quality upgrades.
*   **Upgrade Orchestrator**: State machine for finding FLAC upgrades for 128kbps files.
*   **Atomic Swaps**: Checkpointed 8-step swap process (Lock -> Search -> Download -> Clone -> Swap).
*   **Metadata Cloning**: Preserving ratings and play counts during upgrades.

### ✅ Phase 15: Style Lab (Sonic Taxonomy)
**Goal**: User-defined musical classification.
*   **Style Buckets**: Users define "Neo-Trance" or "Liquid" by example tracks.
*   **Personal Classifier**: ML.NET + LightGBM engine trains *locally* on your examples.
*   **Auto-Tagging**: Library tracks are automatically assigned a `DetectedSubGenre` with confidence scores.
*   **Dynamic Filtering**: Instant style chips in the Library UI.

### ✅ Phase 20: Smart Playlists 2.0
**Goal**: Dynamic, criteria-based playlists.
*   **Smart Criteria**: JSON-backed rules for Energy, BPM, and Genre.
*   **Auto-Population**: `SmartPlaylistService` dynamically evaluating tracks.
*   **Persistence**: Database schema enabled for `IsSmartPlaylist` and criteria storage.

---

## 🚧 Active Development

### ✅ Phase 8: Sonic Integrity (Alpha)
**Goal**: True Audio Quality Verification.
*   **Spectral Forensics**: Headless FFmpeg analysis (`SonicIntegrityService`) detecting frequency cutoffs (16k/19k/21k).
*   **Essentia Engine**: `EssentiaAnalyzerService` extracting real machine learning metrics (BPM, Key, Energy, BpmConfidence).
*   **Drop Detection**: `DropDetectionEngine` for finding mixing points.

### 🚧 Phase 12: UX 2.0 (Search & Curation)
**Goal**: "Curation Assistant" UI.
*   **Visual Hierarchy**: Gold/Silver/Bronze badges, heatmaps.
*   **Forensic Inspector**: UI for verifying Upscaled vs Native files.

### ✅ Phase 13: Audio Intelligence (The "Active" Upgrade)
**Goal**: From Passive Analyzer to Active DJ Assistant.
*   **Phase 13A: Forensic Librarian**
    *   **Dynamic Range**: Detecting over-compressed ("Sausage") masters via `dynamic_complexity`.
    *   **Drift Detection**: Warning about variable tempo via `bpm_histogram`.
    *   **Energy Intensity**: Combining RMS and Loudness for real energy mapping.
*   **Phase 13B: Visual Truth**
    *   **Spectrogram UI**: SPEK-style frequency visualization.
*   **Phase 13C: AI Layer**
    *   **Deep Learning**: TensorFlow models (`.pb`) for Vocal Detection and Mood.
    *   **Active Suggestions**: "Mixes Well With" panel and "Vibe Radar".

### 🚧 Phase 16: Applied Intelligence (The Cognitive Librarian)
**Goal**: Automate management using ML predictions.
*   **Auto-Sorter**: Physical file organization based on `PredictedStyle`.
*   **Smart Curation**: "Vibe Match" playlist generation via Cosine Similarity.
*   **Imposter Detector**: Flagging metadata mismatches (Genre vs Vibe).

---

## 🔮 Future Phases

### Phase 6: Mission Control Dashboard
**Goal**: Proactive command center.
- [ ] Tier 1: Aggregator Facade (`MissionControlService`).
- [ ] Live Operations Grid.
- [ ] System Health Monitoring (Zombie Processes).

### Phase 7: Mobile Companion App (Q2 2026)
**Goal**: Remote management.

### Phase 9: Hardware Export
**Goal**: Direct USB Sync.
- [ ] FAT32 USB syncing for Denon/Pioneer hardware.

---

## 📊 Performance Targets
| Metric | Target | Current |
| :--- | :--- | :--- |
| **Startup** | < 2s | **~1.5s** |
| **Crash Recovery** | 100% | **100%** |
| **Search Speed** | < 5s | **~2-4s** |
