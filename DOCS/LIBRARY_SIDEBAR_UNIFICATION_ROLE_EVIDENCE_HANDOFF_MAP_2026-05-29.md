# Library Sidebar Unification Role-Evidence Handoff Map (2026-05-29)

Status: Active handoff map
Date: 2026-05-29
Scope: Map each role to the minimum evidence required for a safe handoff.

## Handoff Map

| Role | Required evidence | Why |
| --- | --- | --- |
| Maintainer | Latest recap block + next-20 queue | Prevents queue drift |
| Reviewer | Focused gate result + contract/index alignment | Confirms enforceable discoverability |
| Steward | Overlap and supersession references | Prevents archive sprawl |
| Onboarder | Restart reference + onboarding matrix | Reduces re-derivation time |

## Handoff Rule

A handoff is incomplete if any role lacks at least one concrete evidence pointer.
