# Memory Plan - Workstation Cockpit Redesign & UI/UX Overhaul

> Status: Historical redesign blueprint (core slices completed)
>
> Last reviewed: 2026-05-26
>
> See also: [workstation_overhaul_completed_work.md](workstation_overhaul_completed_work.md)

This document establishes the audit, logic cleanup, and layout overhaul strategy to transform the ORBIT Workstation into a high-density, professional-grade timeline cockpit. The goal is to maximize vertical screen space, eliminate redundant controls, and focus user interaction through a single Contextual Inspector, matching the workflows of **DJ.Studio Pro** and **Mixed In Key**.

---

## 1. Visual Clutter & Redundancy Audit

Our deep dive into `WorkstationPage.axaml`, `WorkstationDeckRow.axaml`, and `MixerCenter.axaml` revealed massive control duplication and screen space inefficiencies:

```text
┌────────────────────────────────────────────────────────────────────────┐
│ DUPLICATED CONTROL MATRIX                                              │
├──────────────────┬──────────────┬──────────────┬──────────────┬────────┤
│ Feature          │ Global Header│ Mixer Center │ Deck Row     │ Lanes  │
├──────────────────┼──────────────┼──────────────┼──────────────┼────────┤
│ Play / Pause     │ Yes          │ Yes          │ Yes (per deck)│ No     │
│ Stop / Cue       │ Yes          │ Yes          │ Yes (per deck)│ No     │
│ Snap / Qtz / Met │ No           │ Yes          │ No           │ Yes    │
│ Zoom & Pan       │ Yes          │ No           │ No           │ Yes    │
│ Tap / BPM        │ Yes          │ Yes          │ No           │ No     │
│ Stems / Toggles  │ No           │ No           │ Yes (per deck)│ Yes    │
│ Cues & Loop Size │ No           │ Yes          │ Yes (per deck)│ No     │
└──────────────────┴──────────────┴──────────────┴──────────────┴────────┘
```

### Critical Layout Issues:
1. **Vertical Height Bloat per Deck**:
   - The current `WorkstationDeckRow.axaml` is a massive vertical block (120px default, expanding to 250px+ with Stems or Sections dropdowns).
   - Placing individual Play, Cue, Loop size buttons, Gain, VU meters, Pitch faders, Lock buttons, Key adjustment rows, Stems expanders, and Hot Cue pads on **every track row** means adding 2 tracks fills the screen, forcing constant scrolling.
2. **Scattered Transport Controls**:
   - Play/Pause/Stop and Tap Tempo are in the Header AND in the `MixerCenter`.
   - Snap, Quantize, and Metronome are in the `MixerCenter` AND in the Automation lane.
3. **Lack of Inspector Context**:
   - The right-hand inspector already exists, but because controls aren't removed from the timeline rows, it serves as a duplicate display panel instead of a single source of control truth.
4. **Poor Visual Hierarchy**:
   - The timeline deck rows are dominated by faders and buttons rather than the track waveform, which should be the visual center of gravity.

---

## 2. Redesign Blueprint (The "Cockpit Overhaul")

### Concept A: High-Density Waveform Timeline
* **Minimize Deck Rows (64px max height)**:
  - Strip all buttons, sliders, and expanders from `WorkstationDeckRow.axaml`.
  - Reduce the left column width from 290px to a compact 200px.
  - The deck row left panel will only contain:
    - Small Deck Label (e.g. `A` / `B` in a mini colored badge).
    - Track Title & Artist (small, high-contrast, clean layout).
    - Compact BPM & Key badge (e.g., `124 BPM · 8A`).
    - Tiny Lock status icon.
  - The right column will contain *only* the `WaveformControl`, making the visual timeline clean and expansive.
  - Benefit: Up to 5–6 tracks can now fit on screen simultaneously, providing a true bird's-eye view of the linear mix.

### Concept B: Contextual Inspector as the Single Source of Truth
* When a user selects or focuses a track row on the timeline, the **Right Inspector** becomes the **Track Inspector** and handles all control tasks:
  - **Volume & Pitch**: Gain slider, VU meter, Pitch/Tempo fader, Key Lock, and Semitone shift buttons.
  - **Looping**: Cue, Play/Pause, Loop size pads (1, 2, 4, 8, Exit), and Loop Shift.
  - **Stems separation**: Vocal, Drum, Bass, and Other volume toggles/sliders + "Separate Stems" button.
  - **Hot Cues**: CDJ-style grid of 8 color-coded cue pads.
  - **Sections**: Quick-jump list (Intro, Build, Drop, Outro).
* When a transition overlay is selected -> Inspector switches to **Transition Inspector** (compatibility score, Top Preset, presets list, cycles, length).
* When the Automation tool is selected -> Inspector switches to **Automation Inspector** (envelope parameters, snapping).
* When the Export tool is selected -> Inspector switches to **Export Inspector** (file format, output path, target volumes, export trigger).

### Concept C: Centralized Global Header
* Keep the header compact (44px) and make it the **only** place for global operations:
  - Play / Pause / Stop (controlling the master playhead).
  - Master BPM text input + Tap Tempo button.
  - Master Snap, Quantize, and Metronome toggles.
  - Active tool selectors.
* Remove the `MixerCenter` completely, or reduce it to a simple horizontal crossfader bar at the bottom of the canvas, since all other controls are moved to the Header and Inspector.

---

## 3. Phased Implementation Roadmap

To overhaul the workstation environment without introducing regression bugs, we will proceed in four carefully paced, build-gated slices:

### Slice 20: Header Transport & Grid Consolidation
* **Target**: Consolidate global transport and snap settings in the header.
* **Execution**:
  - Add `IsSnapEnabled`, `IsQuantizeEnabled`, and `IsMetronomeEnabled` controls directly into the global header transport cluster.
  - Move the Master BPM input and Tap buttons into the header, replacing duplicates.
  - Remove duplicate snap, quantize, metronome, transport, and tap tempo controls from `MixerCenter.axaml` and all Active Lanes.

### Slice 21: The Contextual Track Inspector
* **Target**: Move all track-level controls from the deck row to the right-hand inspector.
* **Execution**:
  - Build `TrackInspectorPanel.axaml` and wire it to display when a deck row is selected.
  - Move the following sections from the deck row to this inspector panel:
    - Looping controls (PLAY, CUE, LOOP, HALF, DOUBLE, move loop left/right, exit).
    - Stems separation rack (Vocals/Drums/Bass/Other toggles, SEPARATE button).
    - Section jumps (INTRO, BUILD, DROP, OUTRO, VIEW).
    - Gain slider, VU progress bar, Pitch fader, key lock, and key shift semitone adjusters.
    - CDJ-style Hot Cue pads grid (Pads 1-8, MARK, AUTO).

### Slice 22: Extreme Deck Row Condensation
* **Target**: Shrink the timeline deck rows and focus on high-density waveforms.
* **Execution**:
  - Remove all buttons, sliders, expanders, and detail labels from `WorkstationDeckRow.axaml`.
  - Re-align the row layout to `ColumnDefinitions="200,*"` with a maximum height of 64px.
  - Keep the left panel minimal (Title, Artist, BPM, Key, Lock icon, active indicator).
  - Make the right panel full-width for `WaveformControl`.
  - Remove `MixerCenter` completely and move the crossfader slider to the bottom of the timeline or inside the inspector.

### Slice 23: Navigation, Collapsibility, & Aesthetics Polish
* **Target**: Polish visual details, layout scale, and drawer collapsibility.
* **Execution**:
  - Implement full drawer collapsibility: clicking "Toggle Flow Drawer" reduces the drawer to a minimal 32px status bar, collapsing the grid row dynamically.
  - Apply clean cockpit theme styling (mint accents, soft glass borders, no layout margins, Inter typography).
  - Update all command bindings and validation tests to ensure full test coverage remains green.

---

## 4. Verification & QA Plan

### Unit and Architecture Tests:
* Update `WorkstationTimelineLayoutGuardTests` to assert:
  - Deck row height is restricted to <= 64px.
  - Redundant buttons (e.g. Loop pads, Stems controls, Pitch faders) no longer exist in the Deck row hierarchy.
  - Inspector contains the correct bindings for selected deck controls.
* Update VM tests to verify:
  - Focus switching correctly populates the Track Inspector properties.
  - Key lock, pitch, gain, and loop commands run correctly from the inspector scope.

### Manual Verification Matrix:
- [ ] Verify 4-6 track rows fit on a 1080p screen without scrolling.
- [ ] Select Deck A: Verify right inspector populates with Deck A's gain, pitch, stems, and hot cues.
- [ ] Adjust Gain on Deck A in the inspector: Verify audio level and VU meter react.
- [ ] Toggle Flow Drawer: Verify the drawer collapses to a thin bar and the timeline height increases.
