# Stem Cache and Preferences

**Updated:** April 20, 2026  
**Focus:** cached stem generation, model versioning, and per-track mute/solo preferences

---

## Overview

ORBIT’s stems workflow is built around two related concerns:

1. **avoiding repeated expensive separation work**
2. **remembering how the user likes to treat stems for a given track**

These are handled by two main services:

- `StemCacheService`
- `StemPreferenceService`

with `CachedStemSeparator` acting as the runtime decorator that decides whether separation work must happen again.

---

## Primary files

- `Services/Audio/StemCacheService.cs`
- `Services/Audio/StemPreferenceService.cs`
- `Services/Audio/Separation/CachedStemSeparator.cs`
- `Services/Audio/Separation/DemucsOnnxSeparator.cs`
- `Services/Audio/Separation/DemucsModelManager.cs`
- `ViewModels/Workstation/WorkstationDeckViewModel.cs`

---

## Why caching matters

Stem separation is one of the heaviest operations in the app. Re-running it every time the user opens the same track would:

- waste CPU / GPU time
- slow down the cockpit
- make Stems mode feel unreliable

The cache layer ensures that once a valid set of stems exists for the current model version, ORBIT can reuse it immediately.

---

## Stem cache responsibilities

`StemCacheService` is responsible for:

- resolving the cache folder location
- generating repeatable cache keys
- separating entries by model version / model tag
- helping remove stale outputs from older model versions

### Cache key idea
A cache entry is based on the input track and the active separation model. In practice this combines things like:

- track hash or file identity
- optional start / duration window
- stem type
- model tag

This prevents older or incompatible model outputs from being confused with newer ones.

---

## CachedStemSeparator behavior

`CachedStemSeparator` wraps the real separator implementation.

### High-level flow
1. receive separation request
2. compute cache identity for the request
3. check whether the expected stem files already exist
4. if yes, return cached output immediately
5. if not, invoke the underlying separator
6. write the resulting stems into the cache

This decorator pattern keeps the rest of the app simple. Callers ask for separation and do not need to know whether the result was freshly generated or retrieved from cache.

---

## Model versioning and stale data cleanup

The cache is intentionally version-aware.

If the active Demucs / ONNX model changes, earlier stems may no longer be valid. ORBIT handles that by associating a model tag with the cache identity and purging stale entries when needed.

### Why this is important
Without model tagging, a user could unknowingly receive stems from an older separator build and assume the current model produced them.

---

## Stem preference persistence

`StemPreferenceService` stores per-track user preferences such as:

- always-muted stems
- always-soloed stems

These preferences are persisted so the track can reopen in a familiar state instead of resetting every session.

### Typical use case
A DJ repeatedly likes a track with:

- vocals muted
- bass isolated

When that track returns to the Workstation later, the app can restore that preference profile automatically.

---

## Persistence model

Stem preferences are stored in the database as track-linked JSON fields. The service performs an upsert-style save and a simple fetch on load.

This means:

- preferences survive app restarts
- preferences are associated with the track rather than the current deck instance
- multiple sessions can reopen the same track with consistent stem state

---

## Workstation integration

The Workstation consumes the cache and preference systems in two places:

### Separation entry point
Deck and stem actions request separation through the cached separator instead of calling the raw model directly.

### Preference restore
When a track becomes active in the deck, saved mute/solo preferences can be reloaded and applied back into the stem controls.

---

## Readiness reporting

The user-facing prep summaries also look for persisted stem evidence. This is important because UI state such as `HasStems` may be lazy or refreshed later than the underlying file system.

By honoring the persisted stem folders immediately, the player and workstation can show:

- stem rack ready
- stems cached
- stem prep available

without waiting for a full re-analysis pass.

---

## Failure handling

The system is designed to degrade safely:

- missing cache entry → run separation normally
- partially missing stem set → treat as cache miss
- corrupt or old model output → purge stale entries and rebuild
- preference lookup failure → fall back to neutral stem state

---

## Caveats

Current limitations worth knowing:

- model tagging depends on the configured model identity, so unusual manual model swaps should be handled carefully
- preference state is intentionally lightweight and does not try to persist every transient mixer gesture
- cache presence is a strong readiness signal, but it does not guarantee the user wants stems active by default in every context

---

## Recommended future improvements

- in-memory preference cache for repeated deck reopening
- explicit cache inspection UI with size and last-used details
- per-track stem profile presets beyond mute/solo
- user-facing repair action for stale or broken stem cache entries

---

## Summary

The stem stack balances performance and continuity: `CachedStemSeparator` keeps expensive separation work reusable, while `StemPreferenceService` lets the Workstation reopen tracks in a way that matches the user’s creative intent.