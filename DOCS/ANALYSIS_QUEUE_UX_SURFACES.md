# Analysis Queue UX Surfaces

**Updated:** April 20, 2026  
**Focus:** how background analysis state is surfaced in the player and Workstation without interrupting creative flow

---

## Overview

ORBIT performs a significant amount of background preparation work:

- track structure analysis
- cue discovery
- waveform / readiness prep
- downstream feature extraction used by creative tools

To keep that work visible without becoming noisy, the app now exposes a compact analysis-lane summary in the main creative surfaces.

---

## Primary files

- `Services/AnalysisQueueService.cs`
- `Models/Events.cs`
- `ViewModels/PlayerViewModel.cs`
- `ViewModels/Workstation/WorkstationViewModel.cs`
- `Views/Avalonia/PlayerControl.axaml`
- `Views/Avalonia/WorkstationPage.axaml`

---

## Design goals

1. make background analysis feel alive
2. show whether prep is rolling or paused
3. communicate queue pressure without opening a diagnostic page
4. avoid interrupting the user’s current creative task

---

## Queue service responsibilities

`AnalysisQueueService` owns the runtime queue and worker behavior.

Its duties include:

- receiving analysis requests
- managing concurrency
- counting queued and processed items
- supporting different performance modes
- broadcasting queue state changes to the UI layer

---

## Event model

The queue publishes `AnalysisQueueStatusChangedEvent` whenever meaningful state changes occur.

The payload includes enough information for the UI to summarize:

- queued count
- processed count
- paused state
- current performance mode
- lane or worker count
- currently active track identifier

This event-driven approach keeps the queue service independent from specific UI controls.

---

## Player surface

The player displays `AnalysisLaneSummary` near the existing prep and routing summaries.

This gives the user immediate context such as:

- analysis rolling
- number of queued tracks
- number already prepared
- whether the app is in standard or stealth mode

The player is a strong place for this because it is already the user’s quick-status hub for the current track.

---

## Workstation surface

The Workstation header displays `AnalysisQueueSummary` so the user can see background preparation while actively mixing or arranging.

This is useful because:

- deck work often depends on analysis readiness
- the user can keep mixing without switching to a utility screen
- the cockpit feels connected to the prep pipeline

---

## Performance mode communication

The summary also reflects whether the queue is running in a reduced-intensity mode such as stealth / low-CPU behavior.

That matters because users may otherwise interpret slower queue progress as a bug. Showing the performance mode sets the right expectation.

---

## UX philosophy

This feature intentionally provides a **compact operational summary**, not a full job dashboard.

In other words, it answers:

- is analysis moving?
- how much is left?
- am I in a throttled mode?

without dragging the user into infrastructure detail during creative work.

---

## Current limitations

- the summary is intentionally brief and may still feel too technical for some users
- track identity is not yet consistently shown as a friendly title in every state
- no ETA is currently calculated
- there is no tier-by-tier breakdown of analysis stages in the UI

---

## Recommended future improvements

- friendlier current-track labeling instead of raw internal identifiers
- estimated completion time for queued batches
- click-through diagnostics panel for deeper queue inspection
- per-priority or per-analysis-stage counters

---

## Summary

The analysis queue UX surfaces make ORBIT’s background prep pipeline visible where it matters most: directly in the player and the Workstation. The result is better trust, better timing awareness, and less guesswork about whether the app is still preparing the library.