# Player Prep and Routing

**Updated:** April 20, 2026  
**Focus:** the now-playing sidebar as a creative launch surface for Workstation, Flow, and Stems

---

## Overview

The player is no longer just a playback widget. In ORBIT it acts as a **prep console** and a **routing launcher** for the rest of the creative workflow.

The player surface now exposes:

- track context
- readiness badges
- workstation prep summaries
- routing summaries
- transition-plan hints
- direct action buttons for analysis, stems, deck handoff, Flow, and project staging

---

## Primary files

- `ViewModels/PlayerViewModel.cs`
- `Views/Avalonia/PlayerControl.axaml`
- `Models/Events.cs`
- `Views/MainViewModel.cs`

---

## Main responsibilities

### 1. Readiness visualization
The player summarizes the current track’s creative state so the user can tell at a glance whether it is ready for deeper work.

Typical signals include:

- download / availability state
- BPM and key presence
- energy state
- cue availability
- stems readiness
- routing readiness

### 2. Direct creative actions
The player offers buttons for:

- Analyze
- Stems
- Workstation
- Flow
- Deck A
- Deck B
- Add to Mix
- Reveal / Inspect

These commands reduce the number of page hops needed for a typical DJ-prep session.

---

## Summary surfaces

### CurrentTrackWorkstationPrepSummary
Describes whether the current track is practically ready for workstation use.

Typical factors:

- analysis data available
- cue jumps ready
- stems already cached or available

### CurrentTrackRoutingSummary
Communicates whether the track is ready to be sent into:

- deck handoff
- Flow launch
- mix project staging
- stem rack path

### CurrentTrackTransitionPlanSummary
Offers a more musical prompt about how to think about the track in a mix context, especially around:

- intro
- drop
- outro
- energy anchor points

### AnalysisLaneSummary
Shows the state of the background analysis pipeline from the player’s point of view.

---

## Command-to-event relationship

The player does not own all downstream behavior directly. Instead, it often emits intent via the event bus.

Examples:

- Add to Mix publishes a flow / timeline handoff event
- Workstation open publishes a cockpit routing event
- Stems action can open the Workstation in Stems mode

This keeps the player lightweight while still feeling central to the workflow.

---

## Persisted readiness checks

One key design choice is that the player should not depend only on lazy in-memory flags.

For example, stem readiness checks also honor persisted stem folders. This means the player can tell the user a stem rack is ready even if a fresh view-model refresh has not yet rebuilt every transient flag.

That makes the UX feel much more trustworthy.

---

## UX goals

The player prep layer is designed to answer three questions quickly:

1. **What do I know about this track right now?**
2. **What creative actions are ready?**
3. **Where will this track go if I click?**

By answering those without forcing the user into several panels or dialogs, the player becomes the launchpad for the rest of ORBIT.

---

## Failure behavior

The player avoids hard-blocking most actions.

If the track is not fully prepared:

- summaries explain what is missing
- routing still remains available when safe
- downstream views handle deeper validation

This approach favors fast creative flow over rigid gating.

---

## Recommended future improvements

- richer explanation for why a track is not mix-ready
- direct genre / mood prep hints in the player card
- caching of expensive readiness checks such as repeated file-system probes
- a one-click “prepare everything” action for cue + analysis + stems

---

## Summary

The player’s prep and routing layer turns the now-playing card into a practical DJ control point. It surfaces track readiness, explains the next creative step, and launches the track directly into the correct destination without forcing the user through extra navigation.