# 🎯 Focus: Import & Enrichment Pipeline Repair

**Status**: Active  
**Last Updated**: December 27, 2025  
**Status**: Part 1 Completed ✅  
**Strategic Shift**:
1.  **Local Audio Analysis First**: Prioritize FFmpeg/Essentia for audio features (BPM/Key/Energy) to remove reliance on Spotify's locked API.
2.  **Robust Metadata Enrichment**: ✅ Rebuilt the Spotify Metadata pipeline using a clean, persistent task-based architecture (6-Layer).
3.  **Formalized Import Flow**: Standardize "Paste & Import" for CSV/Text to Albums.

---

## Part 1: Spotify Enrichment Pipeline Repair
**Problem**: The current pipelines are disjointed. Tasks are lost, DB writes fail silently, or UI doesn't update.
**Solution**: Implement a persistent **6-Layer Architecture**.

### 1️⃣ DB Layer: `enrichment_tasks` Table
A dedicated table to track every enrichment request.
```sql
TABLE enrichment_tasks (
    id UUID PRIMARY KEY,
    track_id UUID NOT NULL, -- FK to PlaylistTrack
    album_id UUID,          -- FK to AlbumEntity
    status TEXT NOT NULL,   -- 'queued', 'running', 'done', 'error'
    created_at DATETIME,
    updated_at DATETIME,
    error_message TEXT
)
```

### 2️⃣ Task Creation Layer
*   **Trigger**: Track added via Import, Playlist, or Manual Add.
*   **Action**: Insert row into `enrichment_tasks` with status `queued`.
*   **Implementation**: `ImportOrchestrator` and `DownloadManager` must emit events or directly write to this table.

### 3️⃣ Orchestrator Layer (`MetadataEnrichmentOrchestrator`)
A background service that polls for work:
1.  `SELECT * FROM enrichment_tasks WHERE status = 'queued' ORDER BY created_at LIMIT 1`
2.  Mark as `running`.
3.  Dispatch to Spotify Client.
4.  Handle Result (Write DB / Mark `done` / Mark `error`).

### 4️⃣ Spotify PKCE Client Layer
*   **Existing**: `SpotifyAuthService` (Working).
*   **Required Endpoints**:
    *   Search: `q=track:NAME artist:ARTIST` (Get ID, Album Art, Duration).
    *   Tracks: `/v1/tracks/{id}` (Get details).
    *   Albums: `/v1/albums/{id}` (Get label, release date, art).

### 5️⃣ DB Write Layer: `track_metadata` / Entity Updates
*   **Destination**: Update `PlaylistTrack` (or new `TrackMetadata` Entity).
*   **Fields**: `SpotifyId`, `NormalizedTitle`, `NormalizedArtist`, `AlbumArtUrl`, `DurationMs`, `ReleaseDate`, `Label`.
*   **Strategy**: `ON CONFLICT DO UPDATE`.

### 6️⃣ UI Layer
*   **Bindings**: UI must observe the specific fields populated by the enrichment worker.
*   **Visuals**: Show "Enriched" icon/badge. Show Album Art.

---

## Part 2: Local Audio Analysis (The "No-API" Pivot)
**Goal**: Get BPM, Key, and Energy without Spotify.

### Option A: Enhanced FFmpeg (Immediate)
*   **Extract**: Duration, Bitrate, Codec, Loudness (LUFS), Peak.
*   **Store**: `AudioAnalysis` entity linked to Track.

### Option B: Essentia / Aubio (Next Phase)
*   **Integrate**: Native binaries or CLI wrappers.
*   **Extract**: BPM (Onsets), Key (Spectral Peaks).

---

## Part 3: Import & Album Workflow
**Goal**: "Paste & Go" experience.

### 1. CSV / Text Import
*   **Input**: Raw text paste.
*   **Parser**: Regex to extract `Artist - Title`.
*   **Action**: Map to `Track` objects.

### 2. Album Mapping
*   **Decision**: "New Album" (Create Entity) vs "Existing Album" (Link Entity).
*   **Visual**: Album Editor showing tracks in buckets.

### 3. Download Center "Album View"
*   **Display**: Group tracks by AlbumID.
*   **Metrics**: Album progress %.
*   **Control**: Pause/Resume Album.

---

## 📅 Immediate Roadmap (Phase 1)

**Step 1: Diagnostics**
*   [x] Identify WHICH layer is broken (Creation, Orchestration, Execution, Persistence, UI).
*   [x] Inspect `AppDbContext` for missing tables.

**Step 2: Foundation (Schema)**
*   [x] Create `EnrichmentTask` Entity & Migration.
*   [x] Create `TrackMetadata` Entity (or verify `PlaylistTrack` fields).

**Step 3: Pipeline Rebuild**
*   [x] Implement `EnrichmentTaskRepository`.
*   [x] Implement `EnrichmentOrchestrator` (Poll Loop).
*   [x] Wire up `TaskCreation` in `ImportOrchestrator`/`DownloadManager`.
*   [x] **Validation**: Verified with manual import tests.

**Step 4: Import UI**
*   [ ] Formalize "Paste & Import" Dialog.
