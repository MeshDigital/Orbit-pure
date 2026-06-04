# Library Sidebar Unification Maintainer Restart Quick-Reference (2026-05-29)

Status: Active restart aid
Date: 2026-05-29
Scope: Give a fast restart flow when maintainers resume the closure-maintenance loop.

## Restart Flow

1. Read the topmost RECENT_CHANGES batch block.
2. Confirm next-20 queue alignment across both plan docs.
3. Verify contract/index/status were updated in the latest wave.
4. Re-run the focused gate before new artifact creation if state is uncertain.
5. Continue with the next ten-slice family only after green validation.

## Restart Rule

If recap and queue surfaces disagree, resolve that drift before starting the next slice batch.
