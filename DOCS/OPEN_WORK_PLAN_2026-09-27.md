# ORBIT open-work plan (2026-09-27)

This is the single list of open work. It replaces the older plan files, which are now archived in
`DOCS/archive/plans/`. Everything here comes from three read-only code audits run on 2026-09-27:

- plan files and checklists against the code
- features that are built but can't be reached
- code that was left half-finished

**Nothing here has been confirmed as needed yet.** Every item starts at **Investigate**. For each one:

1. Check the evidence (the file:line references were correct on 2026-09-27).
2. Decide: **Fix / Wire up**, **Finish**, **Delete**, or **Won't do**.
3. Tick the box and write the decision and the commit on the item's line.

Effort: **S** is under an hour, **M** is about half a day to a day, **L** is several days.

---

## H. PRIORITY: drop detection and cue placement accuracy
Added on 2026-09-27 after comparing an outside write-up (EDM phrasing, sub-bass dropout, spectral
flux, onset density, an 8-pad cue template) with `Engine/Cueing/CueGenerationService.cs`.

**Already covered by the current engine:**
- A 3-path priority: EDMFormer/Rekordbox phrase segments → DSP (sub-bass return + spectral flux +
  broadband RMS jumps + genre-family candidates) → heuristic.
- Bars → seconds conversion and snapping to 8-bar phrases.
- Genre-family weighting (breakbeat vs four-on-the-floor).
- Genre-typical spacing between the two drops.
- An 8-cue set of Intro, 16 and 8 bars to Drop 1, Drop 1, 16 and 8 bars to Drop 2, Drop 2, Outro.

**Where the write-up disagrees with our own data:** it suggests mix-in cues 32 and 16 bars before
the drop. The current 16- and 8-bar spacing is documented as measured against 312 of your own
Rekordbox-cued tracks (`CueGenerationService.cs:344-347`).
**⚠ H0 casts doubt on that claim.** In the same 312-track reference set, the hand cues are mostly
phrase markers every 8 bars through the intro (bars 1/9/17/25/33), not countdowns to a drop. Keep
16/8 for now, but re-check it once H0b gives a real drop reference.

**Real gaps found in the code:**

- [x] **H0. Committed accuracy benchmark.** Done 2026-09-27: `Tests/CueBenchmark` (see its README).
  - Read-only; refuses the live DB.
  - Regenerates cues through `CueGenerationService.GenerateCuesWithPath`, a new method that also
    reports which path was used.
  - Reference cues come from a snapshot (`C:\tmp\rekordbox_cues.json`: 312 tracks, of which 84 match
    by filename and have 2 or more cues) or from a live ANLZ scan when the Rekordbox export drive is
    attached.
  - Reports grid alignment, cue coverage and inferred drops, broken down by path and genre family.
    `--compare` shows the deltas against a baseline.

  **Baseline (2026-09-27, 84 tracks):**
  - Only **52%** of hand cues land within ¼ beat of an ORBIT beat. Random placement gives 50%.
  - Only **14%** land on an ORBIT bar line (random gives 12.5%), and 6% on an 8-bar phrase line.
  - 3 of 84 tracks have every cue on an ORBIT bar line.
  - Nearest generated cue to a hand cue: median 2.1 s.
  - Inferred-drop numbers (median 17 s off) are not trustworthy; see H0b.

  → ORBIT's grid doesn't line up with the DJ's at all. The root cause is H-BPM below.
- [x] **H-BPM. Done 2026-09-27 (not yet committed).**
  - **Changes:**
    - New `Services/AudioAnalysis/BeatGridFitter.cs`. It finds the tempo the most beat ticks agree on
      (a coarse-to-fine scan), refines it by least squares over those ticks only, and applies the
      octave from the corrected BPM. It then picks the downbeat from sub-bass returns plus dropouts
      shifted back one beat (the measured detector lag).
    - The fitter is wired into `AudioAnalysisService` as step 5c, after genre inference.
    - `BpmDetectionService` no longer lets the whole-number histogram median replace Essentia's
      continuous BPM.
    - New `BeatGridRecomputeService`: a one-time background pass, 60 s after launch.
      1. Writes a SQLite backup to `%APPDATA%\ORBIT\Backups\library.db.bak-beatgrid-fit-v1-*`.
      2. Re-fits every analysed track from its stored ticks.
      3. Regenerates **auto** cues only, and only for tracks that already had auto cues.
      4. Writes a marker file so it runs once.

      Displayed BPM keeps its precedence: a manual edit or file tag still wins.
  - **Benchmark (84 tracks, stored grid → refit):**

    | Measure | Before | After |
    |---|---|---|
    | BPM within 0.1 of the cue-implied BPM | 5% | **86%** |
    | Median BPM error | 1.50 | 0.01 |
    | Hand cues on a beat | 52% | 85% |
    | On a bar line | 14% | 54% |
    | On an 8-bar line | 6% | 45% |
    | Within ½ beat of a generated cue | 13% | 30% |

  - **Library dry run** (3,618 tracks): 3,486 fitted, the 178/172.27 spikes are gone, about 150 s of
    CPU in total.
  - Tests: `BeatGridFitterTests` (8), plus the updated `BpmDetectionService` tests.
  - **Still open:** the bar phase is right on only 54% of cues. A downbeat test on the audio itself
    (kick vs snare energy by beat position over the whole track) could lift this for new analyses.

  *Original finding, kept for reference:*
- [ ] ~~**H-BPM (NEW TOP PRIORITY). The stored BPM is quantised and often wrong, so every
  grid-snapped cue drifts.**~~
  - The cue spacing implies Rekordbox's BPM (cues sit on its grid). ORBIT's stored BPM is within
    0.1 BPM of it on only **2 of 64** tracks, and within 1 BPM on 23 of 64.
  - Library-wide, 3,476 of 5,074 BPMs are whole numbers. The most common values are **173.0 (824),
    172.27 (620), 178.0 (532)** and 89.0 (257).
  - **Causes:**
    1. `BpmDetectionService.HistogramMedian` (`Services/AudioAnalysis/BpmDetectionService.cs:120`)
       replaces Essentia's continuous `rhythm.Bpm` with a whole-number histogram bin.
    2. For DnB, the half-time reading is snapped to a whole number (87 or 89) and then doubled
       (`:94-101`), giving 174 or 178. A real 174 track that reads 89 becomes 178, which is 2.3% off:
       about 1 beat of drift every 11 bars.
    3. 172.27 is exactly 60·44100/512/30, a whole-frame lag value from the beat tracker, and
       `BeatGridJson` intervals come in steps of that size too. The average over the full span is
       noisy, because beats get lost in breakdowns.
  - *Fix direction:*
    - Keep Essentia's continuous BPM; don't let the median override it.
    - Double the continuous value rather than a rounded one.
    - Fit the tempo and phase to the beat ticks (a robust linear fit that ignores gaps) instead of
      "median interval + first tick".
    - Where Rekordbox has analysed the track (1,300+ local ANLZ files), consider its PQTZ beat grid
      as an authoritative source, the same way PSSI is already used for phrases.
  - Measure with H0: target on-beat above 90% and on-bar above 80%.
  - Needs a re-analysis pass over the library afterwards, or at least a BPM/grid recompute from the
    stored `BeatGridJson`. **M**
- [x] **H0b. Done 2026-09-27, option (a).**
  - `RekordboxAnlzParser` now reads PQTZ beat grids (with a test).
  - `Tests/CueBenchmark --rekordbox-analysis` scores against Rekordbox's local analysis:
    - 808 tracks are scored for grid, BPM and phrase alignment.
    - 589 tracks have drop references: only Rekordbox "high mood" (EDM) tracks, where Chorus = drop.
      In pop, hip-hop and disco, Chorus is a sung chorus.
    - The 73 tracks where ORBIT already uses PSSI as input are excluded.
  - **It confirmed H-BPM independently:** BPM within 0.1 of Rekordbox went from 20% to 86%.
  - **Baseline** (with refit): Drop 1 within 1 bar 35%, within 4 bars 42%, median 16.8 s. Drop 2:
    36% / 43% / 13.9 s.
  - **Two things tried and rejected, on measurement:**
    - Moving the grid onto the kick from the audio (a half-beat or small shift). It made bar
      alignment worse (33% → 24%), so it was removed.
    - Dropping the "Heuristic" phrase source in favour of DSP. Every drop metric got worse, so the
      source stays.
- [x] **H-RBGRID. Done 2026-09-27.**
  - **Changes:**
    - `IRekordboxPssiService.GetBeatGridAsync` reads the PQTZ grid from the matched track's `.DAT`.
    - New `BeatGridFitter.ApplyRekordboxGrid` adopts Rekordbox's BPM, beats and first real downbeat,
      but only if at least 50% of ORBIT's own beat ticks land within 70 ms of a Rekordbox beat. That
      rejects same-named files that are really different copies with a different lead-in.
    - Wired into analysis step 5c (after the fitter) and into `BeatGridRecomputeService`.
  - **Measured on your hand cues:** 54 of 84 tracks adopted Rekordbox's grid and 24 were rejected as
    not matching.

    | Hand cues… | Refit only | + Rekordbox grid |
    |---|---|---|
    | on a beat | 85% | 92% |
    | on a bar line | 54% | 77% |
    | on an 8-bar line | 45% | 60% |
    | tracks with every cue on a bar | 41/84 | 60/84 |

  - Tests: accept on match (including half-time ticks) and reject a 150 ms-shifted copy.
- [ ] ~~**H0b. A real drop reference.**~~ *Original:* Your hand cues mark phrases, not drops, so they
  can't score drop detection. Options:
  - (a) Read Rekordbox's own PSSI phrase analysis ("Chorus" = drop) for the ~1,300 locally analysed
    tracks, placed on Rekordbox's PQTZ beat grid. Exclude the tracks where ORBIT already uses PSSI
    as an input (circular).
  - (b) You mark Drop 1 and Drop 2 on about 40 varied tracks in Cue Forge as a gold set. This is the
    most reliable option.

  **S–M**
- [x] **H1. Done 2026-09-27 (Drop 2 part).** `CueGenerationService.SelectSecondDrop` picks the
  best-scored candidate at least 16 bars after Drop 1, preferring one with a sub-bass dropout (the
  breakdown) in between. It falls back to the old second-half pick. Tests were added.
  - **Measured:** DSP-only Drop 2 within 4 bars +3 points and median −2.9 s. With phrase data: Drop 2
    +1 point and median −0.6 s, with no regressions.
  - **Drop 1's window was left at 10–50%.** An earlier start (5%) gained +7 points on Drop 1 but lost
    3–7 on Drop 2 in every variant tried. Not worth it without a better Drop 2 anchor.
  - *Original finding:* **The second drop is picked by "top score in the second half" instead of
    "the valley and peak that follow Drop 1".**
  - The DSP path (`CueGenerationService.cs:474-480`) takes the highest-scored candidate in fixed
    windows: 10–50% of the track for Drop 1 and 50–90% for Drop 2.
  - `SubBassDropoutTimestamps` (the breakdown valleys) are computed and stored but **never used by
    the DSP path**. Only the heuristic `IntentClassifier` reads them.

  *Change:* pick Drop 2 as the strongest return after the first real dropout (breakdown) that
  follows Drop 1. Relax the fixed windows so a track whose Drop 2 lands before the midpoint (long
  outro, short track) or whose Drop 1 lands after it (long DJ intro) still works. Measure with H0.
  **M**
- [ ] **H2. Fake-out builds and snare rolls are never rejected by onset density.**
  `OnsetDensityEngine` exists, but it's only computed for Cue Forge's display
  (`CueForgeViewModel.cs:840`). Detection never uses it, and nothing stores it.
  *Change:* compute it at analysis time and use it to penalise candidates in dense roll regions (a
  build) and to favour the reset to a steady kick pattern after one. **M**
- [ ] **H3. No Breakdown or Mix-Out pad.** The 8 pads are fully used by the countdown layout, so the
  write-up's Breakdown cue (the valley after Drop 1, useful for early mix-outs and loops before a
  double drop) and its Mix-Out cue (Drop 2 plus 16 or 32 bars) have no slot.
  *Decide:* offer a "Structure" layout next to the current "Countdown" one: Intro, 16 bars to Drop 1,
  Drop 1, Breakdown, 16 bars to Drop 2, Drop 2, Mix-Out, Outro. Make it a setting, or add these as
  memory cues (non-pad markers, which Rekordbox supports as Type 0 without a hot-cue number). **M**
- [ ] **H4. Continuous genres (hard techno, some house): the dedicated helpers never run.**
  `HarmonicPhaseTracker` and `EnergyCurveNormalizer` are only used inside the dead
  `Engine/Analysis/AnalysisPipeline.cs` (see D1), so the live analysis path never runs them. For
  linear genres without a silent breakdown, the write-up recommends harmonic and phrase-grid cues
  instead of energy valleys.
  *Investigate:* move them into the live `AudioAnalysisService` path for four-on-the-floor tracks, or
  accept that the existing RMS energy-jump plus structural-stripping candidates are enough (check
  with H0's four-on-the-floor numbers). **M–L**
- [ ] **H5. Tracks that aren't strict 4/4 or don't hold one tempo.** All snapping assumes one
  constant BPM from a single downbeat anchor (`SnapToBar`/`SnapToPhrase`). A live-drummed, tempo-
  drifting or half-time/double-time-ambiguous track gets its cues snapped onto a grid that drifts
  away from the audio.
  *Investigate:* detect grid drift (compare beat-tracker ticks against the constant grid near each
  cue). Where it drifts, snap to the nearest real tracked beat instead and lower the confidence.
  **M**
- [ ] **H6. Related items elsewhere in this plan that feed the same engine:**
  - **A5:** the aggressive-mood signal is always zero, so the intent classifier's boost is dead.
  - **C2:** EDMFormer confidence is hardcoded to 0.9, so the phrase path can't be weighed against
    the DSP path.

  Do both as part of this section. **S + M**

**Order (revised 2026-09-27 evening):** H-BPM, H0b, A5, H1 and H-RBGRID are done.
1. **The phrase path.** It carries about 80% of drops and is the weakest link. The "Heuristic"
   structure segments are rigid 16-bar blocks with odd "Drop" labels (traced on Canned Heat and
   Omen).
   - *Tried 2026-09-27:* rebuilding the stored Heuristic segments with the corrected BPM, with the
     phrase grid counted from 0 s or from the downbeat. Both are neutral within ±3 points and
     trade Drop 1 against Drop 2, because the Heuristic drop times come from energy novelty
     (`StructuralAnalysisEngine.FindDrops`), not the grid. Reverted.
   - *Next idea:* improve `FindDrops` itself. For example, score candidates with the sub-bass
     return signal and require a preceding dropout, rather than relying on 1-second RMS novelty only.
2. H2, H3 (decide the layout), and C2.
3. H4/H5, depending on the numbers.

## A. Broken now: users see something that doesn't work
These come first. Most are small, and every one is a visible bug.

- [x] **A1. Done 2026-09-27:** `MainViewModel` now forwards `NotificationEvent` to the same toast
  path as `ToastRequestedEvent`, which fixes all 12 publishers at once. *Original:* **About 12
  warning and error toasts never show up.** `NotificationEvent`
  (`Services/NotificationEvent.cs:7`) has no subscriber. The toasts that work use
  `ToastRequestedEvent` (`Views/MainViewModel.cs:367`). Messages lost this way:
  - Session Conflict: `ConnectionLifecycleService.cs:315,401,581`
  - Auto-Retry Triggered: `DownloadManager.cs:1823`
  - `LibraryService.cs:1894`
  - 7 error toasts in `ViewModels/Library/TrackOperationsViewModel.cs:502-669`

  *Investigate:* should `NotificationEvent` forward to `ShowToast`, or should its publishers switch to
  `ToastRequestedEvent`? **S**
- [x] **A2. Done 2026-09-27:** both Find Similar commands now send the same
  `FindSimilarTrackRequestEvent` as the Library (it opens the app-wide Similar Tracks panel). The
  dead `FindSimilarRequestEvent` was deleted. *Original:* **The Download Center "Find Similar" button
  does nothing.** `StandardTrackRow.axaml:558` →
  `UnifiedTrackViewModel.cs:617` publishes `FindSimilarRequestEvent`, but that event's subscriber
  was removed (`SearchViewModel.cs:456`). The `FindSimilarAiCommand` at `UnifiedTrackViewModel.cs:521`
  has the same cause. *Investigate:* point it at the Library similarity path, or remove the button. **S**
- [x] **A3. Done 2026-09-27:** "Search Again" now hard-retries the track: it resets to Pending with a
  clean state and gets a fresh search. The dead `ManualSearchRequestEvent` was deleted. *Original:*
  **Library inspector "Search Again" cancels the track but never starts a new search.**
  `LibraryTrackInspector.axaml:446` → `UnifiedTrackViewModel.cs:383` publishes
  `ManualSearchRequestEvent`, which has no subscriber. *Investigate:* call DownloadManager directly. **S**
- [x] **A4. Done 2026-09-27: it wasn't a live bug.** The service with the silent fallback,
  `Services/StemSeparationService.cs`, wasn't registered in DI or created anywhere. The app separates
  stems through `DemucsOnnxSeparator` → `CachedStemSeparator` → `StemSeparationServiceAdapter`. It
  was deleted together with its only consumer, `BatchStemExportService` (also dead; see C4), and its
  tests. *Original:* **When no stem separator is available, the app returns silent files as a
  success.**
  `StemSeparationService.cs:99-100` writes silent mock WAVs if both ONNX and Spleeter are missing.
  *Investigate:* confirm which separator the service really calls (is it `DemucsOnnxSeparator`?),
  then return a real error that the user sees. **M**
- [x] **A5. Done 2026-09-27:** `AnalysisPipelineResultBuilder` now passes `f.MoodAggressive` (0–1),
  with a test. *Original finding:* **The aggressive-mood signal is always zero.** `AnalysisPipelineResultBuilder.cs:26` hardcodes
  `EssentiaAggressiveProbability = 0f`, even though the ONNX model fills
  `AudioFeaturesEntity.MoodAggressive`. Because of this, the boost in `IntentClassifier.cs:111` never
  takes effect. *Investigate:* check the value range (0–1?), then make it `= f.MoodAggressive`. **S**
- [ ] **A6. The "Gate 1" new playlist surface is switched off, and its flag is read by nothing.**
  `UseNewPlaylistSurface = false` is at `Configuration/AppConfig.cs:208` and exposed at
  `LibraryViewModel.cs:161`. No view reads it and there's no Settings toggle. Known problems: it
  crashes when a track is selected, and it has no art column or header controls.
  *Decide:* finish it (test with an isolated config, never the live one) or delete the flag. **S to
  delete, M to finish**
- [x] **A7. Done 2026-09-27:** the explicit Save button shows a "Settings saved" toast. Auto-saves
  (60+ call sites) stay silent, but a failed save now shows an error toast from any path.
  *Original:* **Settings saves with no confirmation.** There's a TODO toast at `SettingsViewModel.cs:2323`. **S**
- [x] **A8. Done 2026-09-27:** it really did delete: the file on disk plus history, library and
  playlist rows, with no confirmation. It was also unbound, so the command was removed instead of
  being left for someone to wire up by accident. *Original:* **Check whether `CleanCommand` really
  deletes.** `UnifiedTrackViewModel.cs:390-393` is
  commented "might be a placeholder" but calls `DeleteTrackFromDiskAndHistoryAsync`. **S**

## B. Built but can't be reached: decide to wire up or delete

- [ ] **B1. SavedDoubles: you can save pairs but never see, rename or remove them.**
  - The only way to save is `DoubleInspectorPanel.axaml:127`. Saved pairs only feed a hidden +0.03
    score and a badge.
  - `LibraryPage.axaml.cs:428` looks up `SavedDoublesSidebarSection`, which no view defines.
  - The styles at `LibraryPage.axaml:34-63` are unused.
  - `ViewAllSavedDoublesCommand` and `RemoveSavedDoubleCommand` (`LibraryViewModel.Commands.cs:94`)
    can't be reached.
  - `SavedDoublesForLeadTrack` (`LibraryViewModel.cs:235`) isn't bound anywhere.
  - `SavedDouble.Label` is always saved as null.
  - `PlayerFallbackPanel.axaml:215-297` shows doubles, but that panel is never created.

  *Finish:* a sidebar list with activate, remove and rename; show the doubles for the lead track in
  the side panel; mount or delete `PlayerFallbackPanel`. **M**
- [ ] **B2. Video Export.** `ViewModels/VideoExportViewModel.cs` (255 lines) and
  `Views/Avalonia/VideoExportView.axaml` drive `Services.Video.VideoRenderer`. The ViewModel isn't
  registered in DI and nothing opens the view. *Investigate:* does it still render? If so, add a DI
  registration and a Workstation Export entry. **M**
- [ ] **B3. YouTube chapter export.** `YouTubeChapterExportService` ("Task 8.4") is finished and tested
  but has no caller. *Wire up:* an export button in the Mix Editor. **S**
- [ ] **B4. Traktor and Serato metadata importers.** `Services/Integrations/TraktorMetadataImporter.cs` and
  `SeratoMetadataImporter.cs` aren't in DI and have no callers (they were meant to close issue #40).
  *Investigate:* do they still match the current models? Then add them to Import. **M**
- [ ] **B5. Album tree browser.** `HierarchicalLibraryViewModel` is created at
  `TrackListViewModel.cs:753` but no view binds to it, and `PlaylistGridView.axaml` isn't referenced.
  *Decide:* use them as an alternate Library view (Winamp Tier 1), or delete them. **M**
- [ ] **B6. Database backup.** `DatabaseService.BackupDatabaseAsync` (`Services/DatabaseService.cs:2308`)
  has zero callers. *Wire up:* scheduled or startup backups plus a Settings button. **M**
- [ ] **B7. Deck Sync and Phase-Align.** In `ViewModels/DeckViewModel.cs`, `SyncA/BToMasterCommand`
  (379/381), `PhaseAlignA/BToMasterCommand` (383/385), `SetPitchRangeCommand`, `DeleteHotCueCommand`
  and `StopOutputCommand` have no bindings. *Investigate:* is the Mixer still part of the product?
  If yes, add them to `MixerCenter.axaml`. **M**
- [ ] **B8. Handlers that exist but are never triggered.**
  - The album download handler for `DownloadAlbumRequestEvent` (`DownloadManager.cs:267/293`).
    Compare it with `TrackOperationsViewModel`'s Download Album path.
  - The style-filter refresh for `StyleDefinitionsUpdatedEvent` (`TrackListViewModel.cs:853`).
    Publish the event when style definitions change.

  **S each**
- [ ] **B9. Download Center row actions and bulk actions.**
  - `ViewLogCommand` and `CopyLogCommand` (`UnifiedTrackViewModel.cs:1622-1623`)
  - `ForceDownloadCandidateCommand` (`:478`)
  - `ClearFailedCommand`, `PauseAllCommand` and `ResumeAllCommand` (`DownloadCenterViewModel.cs:632-635`)
  - `AcquireMissingTracksCommand` (`LibraryViewModel.Commands.cs:146`)

  *Decide per command:* context menu or delete. The pause and resume commands probably duplicate the
  header's. **S**
- [ ] **B10. Small commands with no buttons.**
  - Search presets: `ApplyPresetCommand` (`SearchViewModel.cs:397`)
  - Spotify import clipboard paste and copy (`SpotifyImportViewModel.cs:173-174`)
  - Export format commands (`ExportDialogViewModel.cs:103-105`). Probably redundant.

  **S**
- [ ] **B11. Crash report dialog.** `Views/Avalonia/ErrorReportDialog.axaml` is never opened.
  *Decide:* hook it into the unhandled-exception handler (for beta testers), or delete it. **S**
- [ ] **B12. `BulkOperationProgressModal`** is unused. It could show progress for bulk rename and move. **S**
- [ ] **B13. USB Drive Import** is a disabled "Coming Soon" card (`ImportPage.axaml:341-360`) with no
  backend. *Decide:* build it or remove the card. **S to remove, M to build**

## C. Half-finished features: decide to finish or drop

- [ ] **C1. Double Drop during live playback.** Double Drop only works in the offline preview.
  `AudioPlayerService` has its own real-time chain and no Double Drop. This was deferred from the
  2026-09-19 mix-flow plan. **M–L**
- [ ] **C2. EDMFormer confidence is hardcoded.** `EdmFormerService.cs:143` sets `0.9f`, and
  `Tools/edmformer_server.py:283` returns no probability. *Finish:* return the per-segment softmax
  maximum. **M**
- [ ] **C3. Album-mode search.** `SoulseekAdapter.cs:1487` finds candidate album directories, logs them,
  and does nothing else. **L**
- [x] **C4. Closed 2026-09-27:** `BatchStemExportService` was never registered or called, so it was
  deleted along with A4. *Original:* **5-stem instrumental export.** `BatchStemExportService.cs:70` only works if an
  `accompaniment.wav` already exists. **S–M**
- [ ] **C5. Discovery: "Recordings by Producer".** It's only a TODO (`DiscoveryBridgeService.cs:77`), and
  the service may have no callers. The Spotify Hub "Phase 7" is placeholder comments
  (`MainViewModel.cs:307,1094`). *Decide:* finish or delete. **M**
- [ ] **C6. Room member presence.** It only updates if something else is already watching that user
  (`RoomViewModel.cs:149-152`). **S–M**
- [ ] **C7. Audio fingerprinting.** `AudioFingerprint` and `SpectralHash` are copied between objects but
  never computed. **M–L**
- [ ] **C8. Crash-journal checkpoint.** A checkpoint is written on every progress tick above 1 KB
  (`DownloadManager.cs:3788`), inside the stall-detection timer. *Finish:* move it to its own
  heartbeat. **S–M**
- [ ] **C9. `OnMetadataUpdated`** raises about 50 PropertyChanged events without checking whether
  anything changed. **S**
- [ ] **C10. Non-Windows token storage** returns null (`WindowsTokenStorage.cs:120`). This only matters
  if cross-platform support is a goal. **M**
- [ ] **C11. Cue Forge inside Flow Builder.** This needs a per-instance editor ViewModel with its own
  audition deck. The `CueForgeViewModel` singleton currently takes over the shared player
  (`CueForgeViewModel.cs:697,761-784`). **L**

## D. Dead code: confirm it's unused, then delete
The two audits disagree on some of these services. One says each one has no consumer. The other
says every DI registration is referenced somewhere. Check each item with a grep before deleting.

- [ ] **D1. Services that may be orphaned:**
  - `Engine/Analysis/AnalysisPipeline.cs` (registered at `App.axaml.cs:824`; only its result type is
    used)
  - `DownloadOrchestrationService`
  - `PrefetchService`
  - `SpotifyBulkFetcher` and `SpotifyMetadataService`
  - `DiscoveryBridgeService` (see C5)
  - `LibraryActionProvider`
  - `SimilarityServiceAdapter`
  - `WaveformCacheService`
  - `EnrichmentTaskRepository`
- [ ] **D2. Unused controls:**
  - `SpectrogramControl` (a stub)
  - `DualWaveformDeck`
  - `AnalysisProgressModal`
  - `TrackCard`
  - `AlbumCard`
  - `SpotifyImportControl`
  - `DownloadGroupRow`
  - `PlaylistGridView` (see B5)
- [ ] **D3. Commands that have been replaced or are dead.** Before deleting, check each one isn't
  meant as a keyboard shortcut.
  - **Connection and settings:**
    - ConnectionViewModel: `DisconnectCommand`, and the empty stub `StartAutoReconnectLoop` (`:402-407`)
    - Dashboard: `ToggleRightPanelCommand`
    - `ClearSecurityQualityLogsCommand` (a no-op, `DownloadCenterViewModel.cs:678`)
    - ImportHistory: `LoadHistoryCommand`
  - **Library and track list:**
    - Unified: `FilterByVibeCommand`
    - LibraryViewModel: `ToggleEditModeCommand`, `ToggleActiveDownloadsCommand`,
      `SmartInsertAfterTrackCommand`
    - TrackList: `ToggleMixModeCommand`
    - PlaylistTrack: previous and next inspector tab
    - SetlistHealthBar: `RefreshDiagnosticCommand`
  - **Player, workstation and navigation:**
    - Player: `TogglePlayerDockCommand`, `GoBackCommand`
    - Timeline: `NewSessionCommand`
    - StemChannel: `ResetCommand`
    - Workstation: `FocusDeckCommand`
    - MainViewModel: `NavigatePlayer`, `NavigateTimeline`, `NavigateStems`, `ResetZoom`,
      `CancelDownloads`, and the Pause/Resume/Retry-All trio (`:309`)
- [ ] **D4. Events with only one side connected.**
  - Subscribed to but never published: `TrackUpdatedEvent`, `TrackMovedEvent`,
    `AddToQueueRequestEvent`, `SeekRequestEvent`, `SeekToSecondsRequestEvent`,
    `NetworkHealthWarningEvent`, `TrackStatusChangedEvent`.
  - Published but never consumed: `TrackStalledEvent`, `TrackStructureAnalysisCompletedEvent`,
    `SearchHardCapTriggeredEvent`, `SharedFilesStatusEvent`, `TransferFinished/Failed/CancelledEvent`.
  - *Decide per event:* delete it, or keep it as telemetry.
- [ ] **D5. The duplicate MP3-fallback setting** exists as two ViewModel properties for one config field. **S**
- [ ] **D6. `Tools/EDMFormer` repo hygiene.**
  - `git submodule status` fails because `.gitmodules` has no mapping for it.
  - Its `requirements.txt` is modified.
  - A `.safetensors` checkpoint in it is untracked.

## E. Performance leftovers (from the 2026-09-27 performance work)

- [ ] **E1. The database is VACUUMed on every launch.** This causes lock contention at startup: a
  10.4 s UI stall and a 6.6 s page fetch. *Investigate:* run it weekly or when idle instead. **S**
- [ ] **E2. The finalizer thread spends 29% of its time in `SKNativeObject.Finalize`.** The cause may be
  artwork bitmaps not being disposed. *Investigate:* take a profile trace. **M**
- [ ] **E3. No test checks that the library grid handles 50k tracks.** **S–M**
- [x] **E4. Done 2026-09-27.**
  - `BackgroundJobQueueTests` now wait for the actual progress events instead of fixed sleeps, and
    collect them in a thread-safe queue.
  - That class, `DownloadCenterSoftClearContractTests` and `AnalysisPageViewModelTests` share global
    state (the RxApp schedulers, Avalonia's Dispatcher), so they run in a non-parallel xUnit
    collection (`Tests/SLSKDONET.Tests/NonParallelCollection.cs`).
  - Result: 3 full runs in a row, all green. The suite now takes 22 s instead of 12 s.

  *Original:* **Flaky tests in full-suite runs.** A different handful fails on each full run and all pass
  when run on their own, which points to shared state or timing under parallel load. Seen so far:
  - `BackgroundJobQueueTests` (the worker concurrency and completion tests)
  - `DownloadCenterSoftClearContractTests`
  - all of `AnalysisPageViewModelTests` in one run

  *Fix:* find the shared static state and isolate it, or put these classes in a non-parallel xUnit
  collection. **S–M**

## F. Roadmap ("next Winamp")
Big features. Plan these separately; this list only tracks them.

- **Tier 1:**
  - [ ] listening EQ
  - [ ] light theme (`App.axaml:6` hardcodes Dark)
  - [ ] OS media keys
  - [ ] M3U and Rekordbox XML playlist import (see B4)
  - [ ] genre and artist tree (see B5)
- **Tier 2:**
  - [ ] skins
  - [ ] alternate library views
  - [ ] mini-player
- **Tier 3:**
  - [ ] plugin API
  - [ ] Last.fm scrobbling
  - [ ] remote access
- **Durability:**
  - [ ] Split the oversized classes: `WorkstationViewModel` (3,758 lines), `AnalysisPageViewModel`
    (2,523) and `DownloadManager` (4,762). This overlaps issue #163.
  - [ ] Tests for `ChatAttachmentService` and `ShareIndexService`.
  - [ ] Live tests with a real second peer: share serving, chat images and playlist Sync.

## G. GitHub issue triage
All 12 open issues are Workstation cockpit items (#153–#164).

- [ ] Close as done: #155, #157, #160, #161.
- [ ] Close as obsolete: #158 and #159. Those modes were removed in the 2026-07-22 switch to two
  workspaces.
- [ ] Check #153, #154 and #156 by hand; they're probably done.
- [ ] Keep open: #162 (timeline virtualization), #163 (unified state; see F Durability), and #164 (the
  contrast pass, which is partly done).

---

## Archived plan files
These were moved to `DOCS/archive/plans/`. Anything still open in them is included above.

- `DOCS/IMPROVEMENT_PLAN_2026-09-16.md`: 67 of 68 items done. The last one is now A6, and its deferred
  parts are C8, C9 and D5.
- `DOCS/automatic_downloads_phase2_plan.md`: all 70 boxes are unticked, but the work is done
  (`AutoSearchService` is wired up and has a strict-mode toggle).
- `TODO.md`: a historical roadmap from April 2026 (397 unticked boxes, mostly stale). Database backups
  are now B6.
- Older implementation plans from March to June 2026:
  - `CONNECTION_SEARCH_HARDENING_IMPLEMENTATION_PLAN.md`
  - `SEARCH_ENGINE_HEURISTIC_UPGRADE_PLAN.md`
  - `DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md`
  - `DOCS/ROADMAP_PROGRESS_AND_DOC_GAPS_2026-04-20.md`
  - `DOCS/memory/*_plan.md`
  - `.agent/memory/*_PLAN.md`
- The Claude plan-mode files `composed-exploring-dragonfly` (mix flow; only C1 is left) and
  `curried-brewing-valley` (library management; all done) were deleted.
