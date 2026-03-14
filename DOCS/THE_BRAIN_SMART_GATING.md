# The Brain: Smart Music Intelligence

**ORBIT** isn't just a downloader; it's a music intelligence engine. "The Brain" refers to the suite of features that leverage canonical metadata (from Spotify) to make smart decisions about file selection, organization, and tagging.

## Feature: Smart Duration Gating (Phase 0.4)

### The Problem: "Version Roulette"
When searching for a track on peer-to-peer networks like Soulseek, filenames are often unreliable. A search for "Strobe" by Deadmau5 might return:
1.  `deadmau5 - strobe.mp3` (10:37) - *The Club Mix*
2.  `deadmau5 - strobe (radio edit).mp3` (3:34) - *The Radio Edit*
3.  `deadmau5 - Strobe (Live).mp3` (6:00) - *A Live Version*

Legacy downloaders typically sort by **Bitrate** or **File Size**. If the 10-minute version is 320kbps and the 3-minute version is also 320kbps, the downloader has no way of knowing which one you *actually* want. You might end up with a Radio Edit when you wanted the extended journey, or vice versa.

### The Solution: Metadata-Driven Filtering
ORBIT solves this by using the **Canonical Duration** from your source library (Spotify).
- If you liked the **Radio Edit** on Spotify (Duration: 3:34), ORBIT knows you are looking for a file that is approximately 3 minutes and 34 seconds long.
- It will **actively reject** the 10-minute Club Mix, even if it has a high bitrate, because it doesn't match the *identity* of the track you requested.

### How It Works (Technical)
The logic is implemented in the `DownloadDiscoveryService` ("The Seeker").

#### 1. The "Window of Confidence"
We define a tolerance window of **±15 seconds**.
```csharp
var expectedDurationSec = track.Model.CanonicalDuration.Value / 1000.0;
var toleranceSec = 15.0; 

var smartMatches = candidates
    .Where(t => Math.Abs(t.Length - expectedDurationSec) <= toleranceSec)
    .ToList();
```
*   **Why 15 seconds?** It accommodates:
    *   Different silence padding (intro/outro).
    *   Slight variances in MP3 encoding padding.
    *   Regional release differences.

#### 2. The Filter Pipeline
1.  **Search**: `SearchOrchestrator` retrieves raw candidates from Soulseek.
2.  **Analyize**: The system checks if the requested track has a `CanonicalDuration` (from Spotify metadata).
3.  **Gate**:
    *   **If Metadata Exists**: We apply the 15-second filter.
        *   **Hit**: If we find matches within the window, we *discard* all other results (effectively promoting the right version to the top).
        *   **Miss**: If *no* matches are found within the window (e.g., rare Live bootleg), we log a warning (`🧠 BRAIN: No candidates matched...`) and **fallback** to the standard "Best Bitrate" selection.
4.  **Select**: The best candidate (now guaranteed to be the right version) is queued for download.

### Verification
You can see "The Brain" in action in the application logs:
> `[INF] 🧠 BRAIN: Smart Match Active! Found 3 candidates matching duration 214s (+/- 15s)`

This feature transforms ORBIT from a passive tool into an active curator, ensuring your local library perfectly mirrors the intent of your digital collection.
