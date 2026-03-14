# 🧠 The Brain: Intelligent Scoring System

> **"ORBIT doesn't just find files. It finds the *right* file."**

Traditional Soulseek clients sort blindly by speed or bitrate. ORBIT employs a multi-tiered **Scoring Engine** ("The Brain") that evaluates every search result against 30+ criteria to rank the "Musical Truth" of a file.

This document details the exact algorithms, point values, and penalties used to sort your results.

---

## 🏗️ Architecture

The scoring logic is located in `Services/ResultSorter.cs` and governed by constants in `Configuration/ScoringConstants.cs`.

It uses a **Tiered Sorting Strategy**:
1.  **Availability Gating** (Is it download-able?)
2.  **Quality Tiers** (Is it good audio?)
3.  **Musical Compatibility** (Does it match the BPM/Key?)
4.  **Metadata Integrity** (Is the filename/tag match close?)

---

## 📊 The Scoring Formula

A result's final score is an aggregate of several weighted components:

```csharp
TotalScore = AvailabilityScore 
           + ConditionScore 
           + QualityScore 
           + MusicalScore 
           + MetadataScore 
           + StringMatchScore
```

### 1. Availability Score (The Gatekeeper)
If you can't download it, it's worthless.

| Condition | Points | Notes |
| :--- | :--- | :--- |
| **Free Upload Slot** | **+2000** | Massive boost. Immediate start. |
| **Empty Queue** | +10 | No wait time. |
| **Queued Item** | -10 per item | 100 items = -1000 pts. |
| **Long Queue (>50)** | -500 | Automatic penalty for overloading. |

### 2. Quality Tiers (The Currency)
Standardizes "Good Audio" into point classes.

| Tier | Bitrate / Format | Points |
| :--- | :--- | :--- |
| **Lossless** | FLAC, WAV, ALAC | **450** |
| **High Quality** | 320kbps MP3 / 256kbps AAC | **300** |
| **Medium Quality** | 192kbps - 319kbps | **150** |
| **Low Quality** | < 192kbps | `Bitrate * 0.5` (e.g. 128kbps = 64pts) |

> **VBR Buffer**: Files with VBR headers (e.g., LAME V0) averaging ~245kbps are promoted to High Quality via a "buffer" threshold (315kbps).

### 3. Musical Intelligence (The DJ Factor)
Requires `SpotifyMetadata` to be active.

| Factor | Points | Logic |
| :--- | :--- | :--- |
| **BPM Match** | **+100** | Exact match (±2 BPM) |
| **Close BPM** | +75 | Near match (±5 BPM) |
| **Key Match** | +75 | Exact Camelot Key (e.g., "8A") |
| **Harmonic Key** | +50 | Compatible Key (e.g., "8B", "9A") |

### 4. Metadata & String Matching
Ensures you get "Radio Edit" vs "Extended Mix" correctly.

| Component | Weight | Description |
| :--- | :--- | :--- |
| **Valid Length** | +100 | File has duration header |
| **Length Match** | +100 | Duration matches Spotify within ±5s |
| **Title Match** | 200 pts | Levenshtein distance (Title) |
| **Artist Match** | 100 pts | Levenshtein distance (Artist) |

---

## 🛡️ Guard Clauses (Automatic Failures)

Some files are rejected immediately (`Score = -Infinity`), hiding them from the user:

1.  **Duration/Version Mismatch**:
    *   If Spotify says 3:30 (Radio Edit) and file is 7:00 (Extended), it is hidden.
    *   Tolerance: ±30 seconds (Default).
2.  **VBR Fraud Detection**:
    *   Checks `Filesize vs Duration`.
    *   If a "320kbps" file is 50% smaller than mathematically possible, it's flagged as a transcoded "Fake" and hidden.
    *   EXCEPTION: "Silent Tail" detection allows FLACs with high compression if efficiency > 60%.

---

## 🧩 Strategy Pattern

ORBIT supports hot-swapping the scoring logic (Phase 2.4).

*   **BalancedStrategy** (Default): The weights described above.
*   **AudiophileStrategy** (Planned): `LosslessBase = 2000`. Forces FLAC to top.
*   **FastestStrategy** (Planned): `FreeSlotBonus = 5000`. Ignores quality for speed.

---

## 🔍 Debugging Scores

To see why a file ranked #1:
1.  Enable `Debug` logging in `appsettings.json`.
2.  Check logs for `Best fuzzy match score`.
3.  Look for "Strict strict check failed" traces for rejected items.

---

## ⚡ The "Express Lane" (Threshold Trigger)

**Phase 3C.4 Feature (Implemented Dec 2025)**

To match the speed of `slsk-batchdl`, "The Brain" now operates in real-time.

*   **Logic**: Every incoming result is scored immediately.
*   **The Threshold**: If a result scores **> 0.92 (92%)**:
    *   **Action**: The search is **aborted instantly**.
    *   **Result**: The track starts downloading immediately (usually within 2-4 seconds).
    *   **Why?**: A 92% match (Correct Title, Artist, Duration ±5s, Bitrate > 256kbps) is statistically guaranteed to be the correct file. There is no need to wait 30 seconds for marginally "better" options.

    *   **Why?**: A 92% match (Correct Title, Artist, Duration ±5s, Bitrate > 256kbps) is statistically guaranteed to be the correct file. There is no need to wait 30 seconds for marginally "better" options.

This moves ORBIT from a "Wait-and-Sort" architecture to a "Race-and-Replace" architecture.
- **Silver Match (Speculative)**: Score > 0.7. If no Gold match is found within **5 seconds**, the best Silver match starts automatically. (Implemented in Phase 3C.5).
- **Gold Match**: Score > 0.92. Starts immediately (< 1s), cancelling any search.
