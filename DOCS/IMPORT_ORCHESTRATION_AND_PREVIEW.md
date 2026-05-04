# Import Orchestration and Preview

**Updated:** April 20, 2026  
**Focus:** how ORBIT normalizes import requests, prevents duplicates, and streams preview results before download submission

---

## Overview

The import pipeline is designed to let users bring playlists and collections into ORBIT without immediately committing to a full download job.

The core behavior is:

1. accept an external source or playlist input
2. normalize and identify it
3. detect whether the import already exists
4. preview the incoming tracks
5. let the user accept or cancel
6. hand off the chosen items to the download system

This behavior is centralized in `ImportOrchestrator`.

---

## Primary files

- `Services/ImportOrchestrator.cs`
- `ViewModels/ImportPreviewViewModel.cs`
- `Services/DownloadManager.cs`
- provider implementations for streaming and non-streaming imports
- `Views/Avalonia/ImportPage.axaml`

---

## Why the orchestrator exists

Importing is more complex than a simple “paste URL and go” operation.

The app needs to handle:

- different source types
- provider-specific parsing rules
- duplicate detection
- preview-first UX
- cancellation
- clean transition into download jobs

Centralizing that logic keeps the rest of the UI thin and predictable.

---

## Main orchestration flow

### 1. Provider resolution
The import layer identifies which provider should handle the input, such as a playlist URL or structured source.

### 2. Input normalization
The input is normalized so equivalent forms of the same source can be recognized consistently.

### 3. Duplicate detection
The orchestrator checks for previously known imports or jobs using:

- normalized URLs
- generated identifiers
- provider-specific playlist identifiers where applicable

### 4. Preview initialization
An `ImportPreviewViewModel` is created or refreshed so the user can inspect incoming items before committing.

### 5. Streaming or non-streaming load
If the provider supports streaming preview, items appear incrementally in the UI instead of waiting for a full batch to finish.

### 6. Accept / cancel decision
If the user accepts, the chosen items are submitted to the download manager. If cancelled, the in-progress preview work is stopped cleanly.

---

## Preview-first UX

The preview stage is a deliberate product choice.

Benefits:

- the user sees what is about to be imported
- accidental large jobs are easier to catch
- duplicates can be noticed before commitment
- it creates a more trustworthy onboarding flow for external playlists

---

## Streaming provider support

Some providers can yield items incrementally. In that case ORBIT updates the preview list in real time.

This improves perceived responsiveness and makes large imports feel alive rather than blocked behind one long spinner.

---

## Download handoff

Once the user confirms the preview, the orchestrator prepares the selected items for the download layer.

At that point responsibility shifts to `DownloadManager`, which owns:

- queueing jobs
- progress tracking
- persistence of the actual job record

This separation keeps import focused on intake and validation rather than long-running download behavior.

---

## Cancellation behavior

The preview pipeline uses cancellation tokens so the user can abort an import mid-stream without crashing the page.

This is especially important for:

- large playlists
- unstable provider responses
- accidental imports from the wrong source

---

## Duplicate prevention strategy

The orchestrator attempts to prevent duplicate work before it begins.

Typical checks include:

- same normalized URL
- same generated stable identifier
- provider-specific playlist ID match

This is not only a convenience feature; it also protects the download pipeline from redundant jobs.

---

## Current limitations

- unusual or malformed provider URLs may still evade perfect normalization
- very slow upstream providers can make the preview feel stalled if they stop yielding items
- preview state is intentionally session-level, not a long-term saved import draft

---

## Recommended future improvements

- progress breakdown for streamed preview ingestion
- richer duplicate-resolution UI when an existing job is found
- resume semantics for cancelled preview sessions
- better normalization for more provider URL variants

---

## Summary

Import orchestration is ORBIT’s intake control layer. It normalizes incoming sources, prevents duplicate work, previews content safely, and only hands off to downloads once the user has confirmed the import set.