# Auto-mix benchmark (#599, epic #596)

Scores the transitions real playback would play, for every consecutive pair of downloaded tracks in
your playlists. It uses the same code playback uses:
- `PlaybackTransitionDecider`, which chains `TransitionResolver` and `TransitionPlanService` into
  `TransitionPlanner`;
- the player's own timing rules: `AudioPlayerService.LatestMixStart`, the transition length, and
  `TempoSyncRatio`.

The benchmark isn't part of the app build or the unit tests. It runs against a **copy** of the
library database and refuses to open the live `library.db`.

This is Part A: the plan level, with no audio rendered. Part B (rendered audio) comes with MX1 (#600).

## Run

1. Snapshot the library. This is safe while ORBIT is running, because it uses SQLite's online backup
   and opens the live database read-only:
   ```
   python -c "import sqlite3; s=sqlite3.connect('file:C:/Users/<you>/AppData/Roaming/ORBIT/library.db?mode=ro', uri=True); d=sqlite3.connect('library_copy.db'); s.backup(d)"
   ```
2. Run it:
   ```
   dotnet run --project Tests/MixBenchmark -- --db library_copy.db --summary baseline.json
   ```
3. After changing planning or playback code, run it again on the **same** copy with
   `--compare baseline.json` to see the deltas.
   - `--worst 20` lists the beat-matched pairs with the largest phase error, with both tracks' grid,
     intro and drop data.
   - `--trace "<artist or title>"` prints every pair whose outgoing track matches.

## What it measures

- **path.\***: how each pair mixes.
  - `Saved`: your own transition, or the pair's earlier mix.
  - `Planned`: the structure plan.
  - `EchoOutFallback`: a pair that isn't mix-ready.
- **fallback.\***: why a pair fell back to echo-out: no analysis, no beat grid, or a tempo jump over 6 %.
  `tempo_jump_but_half_double` counts the jumps that the player's tempo sync would fold, such as
  87 vs 174 BPM.
- **clamped.pct**: how often `LatestMixStart` moved the mix-out earlier so the transition fits
  before the end of the track. A clamped start is usually off the grid.
- The grid scores below cover **beat-matched mixes only**. An echo-out doesn't try to line beats up.
  - **start.\* / entry.\***: whether the mix-out point (outgoing track) and the mix-in point
    (incoming track) sit within 10 ms of a real beat tick, on a bar line, or on an 8-bar phrase line.
    Bars and phrases are counted from the track's first downbeat.
  - **phase_error.\***: how far apart the two decks' beats are at the moment the incoming deck starts
    (median, p95, share over 20 ms). Above about 20 ms, kicks audibly flam.
  - **bars_aligned.pct**: whether the two downbeats line up, not just the beats.
- **tempo.\*** and **key.\***: the pairs' tempo and Camelot relations. These are reported only, and
  never shown in the app.

## Not measured here

- The actual timing of two output devices.
- The 50 ms control loop.
- Loudness across the overlap.

All three need rendered audio: that's Part B, with MX1 (#600).

## Baseline (2026-11-27)

4,646 pairs in 43 playlists; 3,158 of them are beat-matched with both grids.

- **Paths:** 67.9 % Planned (Drop Sync 54.2 %, Relaxed 11.2 %, Rolling 2.6 %) and 32.0 % echo-out.
  Almost all echo-outs are tempo jumps (31.5 %), and 6.6 % of all pairs are half/double-time jumps.
- **Before the incoming-entry fix in the same PR:**
  - entry on a bar: 94.1 %;
  - bars aligned: 95.9 %;
  - phase error: p95 39.4 ms, and 5.7 % of mixes over 20 ms.
- **After the fix:**
  - entry on a bar: 99.1 %;
  - bars aligned: 98.6 %;
  - phase error: p95 5.0 ms, and 2.0 % over 20 ms.

  Most of the remaining 2 % are Drop Sync mix-outs that sit 240–280 ms off the real beat ticks.
  These tracks don't fit the planner's constant-BPM grid (see #601).
