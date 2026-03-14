# Sidebar Evolution Plan: The "Docked Workspace" Roadmap

## Overview
The transition from an **Overlay Sidebar** to a **Docked Workspace** in `LibraryPage.axaml` lay the foundation for a professional, multi-panel contextual environment. This document outlines the roadmap for integrating advanced features into this workspace.

## Current State
- [x] **Docked Layout**: Sidebar is now a first-class column in the main library grid.
- [x] **Resizable Splitter**: Dynamic width control via `GridSplitter`.
- [x] **Interactive Similarity**: Match results are wrapped in `PlaylistTrackViewModel`, enabling direct play/add/context-menu actions.
- [x] **Transparency**: Detailed match breakdown tags are visible for AI transparency.

## Planned Expansion Panels

### 1. Cue & Phrase Inspection Panel
**Objective**: Real-time analysis and adjustment of auto-generated cue points and structural phrases.
- **Features**:
  - Vertical Waveform preview (Zoomable).
  - List of detected cues (Intro, Drop, Break, Outro).
  - Manual adjustment toggles.
  - "Apply to Serato/Rekordbox" export buttons.
- **ViewModel**: `ForensicSidebarViewModel` (to be expanded or new `CueSidebarViewModel`).

### 2. STEMS Manipulation Panel
**Objective**: Interactive stem separation and preview directly from the library sidebar.
- **Features**:
  - Four sliders (Drums, Bass, Vocals, Other).
  - Solo/Mute buttons per stem.
  - "Export Stems" workflow (triggers background separation).
  - Integration with `StemSeparationService`.
- **ViewModel**: `StemSidebarViewModel`.

### 3. Vibe Engineer (Genre/Style Creation)
**Objective**: Interactive style classification and training integration.
- [x] **Features**:
  - [x] Vibe Radar (Energy vs. Valence).
  - [ ] Sub-genre confidence gauges.
  - [ ] "Tag as [Genre]" rapid tagging buttons.
  - [ ] AI "Why?" breakdown (similar to Sonic Match transparency).
- [x] **ViewModel**: `VibeSidebarViewModel` (integrating `StyleLabViewModel` logic).

## Technical Implementation Strategy

### ViewModel Discovery & Activation
- Sidebars should continue to implement `ISidebarContent`.
- The `ContextualSidebarViewModel` will orchestrate the switch based on:
  - User selection (Tabs/Icons).
  - Task context (e.g., clicking "Analyze Stems" automatically opens the Stem panel).

### Multi-Track Focus (Bulk Mode)
- Enhance the sidebar to handle multiple selected tracks.
- **Similarity**: Show "Common Matches" or "Blend Potential".
- **Bulk Tags**: existing `BulkActionSidebarView` will be refined to match the new aesthetics.

## UI/UX Refinements
- **Iconic Navigation**: A vertical icon strip on the far right of the sidebar to switch between modes (Metadata, Similarity, Forensics, Stems, Cues).
- **Persistence**: Save `SidebarWidth` and `LastActivePanel` in `AppConfig`.
- **Aesthetics**: Glassmorphism effects for the sidebar background to maintain premium feel.
