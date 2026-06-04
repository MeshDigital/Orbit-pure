# Library Sidebar Unification Contract Drift Triage Examples (2026-05-29)

Status: Active reference
Date: 2026-05-29
Scope: Show practical triage paths when documentation-index contract drift is detected.

## Examples

1. New file exists, contract not updated:
   - Add missing `Assert.Contains` entry.
   - Add matching quick-start link.
   - Re-run focused gate.
2. Contract includes file not in index:
   - Restore index entry or retire the contract line with supersession note.
3. Index lists file that was replaced:
   - Update index to newest artifact.
   - Add supersession pointer if historical context matters.

## Triage Principle

Resolve drift at the contract and index boundaries first; recaps come after alignment is restored.
