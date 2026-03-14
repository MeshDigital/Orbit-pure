# AI-DAW Evolution & Pipeline Architecture (The "MIK Killer")

This document defines the transition of **QMUSICSLSK** from a Soulseek player into a high-tier **AI-DAW**, centered around a **Workflow-Centric Navigation** pipeline.

---

## 1. The Core Menu Overhaul: The "Pipeline"

The navigation will be restructured to guide the user through a linear sequence from discovery to a polished set.

### 🌐 Discovery Hub (Steps 1–4) [x]
*   **Spotify Connect**: Authorize via PKCE to pull "My Playlists" or "Discover Weekly." [x]
*   **Crate Digger**: A unified scratchpad for pasting links (YouTube, SoundCloud) or raw text tracklists. [/]
*   **Auto-Cleaner**: A logic layer that normalize queries (stripping "feat. Artist," "Original Mix," etc.). [x]

### 📥 Ingestion & Enrichment (Steps 5–6) [/]
*   **The In-Box (formerly Projects)**: A staging area for tracks "Wanted but not Owned." [x]
*   **The Auditor**: Background worker that cross-references Cloud data with **MusicBrainz** (ISRC/Art) and **Soulseek** (Quality lookup). [x]
*   **Match Confidence**: Visual traffic-light (Green/Yellow/Red) scoring for search accuracy. [x]

### 🧠 AI Lab (Step 7) [/]
*   **The Processor (formerly Analysis Queue)**: Once offline, tracks hit this engine for **Essentia 128D Analysis**. [x]
*   **Features**: Beatgrid alignment, Camelot Key detection, and Structural segmentation (Intro/Outro). [/]

### 🎧 Set Designer (The DAW) [/]
*   **Timeline View (Phase 4)**: A multi-track view for testing transitions and placing "Smart Cues" based on AI-detected drops/breaks. [x]
*   **Mashup Lab (Phase 5)**: A dual-deck "A/B" environment for rapid mashup testing, key shifting, and one-click stem isolation. [ ]
*   **Flow Builder (Phase 6)**: A list-driven curation assistant for sculpting sets based on harmonic flow and energy curves. [ ]

---

## 2. Expanded User Workflow: The "Prepped Set" Journey

### Phase A: Semantic Sync & Ingestion
1.  **Logon**: Authorize Spotify PKCE.
2.  **Mapping**: View Spotify Playlists; click "Prep for Set" to create a "Ghost Library" in **The In-Box**.
3.  **Enrichment**: The app pulls ISRC and High-Res Art from MusicBrainz while the user browses.

### Phase B: AI "Deep Dive" & Discovery
4.  **Offline Analysis**: Essentia scans the downloaded file.
5.  **Sonic Matching**: Suggest similarity-based additions from the existing library.
6.  **Auto-Cueing**: AI identifies the First Beat, The Drop, and The Outro.

### Phase C: Harmonic DAW Prep
7.  **Key-Space View**: A **Camelot Wheel** overlay plotting playlist tracks by harmonic compatibility.
8.  **Energy Profiling**: Visualizing the set's "Energy Map" to avoid plateauing.
9.  **Mashup Lab**: Loading tracks into Slot A/B; Orbit surfacing **Blend Candidates** and offering **Key Shift Intelligence**.
10. **Flow Builder**: List-driven "Flow Suggestions" suggesting the smoothest transition points and structural alignment.
11. **Export**: Seamless export to **Rekordbox** or **Serato** with pre-set Hot Cues and metadata.

---

## 3. UI/UX "Mixed In Key" Style Refinements

| Old Component | New "Pro DJ" Replacement | Purpose |
| --- | --- | --- |
| **Project View** | **The Crate Staging** | A temporary home for tracks being enriched. |
| **Settings** | **AI Preferences** | Adjust "Smart Cues" and "Key Detection" sensitivity. |
| **Track List** | **The Matchmaker** | Split-view showing a track and its AI-matched neighbors. |
| **Waveform** | **The Semantic Timeline** | Color-coded regions (Vocal vs. Instrumental vs. Drop). |
| **Set Designer** | **Mashup Lab** | A/B testing environment with one-click stems & key shifting. |
| **Playlist View** | **Flow Builder** | Recommendation-driven curation list with transition predicting. |

---

## 4. Orbit vs. Mixed In Key (The Competitive Edge)

Orbit is built on **Forensic Transparency**. Unlike MIK's "Black Box," Orbit provides proof.

### Orbit-Exclusive Upgrades:
- **Forensic Timeline**: Every suggestion comes with *alignment proof* — why it works, where it matches.
- **Cue Intelligence**: Auto-suggest cues based on *structural phrase matching*, not just peaks.
- **Track-Scoped Logging**: Every mashup test and transition trial is logged for revisit.
- **Waveform-Aware Matching**: Matching specific musical moments (e.g., "Outro tail matches Intro lead").

---

## 4. Technical Strategy

### A. Core DAW Engine
*   **Sample-Accurate Clock**: Transition from `DispatcherTimer` to a sample-counting audio callback.
*   **Multi-Track Mixer**: Implement a summing bus for the Set Designer timeline.

### B. Asynchronous Logic
*   **Background Enrichment**: `ImportOrchestrator` must run asynchronously to unblock browsing.
*   **Analysis Queue**: Visual progress for AI "Vector Crunching" in the bottom sidebar.

### C. Libraries
*   **Audio**: NAudio (Current) -> Potential migration to portaudio for low-latency.
*   **Analysis**: Essentia (The Core).
*   **UI**: Avalonia + Skia for GPU-accelerated waveform rendering.

