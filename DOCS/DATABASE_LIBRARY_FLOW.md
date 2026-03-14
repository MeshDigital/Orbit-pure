# Database → Library Display Flow

**Schema reference:** [DATABASE_SCHEMA.md](DATABASE_SCHEMA.md) for full table/column definitions and migrations.

**Purpose:** Explain how ORBIT persists track and playlist data and how that data is surfaced in the Library and Now Playing UI. This complements the schema reference and focuses on the end-to-end flow from ingest → download → library display.

## Storage Location & Engine
- Database: SQLite, stored at `%AppData%/SLSKDONET/library.db` (configured in AppDbContext).
- WAL mode + NORMAL synchronous for concurrency; shared cache enabled for multiple contexts.
- Primary entry point: [Data/AppDbContext.cs](Data/AppDbContext.cs).

## Core Tables (what they represent)
- **TrackEntity** (queue/pipeline state): persisted queue items with priority, format/bitrate overrides, Spotify metadata, integrity flags, and retry hints (`NextRetryTime`). Source playlist info (`SourcePlaylistId/Name`) ties queued items back to the job. See [Data/TrackEntity.cs](Data/TrackEntity.cs).
- **PlaylistJobEntity** (“Projects”): playlist/album import headers with cached counts (Total/Successful/Failed/Missing) for sidebar and project cards. See [Data/TrackEntity.cs](Data/TrackEntity.cs).
- **PlaylistTrackEntity** (job membership): per-playlist rows linked by `PlaylistId` and `TrackUniqueHash`; holds status, resolved file path, ranking info, Spotify IDs, waveform data, and per-track priority/overrides. See [Data/TrackEntity.cs](Data/TrackEntity.cs).
- **LibraryEntryEntity** (global library): deduplicated tracks keyed by `UniqueHash` + `Id` GUID, with dual-truth metadata (analyzed, Spotify, manual), enrichment fields (energy/valence/danceability), integrity level, and file paths for UI listing and playback. See [Data/TrackEntity.cs](Data/TrackEntity.cs).
- **LibraryHealthEntity**: a single-row snapshot for quality tiers, storage stats, and top-genre JSON used by dashboards. See [Data/LibraryHealthEntity.cs](Data/LibraryHealthEntity.cs).
- **QueueItemEntity**: persisted Now Playing queue order and current item marker. See [Data/QueueItemEntity.cs](Data/QueueItemEntity.cs).

## End-to-End Flow
1) **Ingestion / Queueing**
- Imports (Spotify/CSV/manual) create a **PlaylistJobEntity** plus **PlaylistTrackEntity** rows populated with source metadata.
- Tracks are also enqueued as **TrackEntity** rows (priority, preferred formats, min bitrate override, source playlist linkage) so the download orchestrator can resume after restarts.

2) **Download & Status Propagation**
- DownloadManager executes against **TrackEntity** items, updates `State`, `ErrorMessage`, and retry windows (`NextRetryTime`).
- On each state change, DatabaseService updates **PlaylistTrackEntity.Status** and cached counts on **PlaylistJobEntity** so project cards and Library playlist views stay in sync without N+1 queries.

3) **Library Materialization**
- On successful download, the file is hashed to `TrackUniqueHash` and upserted into **LibraryEntryEntity** (deduped). File path, bitrate, duration, format, and integrity markers are stored; original path is retained when swaps happen.
- Dual-truth fields (`BPM`, `SpotifyBPM`, `ManualBPM`, etc.) allow UI resolution logic (Manual → Analyzed → Spotify) without losing provenance.

4) **Enrichment & Intelligence**
- Spotify enrichment and library workers populate energy/valence/danceability, ISRC, album/artist IDs, and artwork onto both **TrackEntity**/**PlaylistTrackEntity** for transparency and **LibraryEntryEntity** for long-term display.
- Integrity and quality analysis fields (spectral hash, frequency cutoff, `QualityConfidence`, `IsTrustworthy`) feed the “Gold/Silver/Bronze” quality indicators and upgrade decisions.

5) **Display Layers**
- **Library page / Track list**: reads **LibraryEntryEntity** for global library rows and uses dual-truth fields to show resolved BPM/Key plus quality badges. Sorting/paging lean on indexed hashes and integrity fields.
- **Projects / Playlists**: sidebar/project cards use **PlaylistJobEntity** cached counts; playlist detail views read **PlaylistTrackEntity** to show per-track status, waveform previews, and per-playlist metadata.
- **Queue / Now Playing**: **QueueItemEntity** + **PlaylistTrackEntity** back the player queue; artwork and metadata are pulled from the playlist row first, then fall back to **LibraryEntryEntity** via `TrackUniqueHash`.
- **Library health dashboards**: **LibraryHealthEntity** supplies aggregate counts and storage metrics; Mission Control snapshots combine this with live download/search/enrichment state.

## Key Relationships (summary)
- PlaylistJobEntity (1) ── (M) PlaylistTrackEntity via `PlaylistId` (Projects table naming in AppDbContext).
- PlaylistTrackEntity (M) ── (1) LibraryEntryEntity via `TrackUniqueHash` (deduped global track).
- TrackEntity links to PlaylistTrackEntity via `GlobalId`/`TrackUniqueHash` plus `SourcePlaylistId` for resurrection after restarts.
- QueueItemEntity points to PlaylistTrackEntity to preserve queue order and current item across sessions.

## Operational Notes
- WAL mode and shared cache are configured in AppDbContext for concurrency-friendly reads during heavy download/import activity.
- Priority/override fields (`Priority`, `PreferredFormats`, `MinBitrateOverride`) live on both TrackEntity and PlaylistTrackEntity so user intent is preserved whether viewed in the queue, playlist, or library.
- Integrity/quality fields are present on both PlaylistTrackEntity and LibraryEntryEntity, enabling consistent quality badges in playlist views and the global library.
