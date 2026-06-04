# Library Sidebar Unification - Escalation SLA Exception Matrix (2026-05-30)

## Purpose

Classify acceptable SLA exceptions and required compensating actions in documentation governance waves.

## Matrix

| Exception Type | Allowed Condition | Required Compensation |
|---|---|---|
| Delayed index correction | Contract update merged first in same run | Correct index before gate execution |
| Delayed status update | Index and contract already aligned | Add status update before recap publication |
| Delayed recap update | Gate completed but queue not rolled yet | Update recap and plan docs in same execution |

## Rule

No SLA exception may carry into the next 10-slice wave.
