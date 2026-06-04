# Library Sidebar Unification - Role Continuity Risk Register (2026-05-30)

## Purpose

Track continuity risks that can break maintainership handoff when documentation waves expand quickly.

## Active Risks

- Contract/index mismatch after new artifact insertion
- Status-board lag behind architecture assertions
- Duplicate slice ranges with different date families
- Missing recap entries after successful gate
- Queue roll-forward divergence across planning surfaces

## Mitigations

- Patch contract, index, and status in one atomic batch
- Run focused gate immediately after wiring
- Update RECENT_CHANGES and both plan files in the same pass
- Keep next-20 horizon aligned everywhere

## Exit Criteria

All listed risks are either closed in this wave or explicitly carried with owner and next checkpoint.
