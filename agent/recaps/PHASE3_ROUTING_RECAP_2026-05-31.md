# Phase3 Routing Recap 2026-05-31

Status: Active

## Completed
- Lane driver initialized.
- Discoverability and recap pack created.
- Seed slice artifact P3-RI-001 created.
- Gate baseline passed: build and Library filter tests.
- P3-RI-001 completed:
	- Added route-transition close decision helper in MainViewModel.
	- Triggered CloseInspectorEvent on page transitions for non-player contextual payloads.
	- Added navigation tests covering close-decision matrix.
	- Validation passed: build, navigation tests, and Library lane tests.
- P3-RI-002 completed:
	- Added default inspector fallback DataTemplate for unsupported payload view models in desktop and tablet MainWindow inspector surfaces.
	- Added a deterministic recovery action from fallback pane to Now Playing.
	- Added architecture assertion coverage for fallback marker text.
	- Validation passed: build gate, targeted architecture/navigation tests, and Library lane tests.
- P3-RI-003 completed:
	- Added Source attribution to OpenInspectorEvent with Unknown-safe default handling.
	- Stamped source IDs at active inspector emitters (Library, Search, Downloads, FlowBuilder).
	- Added MainViewModel source normalization and attribution logging at inspector-open routing.
	- Added targeted helper + architecture regression coverage for source-attributed contract calls.
	- Validation passed: build gate, targeted architecture/navigation tests, and Library lane tests.
- P3-RI-004 completed:
	- Replaced hardcoded SplitView inline mode with responsive threshold-driven display mode updates.
	- Added width-threshold resolver for overlay/inline switching around the 1024 breakpoint.
	- Aligned resize path so IsMobileMode/IsTabletMode and SplitView display mode derive from the same width update.
	- Added navigation tests for explicit 1023/1024 display-mode boundary behavior.
	- Validation passed: build gate, targeted architecture/navigation tests, and Library lane tests.
- P3-RI-005 completed:
	- Added page-aware inspector source eligibility helper in MainViewModel to filter stale cross-route OpenInspectorEvent writes.
	- Guarded OpenInspectorEvent listener so delayed route-mismatched sources are logged and ignored before RightPanelService.OpenPanel.
	- Added helper matrix tests for Library/Search/Downloads/FlowBuilder source families and unknown-pass-through behavior.
	- Added architecture contract assertion that MainViewModel applies source eligibility guard before close-event handling path.
	- Validation passed: build gate, targeted architecture/navigation tests (58/58), and Library lane tests (72/72).
- P3-RI-006 completed:
	- Added shared OpenInspectorEvent presentation resolver so title/icon defaults are chosen centrally from source families.
	- Switched all active inspector emitters to source-only factory calls, removing repeated title/icon literals from call sites.
	- Added title/icon default regression coverage and architecture assertions for shared factory usage.
	- Validation passed: build gate, targeted architecture/navigation tests (68/68), and Library lane tests (73/73).
- P3-RI-007 completed:
	- Added null inspector payload guard in MainViewModel so missing payloads are ignored before RightPanelService mutation.
	- Kept invalid payload routing covered through the existing MainWindow fallback DataTemplate contract.
	- Added regression coverage for null payload handling and architecture assertions for the null guard location.
	- Validation passed: build gate, targeted architecture/navigation tests (70/70), and Library lane tests (73/73).
- P3-RI-008 completed:
	- Added a focused regression suite that builds real grouped download rows from the same DynamicData path used by DownloadCenterViewModel.
	- Locked metadata resolution for source-playlist groups and project-selection fallback so the inspector-facing download template keeps stable title/subtitle/artwork output.
	- Validation passed: focused DownloadGroupViewModel tests and combined Library + download-group gate (75/75).
- P3-RI-009 completed:
	- Added a routing hardening decisions section to the discoverability map so the completed routing slices are reachable from one linked trail.
	- Added recap-pack links back to the discoverability section so the routing hardening decisions remain bidirectionally traceable.
	- Validation passed: build gate and Library lane tests (75/75).

- P3-RI-010 completed:
	- Closed the routing lane with a final sweep checkpoint that marks P3-RI-001 through P3-RI-010 complete.
	- Preserved the discoverability and recap trail as the resume point for any future routing hardening effort.
	- Validation passed: build gate and Library lane tests (73/73).

## Next
- Routing lane complete.
