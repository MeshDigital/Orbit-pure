# Phase3 Routing Discoverability

Status: Active
Last Updated: 2026-05-31

## Driver
- .agent/workflows/PHASE3_ROUTING_DRIVER.md

## Active Slice Queue
- P3-RI-001 through P3-RI-010 (all completed)

## Latest Completed Slice
- P3-RI-010 (Completed): final sweep and phase checkpoint report for the routing lane.

## Event Flow Map (P3-RI-001)
- Library and download/search contexts emit OpenInspectorEvent with contextual payload VM.
- MainViewModel listens for OpenInspectorEvent and routes panel payload through RightPanelService.OpenPanel.
- MainViewModel now detects route transitions and emits CloseInspectorEvent for non-player contextual payloads.
- RightPanelService.ClosePanel routes to fallback player VM when contextual payload is active.

## Fallback Hardening Map (P3-RI-002)
- Sidebar inspector ContentControl now includes an explicit default DataTemplate fallback for unsupported payload view models.
- The fallback pane provides deterministic user recovery with a Show Now Playing command.
- Both desktop and tablet inspector template surfaces carry the same fallback behavior.

## Source Attribution Map (P3-RI-003)
- OpenInspectorEvent now carries a Source field with Unknown-safe default normalization.
- Active emitters stamp source identifiers (Library selection/project context, Search single selection, Downloads single selection, FlowBuilder transition inspector).
- MainViewModel logs normalized source attribution at inspector-open routing point.

## Responsive Threshold Map (P3-RI-004)
- MainWindow SplitView display mode is now applied from the shared width-threshold resolver instead of a hardcoded inline mode.
- MainWindow resize handler now applies one width signal to mobile/tablet flags and SplitView display mode selection.
- MainViewModel threshold resolver enforces Overlay below 1024 and Inline at/above 1024.

## Stale Payload Guard Map (P3-RI-005)
- MainViewModel now evaluates inspector-open source IDs against the active page before applying RightPanelService mutations.
- Route families are gated explicitly: `Library.*` -> Library, `Search.*` -> Search, `Downloads.*` -> Projects, `FlowBuilder.*` -> creative workstation/decks/timeline/stems pages.
- Unknown or external source IDs remain pass-through to preserve compatibility while preventing stale delayed opens from prior pages.

## Presentation Defaults Map (P3-RI-006)
- OpenInspectorEvent now owns shared title/icon resolution via a single source-driven helper and factory.
- Active emitters now pass source-only identifiers so title/icon defaults are selected centrally instead of repeated per call site.
- Resolver defaults map Library double, single, and intelligence payloads plus Search/Downloads/FlowBuilder inspector sources to stable titles/icons, with generic inspector defaults for unknown sources.

## Null Payload Guard Map (P3-RI-007)
- MainViewModel now ignores null inspector payloads before calling RightPanelService.OpenPanel, preventing an invalid open state.
- Invalid payload routing remains covered by the MainWindow fallback DataTemplate contract for unsupported view model types.
- The listener path now has explicit regression coverage for null payload handling plus architecture assertions for the guard location.

## Download Group Metadata Map (P3-RI-008)
- DownloadCenterViewModel still groups by `SourcePlaylistId ?? PlaylistId`, but the regression now exercises the real grouped track path end-to-end.
- DownloadGroupViewModel resolves title, subtitle, and artwork directly from the first grouped track so the downloads template renders source-playlist metadata consistently.
- The new tests lock source-playlist naming plus project-selection fallback at the template boundary, preventing inspector-facing metadata drift.

## Routing Hardening Decisions (P3-RI-009)
- P3-RI-004: responsive width-threshold hardening keeps SplitView display mode aligned with the 1023/1024 boundary.
- P3-RI-005: page-aware inspector eligibility guards prevent stale cross-route opens from mutating the right panel.
- P3-RI-006: shared presentation defaults now resolve from a single source-driven factory rather than duplicated caller literals.
- P3-RI-007: null inspector payloads are blocked before panel mutation, while unsupported payloads still fall back through MainWindow.
- P3-RI-008: download-group metadata is resolved from the grouped track path so template output stays stable.
- The recap trail for these decisions lives in [DOCS/recaps/PHASE3_ROUTING_RECAP_PACK.md](../recaps/PHASE3_ROUTING_RECAP_PACK.md) and the dated agent recap.

## Phase Checkpoint (P3-RI-010)
- All routing slices P3-RI-001 through P3-RI-010 are now completed and linked through the recap pack and agent recap.
- The final checkpoint preserves the decision trail from route-transition hardening through download-group metadata stabilization.
- Future routing work should start from this page, the recap pack, and the queue history rather than rediscovering the earlier lane state.

## Identified Failure Point and Delta
- Failure point: contextual inspector payload could remain active across route changes, producing stale panel context.
- Delta: route-transition close guard in MainViewModel with explicit player fallback exclusion and helper-backed tests.
- Failure point: unsupported inspector payload type rendered as an empty/blank pane when no matching DataTemplate existed.
- Delta: explicit fallback DataTemplate in MainWindow (desktop + tablet) with recovery action to Now Playing.
- Failure point: inspector-open telemetry lacked origin attribution, limiting diagnostics for contextual routing behavior.
- Delta: source-attributed OpenInspectorEvent contract and normalized listener-side logging hooks.
- Failure point: SplitView mode remained hardcoded inline while tablet/mobile threshold flags changed, risking inconsistent inline/overlay behavior near boundaries.
- Delta: width-threshold resolver + resize-applied display mode with explicit 1023/1024 boundary regression coverage.
- Failure point: delayed inspector-open events from prior pages could reopen stale payloads after rapid navigation.
- Delta: page-aware source gating in MainViewModel inspector-open listener plus helper coverage for eligibility matrix.
- Failure point: title/icon defaults were duplicated at emitter call sites, making inspector presentation drift likely when new sources were added.
- Delta: shared OpenInspectorEvent presentation resolver and factory, with source-only emitters and resolver regression coverage.
- Failure point: null inspector payloads could reach the panel service and create an invalid open state.
- Delta: MainViewModel now filters null payloads before routing; unsupported non-null payloads continue to hit the fallback DataTemplate.
- Failure point: download-center group metadata could drift away from the grouped track model and render inconsistent title/subtitle/artwork values in the downloads template.
- Delta: focused DownloadGroupViewModel regression coverage now exercises the real grouped path and locks source-playlist/project-selection metadata resolution.

## Primary Code Surfaces
- MainWindow.axaml split view and pane template routing.
- IRightPanelService and inspector payload integration points.
- ViewModels that emit OpenInspectorEvent.

## Validation Contract
- dotnet build ORBIT-Pure.sln -v minimal
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~Library"

## Recap Link
- DOCS/recaps/PHASE3_ROUTING_RECAP_PACK.md
