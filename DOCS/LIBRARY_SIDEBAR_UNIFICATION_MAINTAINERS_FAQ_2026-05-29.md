# Library Sidebar Unification Maintainers FAQ (2026-05-29)

Status: Maintainer FAQ
Date: 2026-05-29
Scope: Common closure-maintenance questions for the stabilized sidebar/intelligence lane.

## FAQ

### Where should maintainers start?
Start with `DOCUMENTATION_INDEX.md`, then use `LibrarySidebarUnificationStartTests` as the contract anchor for active closure expectations.

### What gate should run before merge?
Run the focused sidebar closure regression pack:
1. ProjectListViewModelSelectionSyncTests
2. LibrarySidebarUnificationStartTests
3. SidebarViewModelSyncTests
4. SidebarAndPanelServiceTests
5. LibraryViewModelPlaylistUpgradeCommandTests

### What if a new closure artifact is added?
Update `DOCUMENTATION_INDEX.md`, `DOCUMENTATION_STATUS.md`, the rolling plan docs, and the documentation-index architecture contract in the same change.

### What if a historical closure doc needs revision?
Prefer adding a new dated artifact that supersedes prior guidance rather than rewriting dated closure history in place.

### What changes are considered high-risk for drift?
Inspector routing, template mappings, compatibility surface reintroduction, service-locator usage, and any closure artifact rename/removal.
