# Library Sidebar Unification Contract-Index Drift Triage Matrix (2026-05-29)

Status: Active triage matrix
Date: 2026-05-29
Scope: Triage combined contract and index drift patterns during high-frequency waves.

## Matrix

| Drift pattern | First fix | Second fix |
| --- | --- | --- |
| Contract has entry, index missing link | Add index link | Validate status and recap mention |
| Index has link, contract missing assertion | Add contract assertion | Re-run focused gate |
| Contract/index aligned, status stale | Refresh status bullets | Refresh recap block |
| All three aligned, recap queue stale | Update recap queue | Update plan queues |

## Principle

Fix structural discoverability surfaces before narrative recap surfaces.
