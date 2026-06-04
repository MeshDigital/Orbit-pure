# Library Sidebar Unification Ownership Map (2026-05-29)

Status: Ownership map
Date: 2026-05-29
Scope: Quick reference for which component owns which closure-era responsibilities.

## Ownership Map

1. `LibraryViewModel`: coordination facade and compatibility bridge only where still required.
2. `LibraryDoubleInspectorViewModel`: pairwise inspector context and selected-pair state.
3. `LibraryTrackInspectorViewModel`: explainability state, similar preview enrichment, and single-track inspector enhancements.
4. `PlaylistIntelligenceViewModel`: intelligence tabs, settings, candidate flows, and smart insert state.
5. `LibrarySidebarUnificationStartTests`: architecture contract anchor for closure discoverability and no-regression checks.
6. `DOCUMENTATION_INDEX.md`: discoverability anchor for current closure artifacts.
