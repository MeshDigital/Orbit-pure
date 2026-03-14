# Self-Healing & Hardening Strategy

## Overview
This document outlines the "Industrial-Grade" reliability features implemented in ORBIT (Phase 5) to ensure stability during automated library management and enrichment.

## 1. Ghost File Prevention (File Locking)
**Problem**: Upgrading a track (MP3 → FLAC) while it is loaded in a DJ application (Rekordbox, Serato, Foobar2000) causes `IOException` ("Access Denied") or "Ghost Files" (file disappears but new one fails to move).

**Solution**: "Pre-Flight Spin-Wait" & Safe Deferral.

### Mechanism
1.  **Pre-Flight Check**:
    *   Before any file operation, `FileLockMonitor` attempts to acquire an exclusive lock (`FileShare.None`).
    *   **Spin-Wait**: Loops 3 times with 1-second delay to filter out transient locks (e.g., Anti-Virus scans, Windows Explorer indexer).
2.  **Deferral**:
    *   If the file remains locked (indicating a persistent user session), the operation is **Deferred**.
    *   **Persistence**: The track is marked `Deferred` in the database with a `NextRetryTime` set to **5 minutes** in the future.
    *   **User Feedback**: UI shows the track as "Deferred (Locked)".
3.  **Cross-Volume Safety**:
    *   `UpgradeOrchestrator` explicitly detects if the upgrade targets a different physical drive.
    *   If so, it bypasses the non-atomic `File.Move` and uses a verified Transaction: `Copy` → `Size Verification` → `Delete`.

## 2. Spotify Quota Preservation (Enrichment Proxy)
**Problem**: Browsing a large library (>2,000 tracks) triggers rapid-fire requests to the Spotify API for `Audio Features` (BPM, Energy), quickly exhausting the sliding-window rate limit (429 Too Many Requests).

**Solution**: "Cache-First" Proxy & Circuit Breaker.

### Mechanism
1.  **Cache-First UI**:
    *   The `TrackInspector` logic is decoupled from the API.
    *   On track selection, it **only** queries the local SQLite database (`GetCachedMetadataAsync`).
    *   **Optimistic Hydration**: If data exists, it loads instantly (0ms latency).
    *   **Lazy Queueing**: If missing, it logs a "Cache Miss". The background `LibraryEnrichmentWorker` picks this up in its regular low-priority batch cycle.
2.  **Circuit Breaker**:
    *   `SpotifyEnrichmentService` monitors for `429 Too Many Requests` exceptions.
    *   **Trip**: When hit, it sets a global `_isServiceDegraded` flag and calculates `_retryAfter` based on the Spotify header.
    *   **Block**: All subsequent requests (foreground or background) are immediately rejected/skipped until the cooldown period expires.
