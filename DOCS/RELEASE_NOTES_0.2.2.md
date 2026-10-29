# ORBIT 0.2.2 — release notes

**Released:** 2026-10-29 · **Previous public build:** 0.2.1 (2026-10-29) · **Runtime:** win-x64,
self-contained single file.

0.2.2 is a hardening + prep-quality release on the 0.2 early-stable line. It rounds out the
cue-preparation workflow — a tempo override in grid repair, a "Clear all cues" that actually resets the
inspector, and playlist-scoped force re-analysis — and closes the data-correctness gaps called out after
0.2.1: duration-mismatch detection, spectral (fake-lossless) scanning on library imports, and a complete
library-entity round-trip. It also ships the security-hardening batch and the playlist-order fix.

Install over 0.2.1 (same per-user AppId, no uninstall needed). `%APPDATA%\ORBIT` is untouched. One new
column is added to the database on first run (see *Schema*).

---

## Cue Forge: correct a wrong tempo in grid repair

Cue Forge's grid panel showed the detected BPM read-only, so a track detected at the wrong tempo (a
half/double-time mistake, or an analysed edit) could not be corrected in place.

- **×2 and ÷2** buttons rescale the grid for half/double-time detections.
- A **manual BPM field** sets the tempo directly.
- The correction persists via `UpdateBpmAsync`, which writes `ManualBPM`, so it **survives re-analysis**
  instead of being overwritten by the next detection.

## Cue Forge: "Clear all cues" now really clears the slate

Clearing every cue left the inspector stuck on a ghost selection: the right-hand panel kept showing the
selected cue editor (with a cue that no longer existed) instead of returning to the AUTO-GENERATE panel,
so **Auto-generate** was effectively hidden until you clicked elsewhere.

- **Clear cues & reset view** (and any regenerate that removes the selected cue) now clears the
  selection, so the inspector falls back to the AUTO-GENERATE panel and you can immediately generate a
  fresh set.
- Cue Forge also stays attached to the **playing** track after an automatic crossfade advance, instead of
  showing the previous track.

## Duration mismatch: flag a file that isn't the track it matched

A match whose file is a slowed/sped edit, an excerpt, or a wrong-master can be minutes off the canonical
duration it claimed. Detection only probed files with *unknown* duration, so a known-duration match was
silently accepted.

- The download pipeline now always probes the file and logs a clear warning when the actual length
  diverges from `CanonicalDuration` by more than ~5%.
- The library **inspector** surfaces that mismatch ("file 1:53 vs matched track 2:40 — likely a mix/edit").

## Spectral scan now runs on library imports, not just downloads

`PostDownloadSpectralScanService` only listened to download completion, so an **imported** FLAC that is
actually an AAC→FLAC transcode ("fake lossless") was never flagged. It now subscribes to
`LibraryEntriesAddedEvent`, scans newly imported lossless files, and writes the verdict
(`Integrity` / `IsTranscoded` / `QualityDetails`) back to the library entry.

## Analysis page: force re-analyse a whole playlist

Next to the "run missing" action there is now **Force re-analyse**: queue every track in the selected
playlist for a full re-analysis, bypassing the "already analysed" completeness check. Use it after a
detection-pipeline change, or whenever one set's stored analysis looks stale.

## Playlist: one canonical track order

Reorder-for-flow and Auto-Arrange could leave duplicate `SortOrder` positions behind, so the grid, Cue
Forge and the player could each show a different sequence. Reads are now deterministic and the order is
renumbered 1..N on the next reorder, so every view agrees on one order.

## Library data integrity: the entity round-trip no longer blanks columns

`PlaylistTrackToEntity` didn't map several columns (`CuePointsJson`, `AudioFingerprint`, `Danceability`,
`Arousal`, spectral columns, …), but whole-row saves upserted the entire row — silently resetting those
columns to their defaults. The mapping is now complete (and order-only writes stay order-only), so a
save no longer loses data it didn't mean to touch.

## Security hardening

- **Incoming chat images** are validated before display, and local file paths are no longer leaked.
- **Download paths** are contained within the download directory.
- The **Spotify client secret** is DPAPI-encrypted in `config.ini` instead of stored in plain text.
- **External links** open only through a validated `http(s)` helper.
- **SQLitePCLRaw** and **Tmds.DBus.Protocol** are pinned past the patched transitive versions
  (CVE-2025-6965 / CVE-2026-39959).

## Schema

One new nullable column is added on first run via `SchemaMigratorService` (raw SQL, no EF migration):
`ManualBPM` on the track entity. Existing installs migrate automatically; no data is touched.

---

## Verification

- `dotnet build` (Debug and Release): **0 errors** (38 pre-existing warnings).
- New coverage for the tempo override, the clear-command selection reset, the spectral-scan-on-import
  path, duration capture, the playlist order and the entity round-trip.

## Known issues / limitations

- **Existing auto cues are not migrated** (carried from 0.2.1): tracks cued by an older build keep their
  positions until **Auto-generate** runs again.
- **Downbeat phase can be off by a few beats** on some tracks; bar numbers and manual bar-snapping depend
  on it. Use **Bar 1 here** (and now the ×2 / ÷2 / manual BPM override) to correct a track, then re-run
  Auto-generate.
- **The update check contacts GitHub** (one unauthenticated GET, at most once a day); opt out with the
  existing `EnableUpdateCheck` setting.

## Artifacts

| File | What it is |
|---|---|
| `artifacts/ORBIT-Setup-0.2.2-win-x64.exe` | Per-user installer (no admin prompt), shortcuts, uninstaller |
| `artifacts/ORBIT-0.2.2-win-x64.zip` | Portable package (same publish output) |
| `artifacts/ORBIT-0.2.2-win-x64-manifest.txt` | Version, runtime, configuration, publish timestamp |

Install and uninstall both leave `%APPDATA%\ORBIT` (library database, config, fingerprints, stems)
untouched, so reinstalling never costs you your library.
