# Library Sidebar Unification Maintenance Cadence (2026-05-29)

Status: Maintenance cadence note
Date: 2026-05-29
Scope: Suggested cadence for closure-maintenance checks and focused validation.

## Cadence

1. On every lane-affecting change: run the focused closure gate.
2. On every new closure artifact: update documentation index, status, and the architecture index-contract test in the same batch.
3. During larger documentation sweeps: verify the closure artifact chain remains present in quick-start references.
4. During future refactors: review the ownership map and drift watch before merge.

## Focused Gate

1. ProjectListViewModelSelectionSyncTests
2. LibrarySidebarUnificationStartTests
3. SidebarViewModelSyncTests
4. SidebarAndPanelServiceTests
5. LibraryViewModelPlaylistUpgradeCommandTests
