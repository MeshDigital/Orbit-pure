# Rekordbox Export Pipeline — Architecture Reference

Status: Authoritative technical reference
Date: 2026-08-10
Scope: Rekordbox XML export, colour tags, hot loops/cues, tempo grid derivation, merge-mode re-export

## Why this exists

As of mid-2026, three parallel Rekordbox-XML code paths existed in the codebase; only one was
actually reachable from the UI. A second, architecturally nicer implementation was registered in
DI but never wired to navigation — fully dead. A third had been deleted in a prior refactor, but
a 330-line test file referencing it survived (excluded from compilation, silently). This document
describes the single, real, consolidated pipeline that replaced all three (commit `200e75e`,
2026-07-31) plus the merge-mode re-export shipped on top of it (commit `6363ee8`, 2026-08-01).

**Explicitly out of scope, by deliberate decision:** Traktor/Serato export-back (those importers
stay one-directional into ORBIT), and real CDJ-native Device Library Plus / PDB+ANLZ binary
device export (reverse-engineering an encrypted binary format with no .NET precedent). USB
export is "files + a correct, complete `rekordbox.xml`" for desktop re-import — not a native
device-DB write.

## Canonical components

1. [Services/Library/PlaylistExportService.cs](../Services/Library/PlaylistExportService.cs) — the single real export entry point
2. [Services/Library/Rekordbox/TempoGridDeriver.cs](../Services/Library/Rekordbox/TempoGridDeriver.cs) — pure logic, no DI, beatgrid → `TEMPO` node derivation
3. [Services/Library/Rekordbox/RekordboxXmlMerger.cs](../Services/Library/Rekordbox/RekordboxXmlMerger.cs) — pure `XDocument`-to-`XDocument` reconciliation, no DB access
4. `Utils/GuidGenerator.cs` — `CreateStableIntFromSeed`, the stable-`TrackID` basis

## Export flow

```
Playlist selection
   │
   ▼
PlaylistExportService.ExportToRekordboxXmlAsync
   │
   ├─ per track: build <TRACK> node
   │    ├─ Name/Artist/Album/Genre/Kind/Size/TotalTime/BitRate/SampleRate — from file/library metadata
   │    ├─ Rating/Comments/ColorTag — from the track's own DB fields (no longer synthesized)
   │    ├─ TrackID — GuidGenerator.CreateStableIntFromSeed(TrackUniqueHash), collision-guarded
   │    ├─ Location — file path, percent-encoded (drive-letter segment exempted from naive escaping)
   │    ├─ POSITION_MARK nodes — from CuePointEntity rows + CuePointsJson (50ms dedup window);
   │    │    a cue on a hot-cue pad also gets a memory-cue duplicate at the same position
   │    └─ TEMPO nodes — via TempoGridDeriver (see below)
   │
   ├─ nested <NODE> playlist folders — projected from the existing PlaylistFolder/FolderId
   │    hierarchy; degrades with a logged warning (not a throw) on a missing/cyclic folder chain
   │
   ▼
   If target rekordbox.xml already exists → RekordboxXmlMerger.Merge(...) (see below)
   Else → doc.Save(path)
```

## Tempo grid derivation

Previously `Inizio` (the grid start offset) was hardcoded to `"0.000"` for every track, even
though ORBIT already computes real beat-tick timestamps (`AudioFeaturesEntity.BeatGridJson`) and
a real downbeat offset (`DownbeatOffsetSeconds`) during analysis — they were simply never read
by the exporter.

`TempoGridDeriver` now:
- Always uses the real downbeat offset for `Inizio`.
- Derives **multiple tempo anchors** only for tracks with genuine detected tempo drift, gated by
  the existing `BpmStability < 0.7` convention (already used elsewhere in the codebase) — the
  common stable-tempo case still emits a single anchor.

All numeric XML values (`Bpm`, cue `Start`/`End`) are formatted with
`CultureInfo.InvariantCulture` — a real locale bug was caught by the test suite where a
comma-decimal machine locale would have written `"15,000"` instead of `"15.000"`, silently
corrupting the XML.

## Merge-mode re-export

Re-exporting a playlist to a path that already has a `rekordbox.xml` merges into it instead of
overwriting it, wired transparently into `PlaylistExportService.ExportToRekordboxXmlAsync`
right before the final save — no new UI, no signature change, every existing call site benefits
automatically.

**Why:** a user exports → imports into Rekordbox → rates/colours/cues tracks there → asks ORBIT
to re-export after new downloads finish. The pre-merge exporter would silently destroy all of
that Rekordbox-side editing on re-export.

**Field-ownership split** (confirmed with the project owner as the core design decision):

| Field | Rule |
|---|---|
| `TrackID`, `DateAdded` | Always preserve existing on match (protects other playlists' `<TRACK Key>` references elsewhere in the file) |
| `Rating`, `Colour`, `Comments`, all `POSITION_MARK` cues | Preserve existing if present, else fill from ORBIT (all-or-nothing per track for cues) |
| `Name`/`Artist`/`Album`/`Genre`/`Kind`/`Size`/`TotalTime`/`BitRate`/`SampleRate`/`AverageBpm`/`Tonality`/`Location`/`TEMPO` | Always refresh from ORBIT — file-derived or analysis-owned data |

**Matching**:
- Track: `TrackID` first, `Location` fallback.
- Playlist node: walk a root-to-leaf name chain (`["ROOT", ...folders, playlistName]`) in both
  the existing and fresh `<PLAYLISTS>` trees, replacing only the matched leaf node — every
  sibling node/folder is left byte-for-byte untouched.
- A malformed or foreign existing file at the target path falls back to a full overwrite,
  logged, never throws.

**Known, documented limitation:** renaming or moving a playlist between ORBIT folders is not
recognized as "the same playlist" on merge — the folder chain won't match, so a duplicate node
is created and the old one orphaned. Real Rekordbox's own internal renumbering behavior on a
genuine import→re-export round trip has not been verified against a real Rekordbox install.

## Colour tags

`ColorTag` is a real nullable `INTEGER` column (added via the standard `SchemaMigratorService`
raw-SQL patch convention), editable through a "Set Colour" context-menu submenu (8 Rekordbox
swatches + clear) on any track, with bulk-selection support. It round-trips through both the
first-export and merge-mode paths described above.

## Related

- [RECENT_CHANGES.md](../RECENT_CHANGES.md) — 2026-07-31 and 2026-08-01 entries have the
  original implementation narrative, test coverage list, and verification notes
- [ARCHITECTURE.md](../ARCHITECTURE.md) → "Rekordbox Export Pipeline" — condensed summary version of this document
