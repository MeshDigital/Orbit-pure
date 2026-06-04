# Library Sidebar Unification Validation Reporting Shorthand Guide (2026-05-29)

Status: Active reporting guide
Date: 2026-05-29
Scope: Standardize compact wording for focused-gate results in recap surfaces.

## Shorthand Pattern

- `Focused gate: 42/0` means 42 passed, 0 failed in the target suite set.
- Always include the suite family once per recap block.
- Mention warnings only if they changed or blocked closure.

## Recommended Recap Line

`Focused gate remained green (42 passed, 0 failed) after contract/index/status synchronization.`

## Why

Consistent shorthand improves scan speed across long-running closure-maintenance loops.
