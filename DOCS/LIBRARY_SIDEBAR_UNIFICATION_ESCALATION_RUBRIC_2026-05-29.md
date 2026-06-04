# Library Sidebar Unification Escalation Rubric (2026-05-29)

Status: Maintainer escalation rubric
Date: 2026-05-29
Scope: Escalation guidance when a sidebar/intelligence regression or closure drift is discovered.

## Escalation Levels

1. Minor docs drift: fix index/status/test alignment in the same batch.
2. Contract drift: update architecture assertions and rerun focused gate before merge.
3. Ownership drift: investigate whether a new migration effort is needed before further cleanup.
4. Repeated drift pattern: document it in repo memory and add a durable maintenance artifact if needed.
