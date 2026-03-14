# Drop Detection & Smart Cue Generation (Phase 17)

> [!IMPORTANT]
> This system is designed specifically for **Electronic Dance Music (EDM)** structure analysis. Results may vary for non-electronic genres.

## Overview
The "EDM Specialist" module uses a combination of signal processing (Essentia) and heuristic rules to identify key structural points in a track: **Intro**, **Breakdown**, **Build-up**, and **Drop**.

## 1. Drop Detection Algorithm
The drop detection engine looks for the "Double Cross" signature common in modern EDM:
1.  **Energy accumulation**: A steady rise in RMS energy and high-frequency content (Build-up).
2.  **The "Pre-Drop" gap**: A distinct < 1s silence or low-energy moment.
3.  **The Drop**: A massive return of bass (Low Band) and Kicks, often exceeding previous RMS levels.

### Key Metrics
- `DropConfidence` (0.0 - 1.0): Probability that the detected timestamp is a true drop.
- `DropTimeSeconds`: Exact timestamp of the first major drop.

## 2. Smart Cues (OrbitCue)
The system automatically generates "Hot Cues" for DJ software integration (Traktor/Serato/Rekordbox XML).

| Cue Name | Color | Typical Location | Purpose |
| :--- | :--- | :--- | :--- |
| **GRID** | White | 0.0s (or first beat) | Beatgrid anchor |
| **INTRO** | Blue | 0.0s - 30.0s | Mixing in point |
| **VOCAL** | Pink | Varies | First major vocal entry |
| **BUILD** | Orange | ~8-16 bars before drop | Tension rising |
| **DROP** | Red | `DropTimeSeconds` | Main energy release |
| **BREAK** | Cyan | Post-Chorus | Breakdown / Melodic section |
| **OUTRO** | Blue | Last 30-60s | Mixing out point |

## 3. Genre Templates
The system uses genre-specific templates (`GenreCueTemplates` table) to fine-tune cue placement.
- **Techno**: Prioritizes 16-bar phrasing and high-frequency loops.
- **Drum & Bass**: Looks for "The Switch" (bassline change).
- **House**: Standard Intro/Outro detection based on kick drum rhythm stability.

## 4. Integration
These cues are visualized in the **Intelligence Center** (`IntelligenceCenterView.axaml`) and can be used for instant navigation via the `SeekToCueCommand`.
