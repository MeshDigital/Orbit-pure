# Library Sidebar Unification Contract Assertion Minimization (2026-05-29)

Status: Active guidance
Date: 2026-05-29
Scope: Keep the architecture documentation-index contract strict without making it unreadable.

## Minimization Rules

1. Assert only the active artifact file names that must remain discoverable.
2. Avoid duplicating explanatory prose inside the test.
3. Keep retired wording and stale-language checks in separate targeted assertions.
4. Add new contract lines in themed batches so review remains scan-friendly.
5. Prefer one authoritative discoverability test over many partial variants.

## Result

A dense contract is acceptable if every line maps to a real, current artifact and the surrounding documentation stays synchronized.
