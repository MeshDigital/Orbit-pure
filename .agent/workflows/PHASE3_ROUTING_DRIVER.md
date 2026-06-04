# PHASE3 Routing Driver

Status: Active
Owner: Autonomous Agent
Last Updated: 2026-05-31

## Scope
Phase 3 routing and inspector stabilization for global right panel behavior, event routing, and resilient fallbacks.

## Slice Queue (10 Items)
1. P3-RI-001: Audit inspector open/close event flow from route changes.
2. P3-RI-002: Harden fallback pane behavior when view model template is missing.
3. P3-RI-003: Add telemetry hooks for inspector open source attribution.
4. P3-RI-004: Validate responsive inline/overlay mode switches around width thresholds.
5. P3-RI-005: Guard against stale inspector payloads during rapid navigation.
6. P3-RI-006: Consolidate inspector title/icon defaults across callers.
7. P3-RI-007: Add tests for null payload and invalid payload routing.
8. P3-RI-008: Verify download center group metadata resolution in inspector.
9. P3-RI-009: Add discoverability and recap links for routing hardening decisions.
10. P3-RI-010: Final sweep and phase checkpoint report for routing lane.

## Execution Loop
1. Create slice artifact in .agent/queues/ using the slice template.
2. Integrate discoverability updates into DOCUMENTATION_INDEX.md and lane discoverability docs.
3. Implement code changes needed by the slice.
4. Run lane gates and capture pass/fail output.
5. Generate lane recap pack delta.
6. Append memory deltas in .agent/memory and repository memory notes.
7. Mark slice status and continue to next item.

## Gate Commands
- dotnet build ORBIT-Pure.sln -v minimal
- dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~Library"

## Discoverability Targets
- DOCUMENTATION_INDEX.md
- DOCS/discoverability/PHASE3_ROUTING_DISCOVERABILITY.md
- DOCS/recaps/PHASE3_ROUTING_RECAP_PACK.md

## Memory Targets
- .agent/memory/PHASE3_ROUTING_MEMORY.md
- /memories/repo/global-sidebar-panel-sync.md
- DOCS/memory/workstation_cockpit.md

## Recap Targets
- agent/recaps/PHASE3_ROUTING_RECAP_2026-05-31.md
- DOCS/recaps/PHASE3_ROUTING_RECAP_PACK.md
