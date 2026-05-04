# Workstation Cockpit Routing

**Updated:** April 20, 2026  
**Focus:** how tracks move from the player, search, and project workflow into the unified Workstation page

---

## Overview

ORBIT uses a single Workstation page as the creative cockpit for:

- waveform deck prep
- Flow planning
- Stems work
- export and mixdown

The routing layer is intentionally **event-driven** so the player, search, and project surfaces can hand tracks into the Workstation without tightly coupling view models together.

---

## Primary files

- `ViewModels/PlayerViewModel.cs`
- `Views/MainViewModel.cs`
- `ViewModels/Workstation/WorkstationViewModel.cs`
- `ViewModels/Workstation/WorkstationDeckViewModel.cs`
- `Models/Events.cs`
- `Views/Avalonia/WorkstationPage.axaml`

---

## Design goals

1. Keep the user in the same creative page instead of spawning extra top-level pages.
2. Allow a track to be routed directly to:
   - the focused deck
   - Deck A or Deck B
   - Flow mode
   - Stems mode
3. Preserve loose coupling via the event bus.
4. Keep routing safe even when a track is not fully prepared yet.

---

## Main routing events

### OpenStemWorkspaceRequestEvent
Used when the user wants to open the Workstation around a specific track, optionally targeting:

- a preferred deck
- the stem rack / Stems mode

This event is published from player- and track-level actions and consumed by the shell and Workstation view model.

### AddToTimelineRequestEvent
Used for **Add to Mix** and Flow-building handoff.

This event carries one or more `PlaylistTrack` instances and tells the app to open the Workstation in the creative path associated with Flow planning.

---

## Routing sources

### 1. Player surface
The player provides direct one-click commands for:

- opening the current track in Workstation
- sending the track to Deck A
- sending the track to Deck B
- opening Flow mode
- opening or preparing stems

The player computes lightweight readiness summaries before routing so the user can see whether cues, analysis, or stems are already available.

### 2. Search surface
The Search page can now batch-stage selected results into the mix workflow. Those results are converted into `PlaylistTrack` objects and then published through `AddToTimelineRequestEvent`.

### 3. Add-to-project workflow
When tracks are added to a project, the app can continue directly into Flow / Workstation instead of stopping at the selection step.

---

## Shell navigation responsibilities

`MainViewModel` acts as the shell coordinator:

- subscribes to routing events
- closes or adjusts shell panels as needed
- navigates to the Workstation page
- leaves the actual deck loading to `WorkstationViewModel`

This split is important:

- **MainViewModel** owns page navigation
- **WorkstationViewModel** owns creative state and deck behavior

---

## Workstation-side handoff behavior

Once the Workstation receives a routing event, it resolves the intent in this general order:

1. decide which mode should be active
2. resolve the preferred or focused deck
3. validate the incoming track
4. load the track into the deck if possible
5. refresh header summaries and save session state

### Mode selection
The Workstation can switch among:

- `Waveform`
- `Flow`
- `Stems`
- `Export`

Routing helpers can explicitly force Flow or Stems mode when appropriate.

### Deck targeting
The focused deck is preferred when possible. If the event asks for a specific deck, that preference wins. If neither exists, the load falls back to the first safe deck, usually Deck A.

---

## Smart routing UX

The routing layer is paired with user-facing status summaries in the header and player surface:

- deck focus summary
- active playlist flow summary
- focused deck action summary
- mix coach summary
- workstation prep summary
- routing summary

These summaries reduce ambiguity and help the user understand where the track will land before or after handoff.

---

## Safety and fallback behavior

The routing pipeline is designed to be permissive but resilient:

- if the track is not yet fully analyzed, it can still be routed
- if cues or stems are missing, the UI reports that state instead of blocking the handoff
- if the file is missing, the deck view model surfaces a load error and avoids crashing the cockpit
- if the Workstation is not currently visible, the shell still navigates into it first

---

## Persistence interaction

Routing itself is stateless, but it triggers persistent side effects through the Workstation session layer:

- deck loads update in-memory state
- session autosave runs after mode changes and track loads
- the next app start can restore the same deck and mode context

---

## Extension points

Recommended future improvements:

- explicit duplicate prevention when the same track is routed repeatedly into Flow
- richer deck targeting policies beyond A/B/focused deck
- route-history breadcrumbs in the Workstation header
- configurable rules for preferred handoff destination by context

---

## Summary

The cockpit routing system is the glue that makes ORBIT feel like one unified workspace instead of a set of disconnected pages. The player, search, and project actions all publish lightweight intent events, while the Workstation resolves those events into concrete mode and deck behavior.