# Library Sidebar Unification Closure Maintenance Anti-Patterns (2026-05-29)

Status: Active warning note
Date: 2026-05-29
Scope: Capture the common failure modes that make the closure-maintenance archive harder to trust.

## Anti-Patterns

1. Publishing new artifacts without adding them to the documentation-index contract.
2. Rolling the queue forward before the current batch is validated.
3. Letting repo memory become a duplicate of RECENT_CHANGES.md.
4. Writing multiple docs that differ only by title wording.
5. Expanding cross-links until the quick-start surface becomes noisy.
6. Treating planned slices as completed work in recap docs.

## Correction Pattern

When any anti-pattern appears, stop the wave, restore contract/index/status alignment, then continue with the next batch.
