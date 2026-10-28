# Analysis coverage and per-engine runs — handover

Branch: `analysis-engines-2026-10-28`, commit `0a25c14`, built on `origin/master` (`8ed8403`).
Worktree used: `C:\Users\quint\OneDrive\Documenten\GitHub\ORBIT-analysis` (the main checkout was
left untouched because `cue-lab-waveform-polish-2026-10-28` is in progress there).

This document is for review. It states what changed, why, how to verify it, where it overlaps the
cue-lab branch, and what is still open. Nothing here has been run against a real UI session —
see "Not verified".

---

## 1. Why

The Analysis page could not report an incomplete track. `AnalysisPageViewModel.MapLibraryEntryToTrack`
built an `AnalysisData` for every library row, inventing `TonalProbability = 0.55`, moods derived
from energy/valence, a synthetic 6-point energy curve and an 8-point phrase shape. `HasAnalysis`
was `AnalysisData is not null`, so:

- every library row showed the ✓ and status `Completed`;
- the header always read "M of M tracks analysed";
- `PendingTrackCount` was always 0;
- `GetIncompleteAnalysisReasons()` could never return "analysis missing";
- the genre `"Analyzed" @ 0.75` fallback rendered as a real genre bar.

Four different completeness definitions existed and disagreed:

| Where | Rule |
|---|---|
| `AnalysisPageViewModel.GetIncompleteAnalysisReasons` | failed · no AnalysisData · BPM≤0 · key blank · cues≤0 · no waveform |
| `DashboardService.GetIncompleteAnalysisTrackCountAsync` | hasBpm && hasKey && hasCues && hasWaveform |
| `PlaylistTrackViewModel.NeedsAnalysis` | **OR** of bpm, key, cues, genre, sample rate, energy curve — a row with only a sample rate counts as analysed |
| `WorkstationDeckViewModel.GetTrackEligibilityIssue` | waveform / cues / analysis |

Separately, the pipeline was all-or-nothing: one 394-line `AnalyzeFileAsync`, one "Start analysis"
button, and a partial failure could only be fixed by redoing every step.

## 2. What changed

### New files

| File | Purpose |
|---|---|
| `Models/AnalysisEngine.cs` | `[Flags]` engine enum, `AnalysisEngineInfo` catalog (name / purpose / produces / cost), `TrackAnalysisCoverage` (present, missing, runnable-missing), `Has`/`HasAny`/`Without` helpers. |
| `Services/AudioAnalysis/AnalysisCoverageAnalyzer.cs` | Pure predicates: which engines have output for one `AudioFeaturesEntity`. Plus `Summarize` (per-engine counts) and `MissingUnion` (the mask for "fill what's missing"). |
| `Services/AudioAnalysis/AnalysisCoverageService.cs` | Per-playlist (or whole-library) coverage: playlist tracks → features rows + cue counts + fingerprint files, matched by hash, offline-only. Returns `PlaylistAnalysisCoverage` with per-engine rows and per-track `TrackCoverageEntry`. |
| `ViewModels/AnalysisCoverageRows.cs` | `PlaylistChoice` (picker entry) and `EngineCoverageRow` (one row of the coverage table). |
| `Tests/SLSKDONET.Tests/Analysis/AnalysisCoverageAnalyzerTests.cs` | 18 tests: predicates, constructor defaults, cue rows, fingerprint file, summarisation, offline filter, mask helpers. |

### Changed files

| File | Change |
|---|---|
| `Events/AnalysisEvents.cs` | `TrackAnalysisRequestedEvent` gained `AnalysisEngine Engines = AnalysisEngine.Pipeline`. |
| `Services/IAudioAnalysisService.cs` | `AnalyzeFileAsync(..., AnalysisEngine engines = AnalysisEngine.Pipeline, CancellationToken)` — inserted **before** `cancellationToken`, all existing call sites use named arguments. |
| `Services/AudioAnalysis/AudioAnalysisService.cs` | Returns early on `engines == None`; each step gated (see §3). 47 lines. |
| `Services/AnalysisQueueService.cs` | Passes `evt.Engines` through; runs the structure job only when `Structure` or `Cues` was requested. 11 lines. |
| `App.axaml.cs` | One `AddSingleton<AnalysisCoverageService>()`. 3 lines. |
| `ViewModels/AnalysisPageViewModel.cs` | Playlist picker + coverage state + four commands; `AnalysisTrackItem` gained `Coverage` and the row-level missing properties; `HasAnalysis` now prefers measured coverage over `AnalysisData`. 258 lines. |
| `Views/Avalonia/AnalysisPage.axaml` | Coverage card (picker, headline, per-engine rows with Run) and row-level "Missing …" line + run-missing button. 85 lines. |

## 3. Engines and what proves each one

`AnalysisEngine.Pipeline` = everything except stems. A track is "fully analysed" when all of these
are present.

| Engine | Output | Predicate | Gated at (`AudioAnalysisService.cs`) |
|---|---|---|---|
| Waveform | `WaveformBlob`, `WaveformBlobSampleCount` | blob non-empty **or** count > 0 | :161 |
| Loudness | `LoudnessLUFS` | `!= 0` (0 is the not-measured sentinel; −100 silent marker counts) | :198 |
| Tempo & grid | `Bpm`, `BeatGridJson`, downbeat | `Bpm > 0` **and** non-empty grid | :333–346, :448, :454 |
| Key | `Key`, `CamelotKey`, `KeyConfidence` | Camelot non-blank, or key non-blank with confidence > 0 | :344 |
| Mood & genre | `EnergyScore`, `GenreDistributionJson` | EnergyScore > 0 **or** real genre distribution | :284 (ONNX), :440 (genre) |
| Structure | `PhraseSegmentsJson`, `EnergyCurveJson`, `DropTimeSeconds` | any of the three non-default | :219 (curve), :461 (drop/cue detection) |
| Cues | `CuePoints` table rows | row count > 0, or legacy JSON | structure job (queue) |
| Fingerprint | `%APPDATA%\ORBIT\Fingerprints\…json` | file exists | :476 |
| Stems | stem cache files | **not** in the default mask — separate on-demand job | n/a |

Two decisions worth checking:

- **Valence/arousal are not evidence.** The entity constructor sets both to `0.5f`, which is also a
  legitimate measured value, so mood is proved by `EnergyScore` or `GenreDistributionJson` only.
- **Cue coverage counts rows, not JSON.** The live pipeline persists `CuePoints` rows; the JSON
  columns are legacy. Counting only JSON would report every fully-cued track as missing cues.

## 4. Coverage semantics

- Only **offline-available** tracks are measured: `Status == Downloaded`, or availability
  `Ready`/`LocalUnanalyzed`. Missing tracks would otherwise make a correctly-filled playlist look
  permanently incomplete.
- Rows are matched **by hash**, never by position — the library list and a playlist are different
  populations in different orders.
- Stems are reported by the catalog but excluded from the default mask, so "fully analysed" does
  not silently depend on a heavy optional job.

## 5. UI

Coverage card, in the capped scroll strip above the two panes:

- playlist picker ("Whole library" first, then playlists, newest first);
- headline "N of M tracks fully analysed", breakdown "x partially analysed · y not analysed at all";
- one row per engine: name, purpose, `118 / 140`, "22 missing", and a **Run** button;
- **Fill missing** queues the union of what the selection lacks.

Library rows: a third line "Missing Key, Cues and Fingerprint" (only when measured) and a run-missing
button next to the existing add-to-queue button.

Queueing reuses the existing path: `TrackAnalysisRequestedEvent` with the mask, the row is added to
`AnalysisQueue` and marked queued, so progress and completion behave as before.

## 6. Verify

```
cd C:\Users\quint\OneDrive\Documenten\GitHub\ORBIT-analysis
dotnet build SLSKDONET.csproj -c Debug
dotnet test Tests\SLSKDONET.Tests\SLSKDONET.Tests.csproj --filter "FullyQualifiedName~AnalysisCoverageAnalyzerTests"
dotnet test Tests\SLSKDONET.Tests\SLSKDONET.Tests.csproj --filter "FullyQualifiedName~Architecture"
```

Results at commit time: build 0 errors; 18/18 coverage tests pass; Architecture green.
Full suite: 2422 passed, 5 failed — all `CueDetrParityTests`, because
`Tests/SLSKDONET.Tests/TestData/CueDetr/meta.json` is untracked in the main checkout and therefore
absent from a fresh worktree. Unrelated to this change; it will pass in the main checkout.

## 7. Not verified

- **The page has not been opened.** XAML compiles and the ViewModel builds, but no visual check of
  the coverage card, the playlist picker or the row layout was done. Worth a look at 1920×1080 and
  with the compact (single-pane) layout, where the card sits above the panes.
- **No real end-to-end run.** Partial runs were not exercised against actual audio; the gating is
  verified by reading and by build, not by watching a waveform-only re-run finish.
- `Classes="caption warning"` on the row's missing line assumes a `warning` text class exists in the
  design tokens; if it does not, the text still renders, just without the colour.

## 8. Merge notes for the cue-lab branch

Overlap is small and mostly additive:

| File | My hunk | cue-lab also modifies |
|---|---|---|
| `Services/AudioAnalysis/AudioAnalysisService.cs` | 47 lines, gating only | +1 line |
| `Services/AnalysisQueueService.cs` | 11 lines | +15 lines |
| `App.axaml.cs` | +3 lines | yes |

The page files (`AnalysisPageViewModel.cs`, `AnalysisPage.axaml`, `AnalysisCoverageRows.cs`,
`Models/AnalysisEngine.cs`, both new services, the test file) are **not** in the cue-lab change set.
Conflicts should be limited to the three files above and resolvable by keeping both sides.

Note `WaveformExtractionService.cs` was rewritten on cue-lab; `HasWaveform` only requires a
non-empty blob or a positive sample count, so it stays correct for the new extractor.

## 9. Open questions / suggested follow-ups

1. **Queue dedupe is keyed by track hash alone** (`_scheduledTrackHashes`, `_activeTrackHashes`), so
   a partial request for a track that is already queued is silently dropped. The mask should be part
   of the key, or requests should merge into the pending one. Not fixed here.
2. **`StructuralStrippingEngine` can never run on a first analysis**: it is gated on
   `features.Bpm > 0` (`AudioAnalysisService.cs:247`) but BPM is only set in the Essentia step
   (`:344`), later in the same method. One-line fix, but it changes analysis output, so it belongs
   with a cue-lab decision.
3. **No per-engine version is recorded.** On `master` the pipeline writes only `AnalyzedAt`; the
   cue-lab branch adds a single `AnalysisVersion` that names the waveform rewrite. Either way there
   is no per-engine staleness, so after an engine upgrade old output still counts as present.
4. **Stems**: excluded from "fully analysed". Decide whether the card should show them as a separate
   opt-in row.
5. **CUE-DETR cues**: `CueDetrJson` is only ever written from the track inspector, never by the
   analysis flow. Today a track whose only cues are CUE-DETR counts as having cues. Decide whether
   that is intended.
6. **The library list is not filtered to the selected playlist** — every library row is shown, with
   coverage filled in for the selected playlist's tracks and left unmeasured elsewhere. A
   playlist-only filter may be the better affordance.
7. **A number of engines never write their fields at all** (`AvgPitch`, `VocalDensity`,
   `ChordProgression`, `VggishEmbeddingJson`, `AiEmbeddingJson`) — reported by the audit as
   "never assigned anywhere". They are not in the coverage mask for that reason; if they are meant
   to exist, they need their own engine entries once they actually run.

## 10. Review checklist

- [ ] Do the engine predicates match what the pipeline really writes? (see §3 table)
- [ ] Is `energy curve != "[]"` the right proof of the structure engine, given the structure job also
      writes phrases and drop times?
- [ ] Is the default mask right — should stems or CUE-DETR be in "fully analysed"?
- [ ] Does gating `CuePointDetectionService` on `Structure` (rather than `Cues`) match intent? It
      only writes `DropTimeSeconds`/`DropConfidence`/`TimeTillDropSeconds`; the cue **rows** come from
      the structure job.
- [ ] When `Mood` is requested alone, the Essentia pass still runs (it yields tempo/key together).
      Acceptable, or should a mood-only run skip Essentia?
- [ ] `AnalysisTrackItem.HasAnalysis` now prefers measured coverage and falls back to `AnalysisData`
      for queue rows that have not been measured. Is that the right precedence?
- [ ] Anything in §9 that should be fixed now rather than later.

## 11. Revert

Everything is in `0a25c14`. `git revert 0a25c14` drops the feature cleanly; the only behavioural
change outside the page is the new optional `engines` parameter (default = previous behaviour) and
the structure-job gate, which is inert unless a mask is passed.
