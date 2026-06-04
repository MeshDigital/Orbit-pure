# Library Sidebar Unification Contract Correction Anti-Patterns List (2026-05-30)

Status: Active warning list
Date: 2026-05-30
Scope: Capture correction anti-patterns that cause recurring contract/index drift.

## Anti-Patterns

1. Updating index links without matching contract assertions.
2. Editing contract assertions without recap queue updates.
3. Merging two wave families into one recap block.
4. Applying broad wording changes that hide artifact purpose.
5. Deferring focused-gate validation until after multiple waves.

## Prevention Rule

Use contract-first, wave-bounded corrections and validate before recap rollover.
