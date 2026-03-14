# Audio Intelligence Upgrade: From Passive Player to Pro DJ Tool

## 1. The Core Philosophy
**Current State**: ORBIT acts as a "Passive Analyzer," telling you basic facts about a file (Bitrate, Sample Rate) and guessing its quality via heuristics.
**Future State**: ORBIT becomes an **Active Assistant** and **Source of Truth**. It should:
1.  **Prove** audio quality visually (Spectrograms), not just guess.
2.  **Suggest** what to play next (Harmonic Mixing).
3.  **Understand** the "Vibe" of a track (AI Mood/Danceability).

## 2. The "Firehose" Strategy (Data Expansion)
We are currently under-utilizing the `Essentia` engine. The upgrade moves from basic extraction to a full "Firehose" of data.
> **See Also**: [The Cortex: ML.NET Architecture](ML_ENGINE_ARCHITECTURE.md) for deep dive into the classification engine.

| Feature | Old (Garden Hose) | New (Firehose) | Benefit |
| :--- | :--- | :--- | :--- |
| :--- | :--- | :--- | :--- |
| **Loudness** | `0` (Hardcoded) | `EBU R128 Integrated` | Enables Auto-Gain / Volume Normalization |
| **Energy** | `0.5` (Fake) | `RMS` + `Onset Rate` | Accurate "Vibe" sorting |
| **Dynamics** | Ignored | `Dynamic Complexity` | Identify compressed vs. dynamic tracks |
| **Bass/Treble** | Ignored | `Spectral Centroid` | "Brightness" detection |
| **Rhythm** | Single BPM | `BPM Histogram` | Detect variable tempo / drifting drummers |

## 3. Visual Verification (Spectrograms)
Automated integrity checks (like "Sonic Scan 16kHz") are useful filters, but they can produce false positives on quiet tracks.
**The Solution**: A SPEK-style **Spectrogram Tab** in the Track Inspector.
- **Technology**: FFmpeg `showspectrumpic` filter.
- **Visuals**: Full frequency heat map (Black $\to$ Blue $\to$ Red $\to$ White).
- **Goal**: Allow users to *see* the difference between a true FLAC and a 128kbps upscale instantly.

## 4. Pro DJ Tools (Active Assistance)
The Inspector transforms from a debugging panel into a creative tool.

### A. "Mixes Well With" (Harmonic Matching)
Instead of just showing "Key: 8A", the Inspector lists tracks from your library that are harmonically compatible.
- **Logic**: Matches based on Camelot Wheel rules (e.g., 8A mixes with 8A, 9A, 7A, 8B).
- **UI**: Displays "Energy Boost (+1)" or "Smooth Mix" indicators.

### B. Vibe Radar
A visual representation of the track's feel using a radar chart or bar indicators.
- **Energy** (Intensity)
- **Danceability** (Rhythmic stability)
- **Valence** (Mood: Sad $\to$ Happy)

### C. Other Versions
Automatically detects duplicates or alternate versions (Radio Edit vs. Extended Mix) using strict and fuzzy title matching.

## 5. The AI Layer (Implementation Status)

**Phase 15.5 Upgrade**: We have successfully integrated **ML.NET** and **LightGBM** to power the "Personal Classifier".

### Capabilities
- **Personalized Vibe Detection**: Learns *your* specific definitions of genres (e.g., "Dark Techno" vs "Peak Time").
- **Local Training**: Models are trained on-device in the "Style Lab".
- **Reliability**: Uses a "Confidence Cliff" to avoid bad guesses.

### Future Deep Learning Models (Essentia TensorFlow)
While ML.NET handles classification, we still plan to actuate Essentia's .pb models for specific feature extraction:
1.  **Vocal Detection**: `voice_instrumental-musicnn-msd.pb`
    - *Utility*: Warns about "Vocal Clashes" if mixing two vocal tracks.
2.  **Danceability**: `danceability-musicnn-msd.pb`
    - *Utility*: Distinguishes between "Lounge" (low) and "Club" (high).

## 6. The Forensic Librarian (Phase 4.5)
Moving beyond basic checks, we become a "Mastering Engineer" analyzer.

### A. Dynamic Range & Complexity
*   **Metric**: `lowlevel.dynamic_complexity` & `lowlevel.average_loudness`.
*   **Goal**: Flag "Sausage Masters" (over-compressed tracks) vs Dynamic Audiophile rips.
*   **UI**: "Dynamic Range" Badge (Low/Med/High).

### B. Spectral Forensics
*   **Metric**: `lowlevel.spectral_centroid` & `lowlevel.spectral_rolloff`.
*   **Goal**: Identify "Muddy" vs "Bright" masters. Validate 320kbps MP3s by checking if high frequencies exist or were cut off by a lower bitrate encoder earlier in the chain.

## 7. Structural Analysis (Phase 5)
Replacing fragile volume-based drop detectors with Industry-Standard Segmentation.
*   **Tool**: Essentia `Segmenter`.
*   **Output**: Intro, Verse, Chorus, Bridge, Outro boundaries.
*   **Usage**: Auto-set `CueIntro`, `CueOutro`, and `CueDrop` based on actual texture changes, not just volume spikes.

## 8. Beat Stability (Phase 6)
*   **Problem**: Soulseek vinyl rips often drift. Sync buttons fail.
*   **Solution**: `rhythm.bpm_histogram`.
*   **Metric**: `IsQuantized` (Boolean).
*   **UI Warning**: "⚠️ Tempo Drift Detected - Manual Mixing Required".

## 9. Implementation Stages
1.  **Configuration**: Deploy `essentia_profile.yaml` (Unlock Chords, BPM Hist, Dynamic Complexity).
2.  **Database**: Expand `AudioFeaturesEntity` to store the Firehose data + `IsQuantized`.
3.  **Visualization**: Build the Spectrogram UI.
4.  **AI Integration**: Link the `.pb` models and update the analyzer service.
5.  **Forensic Logic**: Implement the thresholds for "Bad Master" and "Drift".
