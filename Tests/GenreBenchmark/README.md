# Genre accuracy benchmark (epic #245, GA3 / #249)

Labels tracks from **trusted** genre metadata in a copy of the library database and scores ORBIT's
genre decision against them. It's read-only, isn't part of the app build or the unit tests, and
refuses to open the live `library.db`.

## Run

1. Snapshot the library. This is safe while ORBIT is running, because it uses SQLite's online backup
   and opens the live database read-only:
   ```
   python -c "import sqlite3; s=sqlite3.connect('file:///C:/Users/<you>/AppData/Roaming/ORBIT/library.db?mode=ro', uri=True); d=sqlite3.connect('library_copy.db'); s.backup(d)"
   ```
   (In PowerShell, write `${path}?mode=ro`. A bare `$path?mode` is parsed as one variable name.)
2. Run it:
   ```
   dotnet run --project Tests/GenreBenchmark -c Debug -- --db library_copy.db --summary baseline.json
   ```
3. After changing genre code, run it again with `--compare baseline.json` to see the deltas. Paste
   the compare block into the PR (every genre-accuracy child asks for it).

| Option | What it does |
|---|---|
| `--offline` (default) | Recomputes fusion from persisted rows with the app's own `GenreFusionEngine`: the Jamendo head re-run on the stored embedding (needs the models next to the exe, so run `git lfs pull` in a fresh clone) plus the stored Discogs-400 scores. Runs thousands of tracks in about a second. |
| `--stored` | Scores what is already in `AudioFeatures.DetectedSubGenre`, the as-shipped baseline. |
| `--summary out.json` / `--compare base.json` | Saves the metrics or prints the deltas against a saved run. |
| `--trace "<part>"` | Full evidence for up to 10 tracks whose artist, title or file name contains the text: truth and its source, BPM, Jamendo top 5, Discogs top 5, fused distribution. |
| `--misclassified N` | The N worst confident-wrongs. |
| `--confident 0.7` | Threshold for "confident" (default 0.7). |
| `--include-untracked` | Also label tracks whose genre has no provenance row. Off by default: older imports copied Spotify **artist** genres into the genre field. |

Skipped rows are counted and printed: not analysed, no embedding, or an embedding from another model
version.

## Ground truth

A track is labelled from `LibraryEntries.Genres` (or `PrimaryGenre`) when its `genre` provenance is:
- `user` (your own edit),
- `tags` (the file's tag; Beatport purchases carry Beatport genres),
- `beatport` with confidence ≥ 0.85, or
- `discogs` with confidence ≥ 0.85 and at most 3 genres (Discogs styles are release-level).

Each genre string goes through `GenreTaxonomy.MapFreeText`, the app's own mapping, so the benchmark
never duplicates mapping logic. A track is excluded when:
- its genre was written from a model suggestion: "Use as genre" or maintenance "Fill empty Genre
  from AI style" (circular);
- it has only junk genres ("Electronic", "Dance"…);
- its genres map to more than one bucket.

Counts per bucket and per source are printed. Dubstep and Techno can be tiny, and a bucket with
fewer than 20 tracks is flagged, so nobody over-reads its F1.

## What it measures

- Per bucket: precision, recall, F1, and the 5×5 confusion matrix plus a `none` column (no label).
- **Confident-wrong rate:** confidence ≥ 0.7 and wrong. This is the headline metric; a DJ forgives
  "unsure", not a confident lie.
- Coverage (any label / confident label) and the club-bucket macro-F1 (DnB, Dubstep, Techno, House).
- Breakdown by tempo band, using the stored BPM.
- A deterministic 80/20 hold-out split by track hash (FNV-1a), printed separately. GA11 (#256)
  trains on the 80 % and reports on the 20 %.

Not here yet: an `--audio` mode that decodes files for rhythm (#251) and pooling (#252)
experiments, and a Rekordbox-genre reference. Those issues add them when they need them.
