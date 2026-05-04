# Workstation Session Persistence

**Updated:** April 20, 2026  
**Focus:** autosave, restore, and crash-safe recovery of the unified Workstation state

---

## Overview

The Workstation is a stateful creative surface. Users expect deck loads, mode selection, and timeline context to survive:

- normal app close
- restart
- unexpected crash
- short interruptions while switching tasks

ORBIT handles that through `WorkstationSessionService` and session-aware logic in `WorkstationViewModel`.

---

## Primary files

- `Services/WorkstationSessionService.cs`
- `ViewModels/Workstation/WorkstationViewModel.cs`
- `Models/Stem/WorkstationSession.cs`
- `Models/Stem/WorkstationDeckState.cs`

---

## What gets persisted

A workstation session captures the core creative context, including:

- active workstation mode
- timeline offset and zoom window
- per-deck track identity
- selected deck labels
- playback position / transport context where applicable
- enough metadata to restore the deck in a meaningful way

The goal is not to capture every transient frame of UI activity, but to recover the user’s working state with minimal friction.

---

## Storage location

The session is stored as JSON under the user application-data path.

This makes it:

- local to the user account
- easy to restore at next startup
- independent of the project repository itself

---

## Save strategy

The Workstation autosaves opportunistically when the creative state changes.

### Common save triggers
- a track is loaded into a deck
- the active mode changes between Waveform, Flow, Stems, and Export
- important deck context is updated in a way that should survive a restart

### Philosophy
Save frequently enough to preserve work, but keep the save format lightweight so the operation remains cheap.

---

## Atomic write behavior

The session writer uses a temp-file swap pattern rather than overwriting the main file directly.

### Why this matters
If the app crashes or the machine loses power mid-save, the previous good session file remains intact.

### Practical benefit
This greatly reduces the chance of waking up to a corrupted cockpit state after a failure.

---

## Restore strategy

On startup or page re-entry, the Workstation can ask the session service for the last saved snapshot.

### Restore flow
1. load JSON from disk
2. deserialize into a `WorkstationSession`
3. validate the payload
4. restore mode and viewport settings
5. attempt to load each saved deck track
6. skip anything that is missing or invalid

The restore path is intentionally fault-tolerant: a partially valid session is still better than failing the whole page.

---

## Interaction with track and cue recovery

The session layer restores **references and layout context**, while track-specific services restore **content details**.

For example:

- Workstation session says which track belonged in a deck
- cue services rehydrate cue data for that track
- stem preference services reload per-track mute/solo intent

This separation keeps the session file compact and avoids duplicating database-backed state.

---

## Failure and corruption handling

The session system is intentionally conservative:

- bad or unreadable JSON is ignored safely
- missing file paths are skipped
- the user can still open a clean Workstation even if restoration fails

This prevents session restore from becoming a single point of failure for the creative page.

---

## Current limitations

- one last-known session rather than a full session history
- no explicit version-migration UI for old session formats
- no built-in diff view showing what changed since the previous save
- no manual save slot system yet

---

## Recommended future improvements

- named session snapshots
- restore prompts after crash vs. clean shutdown
- per-project workstation memories instead of one global snapshot
- optional session history for undoable startup restore

---

## Summary

Workstation session persistence gives ORBIT its “resume where I left off” behavior. The design is simple but robust: frequent autosave, atomic writes, tolerant restore, and clean separation from track-specific metadata services.