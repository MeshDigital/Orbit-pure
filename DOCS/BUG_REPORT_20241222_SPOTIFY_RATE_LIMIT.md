# Bug Report: Spotify Enrichment Rate Limit (403 Forbidden)

## Issue Description
Users reported persistent `403 Forbidden` errors in the logs during "Download Album" operations. These errors occurred on the `https://api.spotify.com/v1/audio-features` endpoint.
The logs revealed that although the requests were valid, they were being sent **individually** for each track (e.g., `ids=1ID`) instead of in batches, causing 186+ requests to fire simultaneously. This flooded the Spotify API and triggered a Rate Limit (429) or Forbidden (403) response.

## Root Cause Analysis
- **Producer**: `DownloadManager` queues tracks for enrichment one-by-one (likely with small delays for DB or UI updates).
- **Consumer**: `MetadataEnrichmentOrchestrator` runs on a background thread.
- **Race Condition**: The Consumer was waking up, checking the queue, finding 1 item, and processing it immediately *before* the Producer could enqueue the next item.
- **Failure**: The "Batch" logic simply created batches of 1 item, defeating the purpose of the bulk API endpoint.

## Resolution: Smart Buffer (v1.2.2)
Implemented a "Smart Buffer" strategy in `MetadataEnrichmentOrchestrator.cs`:
1.  **Unconditional Wait**: When the orchestrator wakes up, it now waits `250ms` unconditionally.
2.  **Accumulation**: This delay allows the `DownloadManager` (Producer) to fill the queue with dozens of tracks.
3.  **Batch Processing**: After the delay, the orchestrator drains the queue (up to 50 items) and sends a **single** API request.

## Impact
- **API Calls**: Reduced from ~186 requests to ~4 requests for a typical album.
- **Errors**: Eliminated 403/429 errors.
- **Performance**: Faster enrichment and metadata retrieval (BPM, Keys).

## Related Files
- `Services/MetadataEnrichmentOrchestrator.cs`
- `Services/SpotifyBatchClient.cs`
