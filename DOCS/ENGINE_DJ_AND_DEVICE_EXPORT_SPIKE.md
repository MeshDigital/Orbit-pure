# DX4 Spike: Engine DJ (`m.db`) & Pioneer PDB / OneLibrary Export

**Status:** Spike completed (Epic #551 / #559). **No user-facing write path is shipped in this PR.**

---

## 1. Engine DJ (`Engine Library/Database2/m.db`)

### 1.1 Schema versions in the wild & detection
Engine DJ / Engine OS stores its database in SQLite at `<Drive>/Engine Library/Database2/m.db` (since Engine OS 2.0; legacy 1.x used separate `m.db` + `p.db` under `Engine Library/`).

Every Database2 `m.db` contains an `Information` table with a single row:

```sql
SELECT schemaVersionMajor, schemaVersionMinor, schemaVersionPatch, uuid FROM Information LIMIT 1;
```

| Engine DJ / OS generation | `schemaVersion` (`major.minor.patch`) | Notable changes |
|---|---|---|
| Engine Prime 1.x | `1.6.x` – `1.18.x` (split `m.db` + `p.db`) | Legacy 2-file layout; superseded |
| Engine DJ 2.0 – 2.4 | `2.18.0` – `2.20.x` | Unified `Database2/m.db`, `PlaylistEntity`, `Track.quickCues`, `Track.loops`, `Track.beatData` |
| Engine DJ 3.x / 4.x | `2.20.x` – `3.0.x` (schema evolves per Engine OS firmware) | Added stems/performance metadata columns, `Smartlist`, stricter foreign-key/trigger invariants |

### 1.2 Binary blob formats (`Track` columns)
Engine DJ stores beatgrids, hot cues, and loops as zlib-compressed big-endian binary blobs inside `Track` (`beatData`, `quickCues`, `loops`, `overviewWaveFormData`):
- **`beatData`:** 8-byte header (`double sampleRate`, `double samples`), followed by default & adjusted beatgrid marker arrays (`double beatNumber`, `double sampleOffset`, `int32 beatsUntilNextMarker`).
- **`quickCues`:** 8-byte cue count (`int64`), followed by 8 fixed slots (`uint8 labelLen` + UTF-8 label, `double sampleOffset`, `uint8 isSet`, `uint32 ARGB color`), then default main cue offset + `isMainCueSet`.
- **`loops`:** 8-byte loop count (`int64`), followed by 8 fixed slots (`uint8 labelLen` + UTF-8 label, `double startSample`, `double endSample`, `uint8 isStartSet`, `uint8 isEndSet`, `uint32 ARGB color`).

### 1.3 Prior art & licensing (`libdjinterop`)
- **`libdjinterop`** (C++, LGPL-3.0) documents Engine DJ `1.x` and `2.x` schemas and blob layouts.
- **Licensing rule for ORBIT:** `libdjinterop` may be consulted **for format documentation only** (clean-room C# implementation), NOT statically linked or copied verbatim unless ORBIT adopts LGPL compliance obligations.

### 1.4 Refuse-unknown-version rule (mandatory gate)
Any future Engine DJ writer MUST:
1. Refuse if Engine DJ desktop is running.
2. Check `(schemaVersionMajor, schemaVersionMinor, schemaVersionPatch)` against an explicit allow-list of tested versions; **refuse with a clear "Unsupported Engine DJ schema version X.Y.Z" error** for anything outside the allow-list.
3. Copy `m.db` to a timestamped backup before opening read-write, run inside one transaction, and verify `PRAGMA integrity_check;` before commit.
4. Never test against a user's real Engine library or USB stick — copies only.

---

## 2. Pioneer Device Export (PDB / ANLZ & OneLibrary)

### 2.1 Formats in the wild
- **Legacy Device Library (`PIONEER/rekordbox/export.pdb` + `PIONEER/USBANLZ/.../*.DAT, *.EXT, *.2EX`):**
  - Custom fixed-page binary B-tree (`export.pdb`, documented by Deep Symmetry's `crate-digger` / `dysentery`).
  - Per-track cue/beatgrid/waveform files (`ANLZxxxx.DAT` / `.EXT` / `.2EX`). Note: ORBIT already has a **read-only** `RekordboxAnlzParser` (`Services/Rekordbox/RekordboxAnlzParser.cs`).
- **Device Library Plus / OneLibrary (`PIONEER/rekordbox/exportLibrary.db`):**
  - SQLCipher-encrypted SQLite database introduced for OPUS-QUAD, OMNIS-DUO, XDJ-AZ, and newer CDJ firmware.
  - Requires keeping both `export.pdb` (for CDJ-2000NXS2 / CDJ-3000) and `exportLibrary.db` (for Device Library Plus hardware) synchronized, plus exact ANLZ binary sidecars.

### 2.2 Risk assessment
| Target | Format stability | Corruption blast radius | Hardware verification needed | Maintenance cost |
|---|---|---|---|---|
| **Engine DJ `m.db`** (`2.20.x`) | Medium (SQLite + documented zlib blobs) | High if unbacked-up; medium with strict version allow-list + backup | Denon Prime / Engine DJ Desktop test copy | Medium |
| **Pioneer PDB + ANLZ** | Low (custom binary page format; firmware-sensitive) | **Critical** (corrupt USB fails silently on CDJ at a gig) | CDJ-2000NXS2 + CDJ-3000 hardware | Very High |
| **Pioneer OneLibrary (`exportLibrary.db`)** | Low (encrypted SQLite; proprietary key/schema churn) | **Critical** | OPUS-QUAD / XDJ-AZ hardware | Very High |

---

## 3. Go / No-Go Verdict

| Target | Verdict | Rationale & Next Step |
|---|---|---|
| **Engine DJ `m.db` (version-pinned `2.20.x`)** | **CONDITIONAL GO (future issue)** | Viable as a clean-room C# writer gated by an explicit `schemaVersion` allow-list, mandatory backup, and `PRAGMA integrity_check`. Defer implementation until a real Engine DJ desktop/controller copy is available to verify round-trip cue/grid loading. |
| **Pioneer PDB / ANLZ direct USB write** | **NO-GO** | Rekordbox XML import (+ opt-in `master.db` cues in PR #507) already gives DJs a 100% firmware-safe path through Rekordbox's own Sync Manager onto CDJs. Writing raw `export.pdb` pages risks bricking a gig USB. |
| **Pioneer OneLibrary (`exportLibrary.db`)** | **NO-GO** | Proprietary encrypted format with active schema churn; Rekordbox remains the sanctioned sync bridge. |
