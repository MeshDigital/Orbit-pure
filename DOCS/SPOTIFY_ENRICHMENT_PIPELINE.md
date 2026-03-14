# Spotify Library Enrichment Pipeline

**Status**: Implemented (Dec 22, 2025)
**Version**: 1.0
**Related Components**: `SpotifyEnrichmentService`, `LibraryEnrichmentWorker`, `SearchResultMatcher`, `DownloadDiscoveryService`

---

## Overview

The Spotify Enrichment Pipeline is a 4-stage mechanism designed to enrich local library tracks with deep metadata from Spotify (BPM, Energy, Valence, Canonical Duration) without blocking user interaction or downloads. 

This pipeline transforms the application from a simple downloader into a "Smart" library manager that can make intelligent decisions about which files to download based on musical characteristics.

---

## Architecture: The 4 Stages

### Stage 1: Ingestion (Zero-Latency)
*   **Goal**: Get tracks into the DB immediately.
*   **Action**: 
    *   **Spotify Imports**: If importing from Spotify, we capture the `SpotifyId` immediately. `IsEnriched` is set to `true` (since we have the ID, ID-lookup is skipped).
    *   **CSV/Text Imports**: Tracks are created as "Placeholder" entities. `SpotifyId` is null. `IsEnriched` is `false`.
*   **Result**: User sees tracks instantly. No blocking "Processing..." dialogs.

### Stage 2: Identification (Background Pass 1)
*   **Worker**: `LibraryEnrichmentWorker` (Runs every 2.5s)
*   **Target**: Tracks with `SpotifyId == null`.
*   **Logic**:
    *   Queries Spotify Search API with `artist` and `title`.
    *   Updates `LibraryEntry` with `SpotifyId`, `SpotifyAlbumId`, and `CoverArtUrl`.
    *   *Note*: Does NOT fetch audio features yet to save API quota.

### Stage 3: Feature Enrichment (Background Pass 2)
*   **Worker**: `LibraryEnrichmentWorker`
*   **Target**: Tracks with `SpotifyId != null` but `IsEnriched == false` (or missing `BPM`).
*   **Logic**:
    *   Aggregates up to **50 IDs** into a batch.
    *   Calls `SpotifyClient.Tracks.GetSeveralAudioFeatures(ids)`.
    *   Updates `BPM`, `Energy`, `Valence`, `Danceability`.
    *   Sets `IsEnriched = true`.
    *   Sets `MetadataStatus` to "Enriched".

### Stage 4: Smart Matching ("The Brain")
*   **Component**: `SearchResultMatcher`
*   **Context**: Download Orchestration
*   **Logic**:
    *   When searching for a file on Soulseek, the engine checks the local `BPM` and `Duration`.
    *   **Duration Gate**: Rejects files that deviate > 15s from Spotify's canonical duration (filters out Radio Edits vs Extended Mixes).
    *   **BPM Match**: Prioritizes files where the filename contains a matching BPM (within 3 BPM).

---

## Data Schema

### `LibraryEntryEntity` / `TrackEntity`
| Field | Type | Purpose |
| :--- | :--- | :--- |
| `SpotifyTrackId` | `string` | Canonical link to Spotify ecosystem. |
| `BPM` | `double` | Tempo (e.g., 128.0). Critical for Smart Matching. |
| `Energy` | `double` | 0.0-1.0. Used for "Vibe" sorting. |
| `Valence` | `double` | 0.0-1.0. Used for "Mood" sorting. |
| `IsEnriched` | `bool` | Flag to prevent re-processing. |

---

## Code Reference

*   **Service**: `Services/SpotifyEnrichmentService.cs` - Handles API interactions (Search + Audio Features).
*   **Worker**: `Services/LibraryEnrichmentWorker.cs` - Batched background loop.
*   **Integration**: `Services/DatabaseService.cs` - Batch update methods (`UpdateLibraryEntriesFeaturesAsync`).
*   **UI**: `ViewModels/PlaylistTrackViewModel.cs` - Exposes `MetadataStatus` (Enriched/Identified/Pending).

---

## Usage

1.  **Import**: Drag & Drop a CSV or use Spotify Import.
2.  **Observe**: Watch the "Metadata" column in the Library.
    *   ⏳ -> 🆔 -> ✨
3.  **Download**: Right-click -> Download. The log will show "Smart Match Active" if metadata is present.

## Error Handling & Reliability

### Global Circuit Breaker (Spotify 403s)
*   **Problem**: If the Spotify API returns a `403 Forbidden` (e.g., due to scope restrictions on `GetAudioFeatures`), it can trigger a log spam loop if retried endlessly.
*   **Solution**: `SpotifyBatchClient` implements a **Global Circuit Breaker**.
    *   **Trigger**: Single `403 Forbidden` response.
    *   **Action**: Blocks **ALL** subsequent Spotify requests for **5 minutes**.
    *   **Impact**: Prevents log flooding. Enrichment gracefully fails (skips audio features), allowing downloads to proceed purely on metadata.

### Soulseek Login Gating
*   **Problem**: Searching before full login causes `InvalidOperationException`.
*   **Solution**: `SoulseekAdapter` waits for `SoulseekClientStates.LoggedIn` (not just `Connected`) before executing searches. Max wait: 10 seconds.
