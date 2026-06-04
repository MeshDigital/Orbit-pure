# Library Sidebar Unification Guardrail Drift Watch (2026-05-29)

Status: Post-closure drift watch
Date: 2026-05-29
Scope: Ongoing regression signals and trigger points for follow-on feature work touching the sidebar/intelligence lane.

## Purpose

Provide a lightweight watchlist for future changes that could silently erode the stabilized sidebar/intelligence closure boundaries.

## Drift Watch Targets

1. Inspector payload route changes in `LibraryViewModel.Events`.
2. Reintroduction of parent compatibility mirrors in `LibraryViewModel`.
3. Service-locator lookups returning to sidebar/intelligence child viewmodels.
4. Documentation index drift that removes closure artifact discoverability.
5. New comments or code paths that restore migration-era wording instead of durable ownership language.

## Watch Triggers

1. Any edit to `LibraryViewModel.Events.cs`, `LibraryViewModel.cs`, `LibraryViewModel.Commands.cs`, `LibraryDoubleInspectorViewModel.cs`, `LibraryTrackInspectorViewModel.cs`, or `PlaylistIntelligenceViewModel.cs`.
2. Any inspector template change in `Views/Avalonia/MainWindow.axaml`.
3. Any doc edit that adds, renames, or replaces closure artifacts in `DOCS/`.

## Required Response

1. Re-run the focused sidebar closure gate.
2. Update architecture tests when route contracts or discoverability artifacts change.
3. Update `DOCUMENTATION_INDEX.md` and `DOCUMENTATION_STATUS.md` in the same batch as any new closure artifact.
4. Record any new closure-stage lesson in repo memory for future planning loops.

## Current Baseline

Focused gate baseline at creation: 42 passed, 0 failed.
