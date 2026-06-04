# Library Sidebar Unification Contract Drift Correction Cookbook (2026-05-29)

Status: Active cookbook
Date: 2026-05-29
Scope: Provide repeatable correction patterns for contract drift issues.

## Recipes

1. Missing assertion recipe:
   - Add contract line.
   - Add matching index entry.
   - Re-run focused gate.
2. Stale assertion recipe:
   - Confirm supersession.
   - Update contract and index in same patch.
   - Refresh recap references.
3. Mixed wave artifacts recipe:
   - Keep wave boundaries explicit in recap blocks.
   - Do not merge queues until both surfaces align.

## Cookbook Rule

Apply smallest correction that restores contract-index-recap consistency.
