# Library Sidebar Unification - Grouping Rollback Impact Scorecard (2026-05-30)

## Purpose

Provide a fast, repeatable scorecard for assessing the operational impact of grouping rollback decisions in the sidebar unification documentation lane.

## Scoring Dimensions

- Discoverability impact (index clarity, quick-start scan speed)
- Contract impact (assertion additions/removals and risk)
- Maintenance impact (future batch overhead)
- Continuity impact (restart/handoff complexity)
- Drift risk (chance of duplicate or orphan artifacts)

## Score Bands

- 0-2: Low impact, safe to apply in current wave
- 3-5: Moderate impact, requires status + recap update in same batch
- 6-8: High impact, requires explicit signoff note and focused gate rerun
- 9-10: Critical impact, defer unless required by blocking contract drift

## Usage Rule

Apply the scorecard before introducing rollback-oriented artifact groups and record the resulting actions in RECENT_CHANGES and plan memory docs.
