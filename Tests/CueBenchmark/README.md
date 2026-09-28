# Cue accuracy benchmark (plan item H0)

Regenerates ORBIT's auto cues with the real `Engine.Cueing.CueGenerationService` and compares them
with hand-placed Rekordbox cues. It is read-only, it is not part of the app build or the unit tests,
and it refuses to open the live `library.db`.

## Run

1. Snapshot the library. This is safe while ORBIT is running, because it uses SQLite's online backup
   and opens the live database read-only:
   ```
   python -c "import sqlite3; s=sqlite3.connect('file:C:/Users/<you>/AppData/Roaming/ORBIT/library.db?mode=ro', uri=True); d=sqlite3.connect('library_copy.db'); s.backup(d)"
   ```
2. Run the benchmark:
   ```
   dotnet run --project Tests/CueBenchmark -c Debug -- --db library_copy.db --reference rekordbox_cues.json --summary baseline.json
   ```
   - To read cues straight from Rekordbox instead of a snapshot, leave out `--reference`. Hand-placed
     cues only exist in a device export (`<drive>\PIONEER\USBANLZ`), so the export drive must be
     attached.
   - Add `--dump-reference file.json` to save those cues as a snapshot for later runs.
3. After changing detection code, run again with `--compare baseline.json` to see the deltas.
   Use `--trace "<part of a filename>"` to print one track's reference cues next to its generated cues.
4. Beat-grid work:
   - `--refit` re-fits BPM and grid from the stored beat ticks with `BeatGridFitter`, in memory only.
   - `--refit-downbeat` does the same and also re-picks the downbeat from the sub-bass events.
     This shows how the library will look after `BeatGridRecomputeService` runs.
   - `--fit-stats` dry-runs the fitter over every analysed track in the copy and prints timing and
     BPM distributions. No reference cues are needed.

## Reference: Rekordbox's own analysis (recommended)

`--rekordbox-analysis` (instead of `--reference`) reads Rekordbox's local analysis cache,
`%AppData%\Pioneer\rekordbox\share\PIONEER\USBANLZ`, which holds about 1,300 tracks. That gives
roughly 800 scorable tracks against ORBIT's library, versus 84 from hand cues.

For each track, it takes three things from the cache:
- **Grid and markers:** the PQTZ beat grid, with BPM and downbeats, and the PSSI phrase starts as
  markers.
- **Drops:** the start of each "Chorus" phrase run counts as a drop.
- **Circular tracks are skipped:** tracks where ORBIT already uses Rekordbox's phrases as input.

This is Rekordbox's automatic analysis, not a DJ's hand placement. But it's independent of ORBIT
and industry-standard, which makes it a good large-scale check. Use the hand-cue reference for
"what this DJ actually does".

Experiment switch: `ORBIT_BENCH_PHRASE_SOURCES=RekordboxPSSI` limits which stored phrase
sources may drive the phrase path. Leaving out `Heuristic` forces those tracks through the DSP path,
which makes it a test bed for DSP changes.

## Reference: your own drops (best for tuning DnB)

`--user-drops --min-cues 1` scores ORBIT's auto drops against every Drop cue you placed or edited
yourself, in Cue Forge or the Flow Builder cue editor, read from the library copy. This is the gold
reference: each drop you set by hand becomes a test case. The "by genre family" rows show the DnB
(breakbeat) numbers. Once a few dozen DnB tracks have hand-set drops, tune the detection against
this reference and keep only changes that improve it.

## What it measures

- **Grid alignment:** whether each hand cue lands on an ORBIT beat, bar line and 8-bar phrase line,
  where the grid is BPM + downbeat anchor, the grid that every generated cue is snapped to.
  - Random placement scores about 50% on-beat and about 12% on-bar.
  - A drop between the first 90 s and the part after 180 s means the tempo drifts.
- **Coverage:** the distance from each hand cue to the nearest generated cue.
- **Drops:** these are *inferred*: a pair of hand cues 8 bars apart is read as a countdown, with the
  drop 8 bars after the second cue.
  - **Caveat (2026-09-27):** in the current reference set, most hand cues are phrase markers every
    8 bars through the intro (bars 1/9/17/25/33), not countdowns. So this inference often "finds"
    drops that aren't there, and the drop numbers are **not trustworthy yet**.
  - A drop reference that doesn't depend on this inference is plan item H0b.
