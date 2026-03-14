# Download Engine & UI Overhaul (Feb 2026)

## Overview
This document details the comprehensive overhaul of the ORBIT Download Center, bridging the gap between the Soulseek.NET network engine, the Pre-Download Heuristics (Forensic Librarian) pipelines, and the Avalonia UI. 

The primary goals were to gracefully manage transient states (such as "Searching" or "Queued") without alarming the user with false-positive "HIGH RISK" flags, provide granular progress tracking natively in the UI, and optimize network layer filtering to protect against soft-bans and excessive memory consumption.

## 1. UI Refinements & "HIGH RISK" Diagnostics
**Problem:** The UI was eagerly displaying a red `HIGH RISK` badge on tracks in the "Moving Now" queue while they were still in the `Searching` or `Queued` states. This occurred because `PlaylistTrackViewModel.IsHighRisk` was directly bound to `Model.IsFlagged`, which might carry over from previous failed heuristic attempts or be incorrectly set before the final result.

**Solution:**
*   **Context-Aware Binding:** Updated `PlaylistTrackViewModel.IsHighRisk` to calculate based on *both* `Model.IsFlagged` and the active track state.
*   **Implementation:** `IsHighRisk => Model.IsFlagged && State != PlaylistTrackState.Searching && State != PlaylistTrackState.Queued && State != PlaylistTrackState.Pending;`
*   **Result:** The "HIGH RISK" badge now only appears for finalized forensic failures, assuring users that the engine is accurately policing the quality and safety of downloads.

## 2. Granular UI Progress Updates (Live Console)
**Problem:** The download process was a black box. Users could not see what the engine was doing, why it rejected a peer, or if it hit a 3-second Speculative Trigger timeout.

**Solution:**
*   **Event-Driven Architecture:** Introduced `TrackDetailedStatusEvent` to carry granular status messages strictly bound to a track's `GlobalId` (`TrackUniqueHash`).
*   **ViewModel Integration:** Added an `ObservableCollection<string> LiveConsoleLog` to `PlaylistTrackViewModel`, subscribing to the new detailed status event. It maintains a rolling history of the last 100 log items per track.
*   **Avalonia UI Update (`StandardTrackRow.axaml`):** Built a toggleable, auto-scrolling "Live Console" panel directly beneath each track row using an `ItemsControl` inside a `ScrollViewer`. It's accessed via a new `ToggleButton` in the Action Hub.
*   **Engine Integration:** Updated `DownloadDiscoveryService.cs` (`The Seeker`) to publish rich `TrackDetailedStatusEvent` messages outlining search queries, rejected peers, heuristic decisions, and final match selections.

## 3. Soulseek.NET Enhancements
**Problem:** The engine was vulnerable to network-level soft-bans if it queried banned phrases. Additionally, searching without strict network constraints caused explosive object instantiation of `Track` models for files we would immediately reject locally.

**Solution:**
*   **Proactive Network Safety (`SoulseekAdapter.cs`):** Before executing a search via `SearchQuery.FromText(query)`, the system now parses the query against the `_excludedPhrases` dictionary. If a banned phrase is detected, the search immediately aborts and returns 0 results, keeping the network standing immaculate.
*   **Memory Optimization via Deferred Parsing:** Within `SearchAsync`, we extracted the `Bitrate` attribute from the raw `Soulseek.File` response *before* calling `ParseTrackFromFile`. If the bitrate violates the user's `(Min, Max)` constraints or if the format `extension` is not in the `formatSet`, we issue a `continue` and avoid the heavy overhead of creating a fully inflated `Track` object.
*   **Minor Version Validation:** Ensured `SoulseekMinorVersion = 2026` is accurately passed when instantiating the `SoulseekClient` in `SoulseekAdapter.cs`.

## Conclusion
These changes establish a robust, highly transparent, and performant download pathway. Users receive immediate, understandable feedback for all engine actions, while fundamental network protections ensure long-term stability and health on the Soulseek network.
