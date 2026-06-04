# Library Sidebar Unification Focused Gate Troubleshooting (2026-05-29)

Status: Focused gate troubleshooting note
Date: 2026-05-29
Scope: Common troubleshooting steps when the focused sidebar gate fails during maintenance work.

## Troubleshooting

1. Check whether a closure artifact was added without updating the documentation-index contract test.
2. Check whether `DOCUMENTATION_INDEX.md` and `DOCUMENTATION_STATUS.md` drifted apart.
3. Check whether route or compatibility assertions now mismatch the current code.
4. Re-run only after all touched doc/index/test surfaces are aligned.
