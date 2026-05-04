# Track Compatibility Scoring

**Updated:** April 20, 2026  
**Focus:** how ORBIT scores transition safety, match quality, and playlist flow compatibility

---

## Overview

ORBIT does not treat track compatibility as a single number from one source. Instead, the system blends several signals into a composite score that can support:

- similar-track recommendations
- transition safety hints
- double-drop detection
- Flow and playlist optimization

The main building blocks are:

- `TrackMatchScorer`
- `SectionVectorService`
- `PlaylistOptimizer`
- section-aware feature vectors and phrase data

---

## Primary files

- `Services/Similarity/TrackMatchScorer.cs`
- `Services/Similarity/SectionVectorService.cs`
- `Services/Playlist/PlaylistOptimizer.cs`
- `Models/SectionFeatureVector.cs`
- `Models/TrackMatchScore.cs`

---

## Why multiple dimensions matter

Two tracks can look compatible in one way and fail badly in another.

Examples:

- same BPM but clashing harmonic centers
- compatible key but very different drop energy
- strong embedding similarity but awkward outro → intro handoff

The scoring system therefore separates multiple dimensions before combining them.

---

## Core score dimensions

### 1. Harmony score
Uses Camelot-style distance between keys.

Purpose:
- reward close key relationships
- penalize large harmonic jumps
- support more confident transitions and mashups

### 2. Beat score
Evaluates BPM closeness and timing compatibility.

Purpose:
- favor tracks that can be aligned naturally
- account for small tempo offsets better than raw equality checks
- help identify viable double-time and half-time relationships

### 3. Sound / embedding score
Uses audio embeddings or a similar sonic likeness metric when available.

Purpose:
- compare timbre and overall sonic character
- surface tracks that feel related even if metadata alone misses the connection

### 4. Drop-sonic score
Compares the tracks’ high-impact sections, especially drop behavior.

Purpose:
- identify stronger performance transitions
- expose potential double-drop opportunities
- avoid transitions where the energy shape is mismatched

### 5. Outro → intro transition score
Uses section vectors to estimate whether one track’s ending flows naturally into another track’s opening.

Purpose:
- improve sequencing quality in Flow and playlist optimization
- reduce jarring energy or spectral handoff changes

---

## SectionVectorService role

`SectionVectorService` is the structure-aware part of the system.

It loads phrase / section information such as:

- intro
- build
- drop
- breakdown
- outro

and turns those into comparable vectors.

These vectors let ORBIT reason about **how a track behaves over time**, not just what its single global BPM or key is.

---

## PlaylistOptimizer integration

The playlist optimizer uses compatibility scoring inside a greedy sequencing process.

### High-level behavior
1. prepare candidate tracks
2. preload section data for the set
3. build transition costs between tracks
4. prefer the next track with the lowest effective transition cost
5. respect BPM, key, energy, and section-aware compatibility together

This produces more musically coherent orderings than simple sorting by BPM or key alone.

---

## Double-drop detection

The compatibility system can also flag especially strong simultaneous-impact matches.

This is based on tighter thresholds than general compatibility:

- stronger beat alignment
- stronger harmonic agreement
- stronger drop profile similarity

This score is intentionally stricter because a double drop is more demanding than a standard blend.

---

## User-facing outputs

The scoring system feeds several visible surfaces:

- similar-track panels
- match percentages
- harmony / beat / drop badges
- “double drop ready” style hints
- Flow recommendations
- workstation coaching summaries

These outputs are simplified for the UI, but they originate from the deeper multi-factor score model.

---

## Caching and performance

Section-aware scoring can be expensive if every comparison re-reads database state.

To reduce that cost:

- section vectors are cached in memory
- transition costs can be reused during optimizer passes
- callers can preload data for a set before scoring many edges

This is important for `O(n²)` style comparison work in larger candidate sets.

---

## Limits of the current model

- thresholds are still mostly global rather than genre-specific
- some fallbacks rely on more generic similarity when phrase data is incomplete
- the system assumes phrase data quality is good enough to trust for downstream scoring
- user-configurable scoring profiles are still limited

---

## Recommended future improvements

- genre-aware BPM tolerance bands
- user-selectable scoring emphasis presets
- more transparent explanation strings for each match dimension
- explicit confidence reporting when phrase data quality is weak

---

## Summary

Track compatibility scoring is the musical intelligence layer that helps ORBIT recommend safer and more expressive transitions. It blends harmony, beat, sound, drop behavior, and structural handoff data into one practical model for both search results and creative sequencing.