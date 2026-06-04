# Library Sidebar Unification Archive Discoverability Regression Examples (2026-05-29)

Status: Reference examples
Date: 2026-05-29
Scope: Show the kinds of regressions the closure-maintenance process is designed to prevent.

## Example Regressions

1. A new artifact exists in DOCS but is missing from DOCUMENTATION_INDEX.md.
2. The architecture contract omits a recently added file, so discoverability drift becomes silent.
3. RECENT_CHANGES.md records a batch but the plan docs still point to the previous queue.
4. Repo memory logs a one-off narrative instead of the tactic that should be reused later.
5. Multiple docs cover the same topic without a clear supersession hint.

## Prevention

The contract-first workflow catches most regressions before they become archival confusion.
