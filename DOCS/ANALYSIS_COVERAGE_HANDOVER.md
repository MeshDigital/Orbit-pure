# Analysis coverage and per-engine runs — handover

Original feature: `analysis-engines-2026-10-28`, commit `0a25c14`, built on `origin/master`
(`8ed8403`). Merge-ready corrections were made on `analysis-engines-merge-ready-2026-10-28`.
The original worktree was `C:\Users\quint\OneDrive\Documenten\GitHub\ORBIT-analysis`; final fixes
were isolated in `C:\Users\quint\OneDrive\Documenten\GitHub\ORBIT-Pure\.kimi-merge-ready`. The main
checkout was left untouched because `cue-lab-waveform-polish-2026-10-28` is in progress there.

This document states what changed, why, how to verify it, where it overlaps the cue-lab branch, and
what remains open. Nothing here has been run against a real UI session or real-audio partial run —
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
| `Services/AudioAnalysis/AnalysisCoverageService.cs` | Per-playlist (or whole-library) coverage: features rows + cue counts + fingerprint files, matched by hash and restricted to files actually on disk. `GetRequests` returns exact per-track work for the current selection. |
| `ViewModels/AnalysisCoverageRows.cs` | `PlaylistChoice` (picker entry) and `EngineCoverageRow` (one row of the coverage table). |
| `Tests/SLSKDONET.Tests/Analysis/AnalysisCoverageServiceTests.cs` | Database/file-backed tests for availability, hash matching, empty selections and per-track request scoping. |

### Changed files

| File | Change |
|---|---|
| `Events/AnalysisEvents.cs` | `TrackAnalysisRequestedEvent` gained `AnalysisEngine Engines = AnalysisEngine.Pipeline`. |
| `Services/IAudioAnalysisService.cs` | `AnalyzeFileAsync(..., AnalysisEngine engines = AnalysisEngine.Pipeline, CancellationToken)` — inserted **before** `cancellationToken`; existing call sites use named arguments. |
| `Services/AudioAnalysis/AudioAnalysisService.cs` | Returns early on `engines == None`; gates each output and scopes PCM, drop-signal, Essentia and downbeat work to the engines that need it. Structural stripping no longer waits for BPM that is only set later. |
| `Services/AnalysisQueueService.cs` | Passes `evt.Engines` through. Structure-only runs do not create cue rows; Cues-only runs regenerate cues from stored signals without incrementing `StructuralVersion`. |
| `Services/AnalyzeTrackStructureJob.cs` | Adds explicit cue-generation control and a cues-only regeneration path. |
| `App.axaml.cs` | Registers `AnalysisCoverageService`. |
| `ViewModels/AnalysisPageViewModel.cs` | Adds playlist/library coverage, exact per-track Run/Fill requests, empty-selection handling, throttled refresh and stale-response protection. Unmeasured library entries no longer receive fabricated `AnalysisData`. |
| `Views/Avalonia/AnalysisPage.axaml` | Coverage card (picker, headline, per-engine Run), Refresh button, row-level missing details and run-missing action. |
| `Tests/SLSKDONET.Tests/Analysis/AnalysisCoverageAnalyzerTests.cs` | Strict stored-output predicates, constructor defaults, cue rows, fingerprints, summaries and mask helpers. |
| `Tests/SLSKDONET.Tests/ViewModels/AnalysisPageViewModelTests.cs` | Regression coverage for honest unmeasured rows, selection-scoped requests and coverage refresh behavior. |

## 3. Engines and what proves each one

`AnalysisEngine.Pipeline` = everything except stems. A track is "fully analysed" when all of these
are present. Predicates inspect stored output only; constructor defaults and unrelated engine output
do not count.

| Engine | Stored output | Exact proof | Main gate |
|---|---|---|---|
| Waveform | `WaveformBlob` | blob is non-empty; a positive sample count without its blob is not enough | `AudioAnalysisService.cs:161` |
| Loudness | `LoudnessLUFS` | `!= 0` (0 is not measured; the −100 silent marker counts) | `AudioAnalysisService.cs:204` |
| Tempo & grid | `Bpm`, `BeatGridJson`, downbeat | `Bpm > 0` **and** non-empty grid | `AudioAnalysisService.cs:340–378`, `:453–468` |
| Key | `Key`, `CamelotKey`, `KeyConfidence` | Camelot non-blank, or key non-blank with confidence > 0 | `AudioAnalysisService.cs:340–353` |
| Mood & genre | genre distribution, embedding, confident mood tag | any one is present; structural `EnergyScore` does not qualify | `AudioAnalysisService.cs:291–335`, `:380–434`, `:449–451` |
| Structure | `StructuralVersion`, phrases, drop time | version > 0, non-empty phrases, or drop time present; a bare energy curve does not qualify | `AudioAnalysisService.cs:222–282`, `:470–478`; queue `:328–334` |
| Cues | `CuePoints` rows or legacy pipeline JSON | row count > 0 or non-empty `CuePointsJson`; inspector-only `CueDetrJson` does not qualify | queue `:328–340` |
| Fingerprint | entity fingerprint or `%APPDATA%\ORBIT\Fingerprints\…json` | non-blank stored fingerprint or file exists | `AudioAnalysisService.cs:485–497` |
| Stems | stem cache files | **not** in the default mask — separate on-demand job | n/a |

Decisions applied during merge review:

- **Valence/arousal and energy score are not Mood evidence.** They have measured-looking defaults or
  are written by structural/Essentia work, so they cannot prove that Mood produced output.
- **Cue coverage counts pipeline cues, not the CUE-DETR inspector cache.** Live runs persist
  `CuePoints` rows; legacy `CuePointsJson` remains accepted for old data.
- **`CuePointDetectionService` remains Structure-gated.** It writes drop fields, not cue rows. Cue
  rows are generated only when `Cues` is requested.
- **A Mood-only run may still invoke Essentia**, because one pass yields tempo, key and mood inputs,
  but only Mood-requested outputs are retained. Tempo and Key fields are not overwritten unless
  those engines were requested.

## 4. Coverage semantics

- Only **offline-available** tracks are measured: playlist `Status == Downloaded`, or availability
  `Ready`/`LocalUnanalyzed`, plus a nonblank path for which `File.Exists` is true. Availability flags
  alone are not enough after a file is moved or deleted.
- Rows are matched **by hash**, never by position — the library list and a playlist are different
  populations in different orders.
- Run/Fill actions use `PlaylistAnalysisCoverage.GetRequests(...)`: only tracks in the current
  selection are queued, each with only its own missing engines. Complete tracks are skipped and
  duplicate playlist entries collapse into one request.
- Selected tracks need not be present on the currently loaded library page; their requests still use
  the normal queue path, which resolves the track from the database.
- Stems are reported by the catalog but excluded from the default mask, so "fully analysed" does
  not silently depend on a heavy optional job.

## 5. UI and refresh behavior

Coverage card, in the capped scroll strip above the two panes:

- playlist picker ("Whole library" first, then playlists, newest first);
- headline "N of M tracks fully analysed", breakdown "x partially analysed · y not analysed at all";
- one row per engine: name, purpose, `118 / 140`, "22 missing", and a **Run** button;
- **Fill missing** queues the exact per-track work missing from the selection;
- **Refresh** reloads coverage manually. Successful refreshes are throttled, stale selection
  responses are ignored, and an empty selection remains visible with zero counts rather than
  retaining the previous playlist's numbers.

Library rows show a third line "Missing Key, Cues and Fingerprint" when measured and a run-missing
button next to the existing add-to-queue button. Unmeasured rows no longer show fabricated analysis.

Queueing reuses `TrackAnalysisRequestedEvent` with the mask, so progress and completion follow the
existing queue path.

## 6. Verify

From the merge-ready worktree (with the Windows SDK environment configured for the build):

```
cd C:\Users\quint\OneDrive\Documenten\GitHub\ORBIT-Pure\.kimi-merge-ready
dotnet build SLSKDONET.sln --no-restore
dotnet test Tests\SLSKDONET.Tests\SLSKDONET.Tests.csproj --no-restore --filter "FullyQualifiedName~AnalysisCoverage|FullyQualifiedName~AnalysisPageViewModelTests"
dotnet test Tests\SLSKDONET.Tests\SLSKDONET.Tests.csproj --no-restore --filter "FullyQualifiedName~Architecture"
dotnet test Tests\SLSKDONET.Tests\SLSKDONET.Tests.csproj --no-restore
```

Final merge-ready results:

- solution build: **0 errors**, 40 pre-existing warnings;
- focused coverage and page ViewModel tests: **40/40 passed**;
- Architecture tests: **61/61 passed**;
- full suite: **2431 passed, 5 failed, 11 skipped** (2447 total).

All five failures are the known `CueDetrParityTests` fixture problem:
`Tests/SLSKDONET.Tests/TestData/CueDetr/meta.json` is absent from the fresh worktree. There are no
new failures. Restoring that fixture is required before claiming a completely green full suite.

## 7. Not verified

- **The page has not been opened.** XAML compiles and the ViewModel tests pass, but no visual check
  of the coverage card, playlist picker, Refresh button or row layout was done. Check 1920×1080 and
  the compact single-pane layout.
- **No real end-to-end partial run.** Actual audio was not run through waveform-only, structure-only
  or cues-only repairs; gating is verified by code review, build and regression tests.
- `Classes="caption warning"` on the row's missing line assumes a `warning` text class exists in the
  design tokens; if it does not, the text still renders, just without the colour.

## 8. Merge notes for the cue-lab branch

The original feature's overlap is small and mostly additive:

| File | Analysis-coverage change | Cue-lab also modifies |
|---|---|---|
| `Services/AudioAnalysis/AudioAnalysisService.cs` | per-engine gating and scoped work | yes |
| `Services/AnalysisQueueService.cs` | engine forwarding and structure/cues dispatch | yes |
| `App.axaml.cs` | coverage-service registration | yes |

The page files, coverage services and coverage tests are not in the cue-lab change set. Conflicts
should be limited to the shared files above and resolved by keeping both behaviors.

`WaveformExtractionService.cs` was rewritten on cue-lab. Coverage now requires the actual non-empty
waveform blob, which remains the correct proof for either extractor.

## 9. Open questions / suggested follow-ups

1. **Queue dedupe is keyed by track hash alone** (`_scheduledTrackHashes`, `_activeTrackHashes`), so
   a partial request for a track that is already queued is silently dropped. The mask should be part
   of the key, or requests should merge into the pending one. Not fixed here.
2. **No per-engine version is recorded.** On `master` the pipeline writes only `AnalyzedAt`; the
   cue-lab branch adds a single `AnalysisVersion` that names the waveform rewrite. Either way there
   is no per-engine staleness, so after an engine upgrade old output still counts as present.
3. **Stems** are excluded from "fully analysed". Decide whether the card should show them as a
   separate opt-in row.
4. **The library list is not filtered to the selected playlist** — every library row is shown, with
   coverage filled in for the selected playlist's tracks and left unmeasured elsewhere. A
   playlist-only filter may be the better affordance.
5. **A number of engines never write their fields at all** (`AvgPitch`, `VocalDensity`,
   `ChordProgression`, `VggishEmbeddingJson`, `AiEmbeddingJson`). They are not in the coverage mask;
   if they are meant to exist, they need engine entries once they actually run.

## 10. Review decisions applied

- Engine predicates now match stored pipeline output rather than defaults or unrelated fields.
- A bare energy curve no longer proves Structure; CUE-DETR inspector JSON no longer proves Cues.
- Structure and Cues have separate side effects, including cues-only regeneration.
- Structural stripping runs for Structure/Cues work without an impossible first-run BPM gate.
- Measured coverage takes precedence; unmeasured library entries no longer get synthetic
  `AnalysisData` merely to make the row look complete.
- Selection-scoped, per-track requests replace the previous all-loaded-tracks/union-mask behavior.

## 11. Revert

The feature and merge-ready corrections are separate commits. Revert the eventual merge commit on
`master` to remove the complete change set. Outside the Analysis page, the main behavioral changes
are the optional `engines` parameter (default = full pipeline) and explicit Structure/Cues dispatch.
