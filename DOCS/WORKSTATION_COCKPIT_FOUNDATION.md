# ORBIT WORKSTATION - CANONICAL FUNCTIONAL AND UI SPEC

Status: canonical source of truth
Status date: 2026-05-09
Purpose: define what Orbit Workstation is, how the cockpit must behave, and what the UI must and must not become.

This document must be read before making material Workstation UI or workflow changes.

## 1. What Orbit Is

Orbit is not Serato, Rekordbox, Traktor, VirtualDJ, or a live-DJ application.

Orbit is a DJ/DAW hybrid in the same product category as:

- DJ.Studio Pro for timeline-first DJ editing
- Mixed In Key for analysis-first harmonic intelligence
- Ableton Arrangement View for linear editing

Orbit is:

- A timeline-first DJ mix creation tool
- A DAW-style editor for transitions, stems, and automation
- A harmonic-aware and phrase-aware arranger
- A cockpit, not a page-based app
- A production tool, not a live performance tool

Orbit is not:

- A dual-deck live DJ interface
- A mixer with faders and jog wheels
- A Serato or Rekordbox clone
- A tabbed multi-page application

## 2. What Two Decks Actually Means

In Orbit and DJ.Studio Pro style workflow, two decks are engine primitives, not first-class UI elements.

- Deck 1 = currently playing track
- Deck 2 = next track

They are used internally for:

- Beat alignment
- Phrase alignment
- Harmonic compatibility
- Transition preview
- EQ curves
- Stem swaps
- Automation playback

The user should not see a two-deck UI. The user should see a timeline.

## 3. The Actual User Workflow

### Step 1 - Add Tracks

The user drags tracks into the project.

Orbit automatically analyzes:

- BPM
- Key
- Energy
- Phrasing
- Downbeats
- Cue points
- Structure such as intro, drop, breakdown, and outro
- Stems

This is Mixed In Key style behavior.

### Step 2 - Arrange Tracks On Timeline

The user drags Track B after Track A.

Orbit automatically:

- Aligns phrase boundaries
- Aligns beats
- Aligns downbeats
- Aligns energy curves
- Checks harmonic compatibility
- Proposes transition presets

This is DJ.Studio Pro style behavior.

### Step 3 - Preview Transition

Orbit internally loads:

- Track A into Deck 1
- Track B into Deck 2

Orbit applies:

- BPM sync
- Phase sync
- Transition preset
- EQ curves
- Stem swaps
- Automation curves

The user hears the transition.

### Step 4 - Edit Transition

The user edits:

- Crossfade curve
- EQ automation
- Filter sweeps
- Stem swaps
- Transition length
- Automation points
- Phrase alignment

All editing happens on the timeline, not in a deck UI.

### Step 5 - Export

Orbit renders:

- Timeline
- Transitions
- Automation
- Stems
- FX
- Mixdown

## 4. The Cockpit Layout

Orbit must use a single unified cockpit, not panels, tabs, drawers, or pages as primary workflow surfaces.

Accepted high-level layout:

```text
┌──────────────────────────────────────────────────────────────┐
│ HEADER (transport + tool toggles)                            │
├──────────────────────────────────────────────────────────────┤
│ LEFT NAV │                MAIN CANVAS (timeline)             │
│          │  - waveforms                                      │
│          │  - stems lanes                                    │
│          │  - automation lanes                               │
│          │  - transition overlays                            │
│          │  - phrase grid                                    │
│          │  - beat grid                                      │
│          │                                                   │
│          │                INSPECTOR (contextual)             │
├──────────────────────────────────────────────────────────────┤
│ BOTTOM DRAWER (track pool + suggestions + analysis)          │
└──────────────────────────────────────────────────────────────┘
```

## 5. Per-Region Feature Map

### Header

Target size: approximately 40-48px tall.

The header may contain only:

- Play and Stop
- Master BPM
- Sync
- Quantize
- Snap
- Tool toggles

Allowed tool toggles:

```text
Waveform | Flow | Stems | Automation | Samples | Export
```

Nothing else belongs in the header.

### Left Nav

Target size: approximately 64-80px wide.

The left nav may contain only:

- Dashboard
- Library
- Analysis
- Workstation
- Settings

Nothing else belongs in the left nav.

### Main Canvas

Target size: approximately 70% of the screen.

The main canvas must contain:

- Waveforms
- Stems lanes
- Automation lanes
- Transition overlays
- Phrase markers
- Beat markers
- Cue points
- Energy curve
- Track blocks
- Drag handles
- Zoom and scroll

This is the core of the app.

### Inspector

Target size: approximately 20-25% of the screen.

The inspector is contextual based on the active tool.

Waveform tool:

- BPM
- Key
- Energy
- Cue points
- Structure markers

Flow tool:

- Transition preset
- Transition length
- Harmonic compatibility
- Phrase alignment

Stems tool:

- Per-stem volume
- Per-stem mute and solo
- Per-stem FX
- Per-stem automation

Automation tool:

- Curve type
- Interpolation
- Keyframe editor

Export tool:

- Format
- Bitrate
- Normalization
- Loudness target

### Bottom Drawer

The bottom drawer may contain:

- Track list
- Harmonic suggestions
- Energy suggestions
- Phrase suggestions
- Drag-and-drop pool

## 6. What Must Not Exist In The UI

The following must not be reintroduced:

- Deck A/B/C/D UI
- Mixer faders
- Jog wheels
- Dual-deck layout
- Tabs as a primary cockpit interaction model
- Panels stacked around the timeline
- Debug text in the header
- Repeated state surfaces
- Flow Lane as a separate subsystem concept
- Flow Drawer as a separate subsystem concept
- Deck Details panel
- Workspace Tools panel
- Diagnostics panel

These are forbidden UI directions.

## 7. What The Engine Must Support

Required engine capabilities:

- Phrase detection
- Beat detection
- Harmonic analysis
- Energy curve support
- Stems separation
- Automation engine
- Transition engine
- Timeline virtualization
- GPU waveform rendering
- Cached lane rendering
- Unified workstation state model

## 8. What VS Code Must Do

Use the following block as the canonical agent instruction for cockpit work:

```text
You are the Orbit Cockpit Engineering Agent.

Before writing any UI code, you MUST understand the Orbit Cockpit Specification.

Orbit is NOT a dual-deck DJ app. It is a DJ/DAW hybrid like DJ.Studio Pro + Mixed In Key.

You MUST follow these rules:

1. Orbit uses a single cockpit layout:
   - Header (transport + tool toggles)
   - Main Canvas (timeline)
   - Inspector (contextual)
   - Bottom Drawer (track pool)
   - Left Nav (global navigation)

2. The timeline is ALWAYS visible, even empty.

3. Tools are overlays, not pages:
   Waveform | Flow | Stems | Automation | Samples | Export

4. Deck UI is forbidden. Decks are engine primitives only.

5. Flow Builder, Stems, Automation, Transitions MUST appear directly on the timeline.

6. Inspector MUST change based on active tool.

7. No repeated state surfaces. No debug text in header.

8. All UI must use DPI tokens and SVG icons.

9. All features must map to the functional workflow:
   - Add tracks
   - Arrange timeline
   - Preview transition
   - Edit transition
   - Export mix

10. All code must align with:
    DOCS/WORKSTATION_COCKPIT_FOUNDATION.md
    DOCS/WORKSTATION_COCKPIT_ISSUE_BACKLOG.md
    DOCS/WORKSTATION_COCKPIT_EPIC.md

Your job is to implement the cockpit, not reinvent the UI.
```