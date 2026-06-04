# Library Sidebar Unification Maintainer Escalation Decision Table (2026-05-29)

Status: Active operations table
Date: 2026-05-29
Scope: Define when maintainers should escalate closure-maintenance concerns.

## Decision Table

| Situation | Escalate? | Reason |
| --- | --- | --- |
| Contract missing new artifacts | Yes | Discoverability can silently drift |
| Index/status wording mismatch only | Usually no | Fix in same batch unless repeated |
| Focused gate failure in target suites | Yes | Could indicate contract or route regression |
| Duplicate artifact with unclear supersession | Yes | Archive trust and onboarding quality degrade |
| Minor annotation wording preference | No | Keep momentum and batch completion |

## Rule

Escalate whenever drift could hide missing coverage or block reliable handoff.
