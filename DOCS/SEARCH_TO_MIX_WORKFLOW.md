# Search to Mix Workflow

**Updated:** April 20, 2026  
**Focus:** how Soulseek search results move from discovery into ORBIT’s mix and project pipeline

---

## Overview

The Search page is no longer only a download surface. It now supports a direct **Search → Add to Mix** handoff so a user can move promising tracks into the creative workflow with fewer steps.

This bridges three stages:

1. discover candidate tracks
2. select one or more results
3. push them into the project / Flow / Workstation path

---

## Primary files

- `ViewModels/SearchViewModel.cs`
- `Views/Avalonia/SearchPage.axaml`
- `Models/Events.cs`
- `Views/MainViewModel.cs`
- `ViewModels/Workstation/WorkstationViewModel.cs`

---

## User flow

### Step 1: search
The user searches for tracks from the acquisition surface.

### Step 2: multi-select
One or more results are selected from the result grid.

### Step 3: batch action
The user triggers **Add to Mix** from:

- the batch action button
- the context menu

### Step 4: handoff
The selection is converted into `PlaylistTrack` objects and published into the creative pipeline.

---

## View-model responsibilities

`SearchViewModel` owns the interaction layer for this flow.

Key responsibilities include:

- tracking `SelectedResults`
- exposing `HasSelectedResults`
- generating `SelectedResultsSummary`
- enabling `AddToPlaylistCommand`
- converting search hits into playlist-compatible track models

This lets the UI remain declarative while the workflow logic stays in one place.

---

## Conversion step

Search results and playlist-ready tracks are not identical models. Before a result can enter the mix workflow, the view model maps the search hit into a `PlaylistTrack`.

This includes carrying across useful metadata such as:

- title
- artist
- album when available
- duration when known
- source path or identifier

The goal is to make the track immediately useful to downstream playlist and Workstation logic.

---

## Event-driven handoff

After conversion, the view model publishes an event rather than directly manipulating the Workstation.

This is important because it:

- keeps Search decoupled from the creative page
- allows the shell to decide how to navigate
- lets the same event support both single-track and multi-track launch cases

The central event for this path is the same timeline / flow handoff used elsewhere in the app.

---

## UX feedback

The page now communicates selection and readiness through:

- an enabled / disabled Add to Mix button
- a selected-results summary label
- a matching context menu action

This makes the workflow discoverable without requiring extra dialogs.

---

## Relationship to download workflow

Search-to-Mix does not replace the download pipeline. Instead, it complements it.

Depending on the state of the selected items:

- some tracks may already be available locally and can move forward quickly
- some tracks may still require download or later prep
- downstream readiness checks decide how far they can go immediately

This keeps the feature flexible while still preserving validation where it matters.

---

## Failure and edge cases

Current caveats include:

- duplicate staging is still possible if the same search result is added repeatedly
- results can be routed before full analysis exists
- if the user navigates away, selection is not intended to be a persistent saved state

These are acceptable tradeoffs for now because the goal is fast discovery-to-creative flow.

---

## Recommended future improvements

- duplicate protection during batch stage
- stronger indication of which selected tracks are fully ready vs. pending download
- queue-based “stage now, prep later” feedback
- optional immediate routing to a chosen deck instead of generic Flow handoff

---

## Summary

The Search-to-Mix workflow shortens the distance between discovery and creativity. It lets a user select likely candidates in Search and push them straight into ORBIT’s mix-oriented tooling without manually rebuilding the selection elsewhere.