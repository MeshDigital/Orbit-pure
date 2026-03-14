# Phase 1: Intelligent Ranking - Scoring Examples

This document shows concrete examples of how different files are scored by ORBIT's ranking system.

## Scoring Components

### Current Weights (Before Adjustment)
| Component | Weight | Purpose |
|-----------|--------|---------|
| Upload Speed (Free Slot) | 2000 pts | Immediate availability |
| Required Conditions | 1000 pts | Must-have filters pass |
| Preferred Conditions | 500 pts | Nice-to-have filters |
| **BPM Proximity** | **300 pts** | Musical alignment |
| Valid Length | 100 pts | Has duration metadata |
| Length Match | 100 pts | Duration similarity |
| Bitrate Threshold | 50 pts | Meets minimum quality |
| Bitrate Value | 50 pts (capped) | Quality score |
| Title Similarity | 200 pts | Name matching |
| Artist Similarity | 100 pts | Artist matching |

---

## Problem: BPM Overrides Quality

### Scenario 1: Basic Comparison
**Target:** "Artist - Title" (128 BPM from Spotify)

| File | Bitrate | Filename | BPM Score | Bitrate Score | Total | Winner? |
|------|---------|----------|-----------|---------------|-------|---------|
| A | 128kbps | `Artist - Title.mp3` | 150 (neutral) | 50 | **~200** | ❌ |
| B | 320kbps | `Artist - Title.mp3` | 150 (neutral) | 50 | **~200** | ⚠️ TIE |

**With BPM Tag:**
| File | Bitrate | Filename | BPM Score | Bitrate Score | Total | Winner? |
|------|---------|----------|-----------|---------------|-------|---------|
| A | 128kbps | `Artist - Title (128bpm).mp3` | **300** (match) | 50 | **~350** | ✅ WRONG! |
| B | 320kbps | `Artist - Title.mp3` | 150 (neutral) | 50 | **~200** | ❌ |

**Issue:** The 128kbps file WINS because BPM match (300 pts) > Bitrate advantage (0 pts).

---

## Proposed Solution: Bitrate Tiering

### Revised Weights
| Component | Weight | Change |
|-----------|--------|--------|
| BPM Proximity | 300 → **150 pts** | Reduced by 50% |
| Bitrate Value | 50 → **200 pts** | Increased 4x, remove cap |

### Revised Scoring
**With Adjustment:**
| File | Bitrate | Filename | BPM Score | Bitrate Score | Total | Winner? |
|------|---------|----------|-----------|---------------|-------|---------|
| A | 128kbps | `Artist - Title (128bpm).mp3` | 150 (match) | 160 | **~310** | ❌ |
| B | 320kbps | `Artist - Title.mp3` | 75 (neutral) | 400 | **~475** | ✅ CORRECT |

**Effect:**
- 320kbps files always beat 128kbps files (quality wins)
- BPM becomes a **tiebreaker** for files with similar bitrate
- FLAC still beats 320kbps MP3 (1411kbps FLAC = massive bitrate score)

---

## Real-World Examples

### Example 1: DJ Pool vs Casual Uploader
**Search:** "Deadmau5 - Strobe" (Spotify says 128 BPM, 10:37 duration)

| Rank | File | Score Breakdown | Total |
|------|------|-----------------|-------|
| 🥇 1 | FLAC, 1411kbps, "Strobe (128bpm).flac" | BPM: 150 + Bitrate: 1763 + Duration: 100 | **~2013** |
| 🥈 2 | MP3, 320kbps, "Strobe.mp3" | BPM: 75 + Bitrate: 400 + Duration: 100 | **~575** |
| 🥉 3 | MP3, 128kbps, "Strobe (128bpm).mp3" | BPM: 150 + Bitrate: 160 + Duration: 100 | **~410** |
| ❌ 4 | MP3, 320kbps, "Strobe (Radio Edit).mp3" (3:34) | **HIDDEN** (Duration gate: 10:37 vs 3:34 = 7 min diff > 30s) | **-∞** |

**Winner:** FLAC with BPM tag (best quality + musical alignment)

---

### Example 2: Equal Bitrate, BPM Tiebreaker
**Search:** "Artist - Title" (128 BPM)

| Rank | File | Score Breakdown | Total |
|------|------|-----------------|-------|
| 🥇 1 | MP3, 320kbps, "(128bpm) Artist - Title.mp3" | BPM: 150 + Bitrate: 400 | **~550** |
| 🥈 2 | MP3, 320kbps, "Artist - Title.mp3" | BPM: 75 + Bitrate: 400 | **~475** |

**Effect:** BPM acts as tiebreaker when bitrates are equal (correct behavior).

---

## Strict Gating Examples

### Fake File Detection
| File | Size | Duration | Bitrate | Status | Reason |
|------|------|----------|---------|--------|--------|
| `Track.mp3` | 40 MB | 600s (10 min) | 320kbps | ✅ Valid | 40MB ÷ 600s = 6.6 KB/s (reasonable) |
| `Track.mp3` | 3 MB | 600s (10 min) | 320kbps | ❌ **HIDDEN** | 3MB ÷ 600s = 0.5 KB/s (impossible) |

**Effect:** Corrupted/fake files are completely hidden from results (score = -∞).
