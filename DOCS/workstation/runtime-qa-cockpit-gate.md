# Runtime QA Cockpit Gate

Status: active manual gate
Status date: 2026-05-09
Scope: required runtime validation before major timeline feature slices

## Purpose

Confirm the cockpit shell remains stable after structural UI changes and before deeper Flow, Stems, and Automation implementation.

## Preconditions

- Build passes: dotnet build ORBIT-Pure.sln -nologo
- Timeline layout guards pass:
  dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -nologo --filter "FullyQualifiedName~WorkstationTimelineLayoutGuardTests"

## Test Matrix

Run each scenario manually and record result.

Legend:

- PASS: behavior is correct and visually stable
- FAIL: functional or visual regression
- N/A: not applicable to current slice

## Scenarios

1. Empty state
- Expected:
  - Timeline scaffold visible
  - Empty overlay visible and lightweight
  - Empty overlay does not replace canvas
  - No deck-first shell chrome
  - No duplicate flow summary surfaces

2. First track load
- Expected:
  - Timeline rows update
  - Inspector updates to focused track
  - Drawer remains usable
  - No clipped text

3. Second track load
- Expected:
  - Transition region context appears
  - Flow context remains lane-native
  - No duplicate FlowWindowSummary in inspector

4. Drawer toggle
- Expected:
  - Drawer opens and closes cleanly
  - Timeline remains visible and stable
  - No panel overlap

5. Tool switching
- Expected:
  - Waveform to Flow to Stems to Automation to Samples to Export works
  - Timeline remains visible for all tools
  - Inspector content updates correctly

6. Inspector visibility
- Expected:
  - Expand and collapse do not hide timeline
  - No overlap with timeline controls

7. Window resize
- Expected:
  - No clipped controls or labels
  - Timeline scales and remains interactive
  - No overlapping panes

8. Timeline zoom
- Expected:
  - Zoom in and out remain responsive
  - Grid markers remain aligned

9. Drag track into timeline
- Expected:
  - Track block appears in timeline
  - Timeline remains visible
  - No deck chrome reintroduced

10. Remove track
- Expected:
  - Timeline scaffold remains
  - Empty overlay returns when appropriate
  - No layout break

## Execution Log

Date:

Build hash or branch:

Tester:

Environment:
- Resolution:
- Scaling:
- Theme:

Results:

1. Empty state: PASS or FAIL
Notes:

2. First track load: PASS or FAIL
Notes:

3. Second track load: PASS or FAIL
Notes:

4. Drawer toggle: PASS or FAIL
Notes:

5. Tool switching: PASS or FAIL
Notes:

6. Inspector visibility: PASS or FAIL
Notes:

7. Window resize: PASS or FAIL
Notes:

8. Timeline zoom: PASS or FAIL
Notes:

9. Drag track into timeline: PASS or FAIL
Notes:

10. Remove track: PASS or FAIL
Notes:

## Gate Decision

- APPROVED: all critical scenarios PASS
- BLOCKED: one or more critical scenarios FAIL

Blocking defects:

Follow-up issue links:
