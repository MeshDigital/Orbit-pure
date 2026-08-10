# 📚 Documentation Status

**Updated**: 2026-08-10

---

## 2026-08-10 sprawl cleanup

A prior autonomous documentation pass (dated 2026-05-29 through 2026-05-31) spiraled into
generating meta-documentation about its own documentation process for a single UI change
(the library sidebar/intelligence-panel unification). It produced **219 files** under
`DOCS/LIBRARY_SIDEBAR_UNIFICATION_*` — governance checklists, drift trackers, escalation
rubrics, annotation-consistency lint guides — describing process for a change that itself
has no standalone architecture doc. Alongside it, `.agent/queues/` accumulated **131** stale
per-task queue files (`LI-001.md`…`LI-120.md`, `P3-RI-001.md`…`P3-RI-010.md`, `A10-001.md`)
from completed autonomous-loop task batches.

None of this was real reference documentation — it was process exhaust that made the actual
docs harder to find (`DOCUMENTATION_INDEX.md` had grown to 750+ lines, most of it dead links
into this chain). All 350 files were removed; full history is preserved in git (`git log --
DOCS/LIBRARY_SIDEBAR_UNIFICATION_*` / `.agent/queues/`) if any of it is ever needed. See
[DOCUMENTATION_INDEX.md](DOCUMENTATION_INDEX.md) for the current, link-verified doc set.

`DOCS/memory/`, `DOCS/recaps/`, and `DOCS/discoverability/` were left in place — unlike the
sidebar-unification chain, these contain real per-feature investigation and completion notes
(dated, but substantive), not repeated process scaffolding about the scaffolding.

## Current core-doc status

| Document | Status |
|---|---|
| [RECENT_CHANGES.md](RECENT_CHANGES.md) | ✅ Current — chronological, detailed, updated per release. The most reliable doc in the repo. |
| [README.md](README.md) | ✅ Current as of 2026-08-10 |
| [FEATURES.md](FEATURES.md) | ✅ Refreshed 2026-08-10 — added AI genre/mood, social/chat layer, VBR fraud detection, Rekordbox merge export, colour tags |
| [ARCHITECTURE.md](ARCHITECTURE.md) | ✅ Refreshed 2026-08-10 — added Social/Serving Layer, Download Quality Verification, and Rekordbox Export Pipeline sections |
| [DOCS/SOCIAL_LAYER_ARCHITECTURE.md](DOCS/SOCIAL_LAYER_ARCHITECTURE.md) | 🆕 New 2026-08-10 — full deep-dive, previously undocumented |
| [DOCS/REKORDBOX_EXPORT_ARCHITECTURE.md](DOCS/REKORDBOX_EXPORT_ARCHITECTURE.md) | 🆕 New 2026-08-10 — full deep-dive, previously only covered piecemeal in RECENT_CHANGES.md |
| [DOCUMENTATION_INDEX.md](DOCUMENTATION_INDEX.md) | ✅ Rewritten 2026-08-10, every link verified against tracked files |

## Known remaining gaps

These are real systems with working code but no standalone technical write-up. Not blocking,
but worth closing when someone has time:

- `DATABASE_SCHEMA.md` — doesn't exist; the schema is currently only discoverable by reading
  `Data/Entities/*.cs` and the raw-SQL patches in `Services/Data/SchemaMigratorService.cs`.
- Search/Download orchestrator (adaptive lanes, hedged search+download, `SearchScope`) has no
  standalone architecture doc — closest is [DOWNLOAD_CENTER_ARCHITECTURE.md](DOWNLOAD_CENTER_ARCHITECTURE.md),
  which predates the current orchestrator design.
- Corruption scanning / audio ingestion pipeline (`AudioIngestionPipeline`,
  `AudioCorruptionScannerService`, `LibraryCorruptionScanService`) is documented only in
  `RECENT_CHANGES.md` changelog entries, not as a standing reference.

## Maintenance going forward

- Update [RECENT_CHANGES.md](RECENT_CHANGES.md) with every feature-sized change (this has
  been happening reliably — keep doing it).
- When a change is architecturally significant (new service layer, new external integration,
  new DB tables), add or update the relevant section in `ARCHITECTURE.md` in the same pass —
  don't let it accumulate as changelog-only knowledge.
- Do not create new dated/versioned documentation files that supersede a "previous version"
  of themselves (the pattern that caused the 2026-08-10 cleanup). Update the existing doc in
  place instead.
