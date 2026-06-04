# Library Sidebar Unification Closure Handoff (2026-05-29)

Status: Active closure handoff snapshot
Date: 2026-05-29
Scope: Sidebar/intelligence lane closure assumptions, guardrails, and verification anchors.

## Purpose

Provide a durable handoff snapshot for maintainers after the ownership-transfer and closure-hardening waves.

## Finalized Ownership Boundaries

1. Inspector payload routing is explicit and VM-owned:
- Single track: PlaylistTrackViewModel payload.
- Pair context: LibraryDoubleInspectorViewModel payload.
- Intelligence context: PlaylistIntelligenceViewModel payload.

2. Parent compatibility surface is minimized:
- Legacy sidebar mode state removed.
- Legacy double-inspector mirrors removed.
- Dead smart-insert shim wrappers removed.
- Dead forwarding no-op/dispose paths removed.

3. Child VM ownership is stable:
- LibraryDoubleInspectorViewModel owns pairwise inspector context.
- LibraryTrackInspectorViewModel owns explainability and similar-preview enrichment state.
- PlaylistIntelligenceViewModel owns intelligence tab/settings/candidate flows.

## Guardrail Set (Locked by Tests)

Primary contract suite:
- Tests/SLSKDONET.Tests/Architecture/LibrarySidebarUnificationStartTests.cs

Locked categories:
1. Positive template route contracts in MainWindow.axaml.
2. Negative legacy symbol guards for removed compatibility surfaces.
3. No service-locator usage in sidebar/intelligence lane.
4. No stale child-to-parent forwarding no-op paths.
5. Event routing assertions for explicit inspector payload ownership.
6. Selection-flow assertions for child-owned refresh/enrichment paths.

## Verified Focused Gate

Focused regression pack (latest):
- ProjectListViewModelSelectionSyncTests
- LibrarySidebarUnificationStartTests
- SidebarViewModelSyncTests
- SidebarAndPanelServiceTests
- LibraryViewModelPlaylistUpgradeCommandTests

Latest result at handoff: green (see RECENT_CHANGES.md for latest exact count).

## Archived Assumptions

1. Sidebar lane remains global-inspector driven (no local sidebar mode revival).
2. Backward compatibility is preserved only where active bindings/command call sites still require parent facade access.
3. Future cleanup should prefer deleting compatibility surfaces over adding new mirrors.
4. Any new inspector payload route must be explicit and test-locked in architecture contracts.

## Follow-On Maintenance Rules

1. If a removed compatibility symbol is reintroduced, add explicit rationale and update guardrail tests in the same change.
2. Keep docs/index alignment updated in:
- RECENT_CHANGES.md
- DOCS/CURRENT_AND_FUTURE_PLAN_SUMMARY.md
- DOCS/memory/library_sidebar_unification_plan.md
- DOCUMENTATION_INDEX.md

3. Run the focused gate whenever sidebar/intelligence routing, inspector payload contracts, or lane ownership surfaces change.

## Handoff Outcome

The lane is in closure mode with active guardrails, synchronized docs, and a maintainable ownership map.
