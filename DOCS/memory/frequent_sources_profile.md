# Frequent Sources Profile

Date: 2026-05-13
Status: Active implementation lane for local-only social feature work

## Purpose
- Frequent Sources is a privacy-first, opt-in local feature for tracking peers and folders that the user downloads from.
- It must store only local metadata and never upload telemetry or PII.
- Keep scoring, matching, and core audio behavior unchanged.

## Enablement
- The repo already uses config-backed profiles for other features, so any Frequent Sources profile should reuse the same local configuration pattern.
- Prefer a disabled-by-default toggle with an explicit consent notice before enabling downloads or browsing.

## Memory Handling
- Preserve this file when switching feature profiles.
- Do not overwrite previous memory notes; create a new memory file for each distinct feature profile or add a dated subsection.
- If another profile is active, keep both notes side by side and mark the current one clearly.

## Local-only Constraints
- No external uploads.
- No social telemetry.
- No stored IP addresses, track IDs, or playlist contents unless the user explicitly opts into local folder caching.

## Suggested Next Actions
- Add a focused feature flag and local DB models only after the privacy contract is approved.
- Keep tests profile-aware so they skip cleanly when the feature is disabled.

## Implementation Checkpoint (2026-05-28)
- Frequent Sources settings UX is now surfaced in Settings (opt-in toggle + local staging path + folder browse).
- Soulseek transfer-completion path now invokes FrequentSourceService via the adapter hook with remote-folder extraction.
- FrequentSourceService placeholder tests were replaced with real SQLite-backed behavior tests (upsert aggregation, ranking order, clear, and disabled gate).
- Profile-gated Frequent Sources settings tests now execute real command/property assertions instead of placeholders (load-state + browse-command behavior).
- Adapter hook regression tests now cover remote-folder path extraction normalization and transfer-hook upsert invocation via direct internal helper calls.
- Adapter hook fail-safe tests now verify null-service no-op and disabled-feature no-write behavior.
- Hook helpers are now internal with test-assembly visibility so adapter hook tests call them directly (reflection removed).
- Download success call-site handling now routes through a dedicated helper (`TryRecordFrequentSourceDownloadAsync`) so filename parsing + byte fallback behavior are testable without live network traffic.
- FrequentSourceService now normalizes source/folder keys (trim, slash normalization, repeated-separator collapse, lowercase) to reduce duplicate logical rows.
- FrequentSourceService now preserves monotonic `LastDownloadedAtUtc` when older transfer events arrive out of order.
- Cancellation token flow is now propagated from `DownloadAsync` into Frequent Sources hook processing and covered by regression tests.
- FrequentSourceService tests now cover normalization, monotonic timestamps, create-on-missing flag mutations, and disabled-feature no-op flag mutations.
