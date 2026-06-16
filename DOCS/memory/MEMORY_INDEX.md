# Memory Plans Canonical Index

Status: Source of Truth for DOCS/memory plan states

Last reviewed: 2026-06-16 (updated)
Scope: Canonical status board for planning and memory docs in DOCS/memory.

## Status Legend
- Completed: Implemented and validated in this stream.
- Historical: Useful for rationale/history; not an active execution queue.
- Parked: Intentionally deferred; do not execute unless explicitly reopened.
- Draft: Early profile/design note; requires explicit activation before implementation.
- Active Sidequest: Parallel optional stream, additive and scoped.
- Source of Truth: Ongoing reference architecture document.

## Current File Status

| File | Current Status | Notes |
| --- | --- | --- |
| [automatic_downloads_investigation.md](automatic_downloads_investigation.md) | Historical | Investigation complete; side plan and PoC reference. |
| [download_filtering_implementation_plan.md](download_filtering_implementation_plan.md) | Historical | Phased backlog snapshot; Phase 1/2 complete, Phase 3 parked. |
| [download_filtering_phase2_completion_report.md](download_filtering_phase2_completion_report.md) | Completed | Canonical completion handoff for strict-download hardening. |
| [download_filtering_strict_mode_hardening.md](download_filtering_strict_mode_hardening.md) | Historical | Superseded rationale; keep for design context. |
| [frequent_sources_profile.md](frequent_sources_profile.md) | Active | Privacy-first local feature lane is now in implementation (settings UX, transfer hook activation, call-site hardening, and expanded regression coverage). |
| [implementation_plan.md](implementation_plan.md) | Historical | Coordination index; workstation and strict-download streams already closed. |
| [download_orchestrator_hit_rate_improvements.md](download_orchestrator_hit_rate_improvements.md) | Completed | Four-fix hit rate pass: FLAC bitrate floor 700→400 kbps, retry delay 20→3 min, Aggressive-tier diacritic stripping, MP3 fallback uses Aggressive not Smart. Also covers Soulseek noise suppression and fire-and-forget safety fixes. |
| [library_2026_overhaul_completion.md](library_2026_overhaul_completion.md) | Completed | Completed state machine, UI styling, deck guards, and tests for the Hybrid Track State Model. |
| [library_sidebar_unification_plan.md](library_sidebar_unification_plan.md) | Active | Reopened by explicit request; implementation started with PlaylistIntelligence panel extraction and guard tests. |
| [library_waveform_automix_plan.md](library_waveform_automix_plan.md) | Historical | Execution blueprint retained for rationale and deltas. |
| [workstation_cockpit.md](workstation_cockpit.md) | Source of Truth | Ongoing cockpit architecture/reference log. |
| [workstation_flow_intelligence_A10.md](workstation_flow_intelligence_A10.md) | Active Sidequest | Parallel additive stream; optional unless requested. |
| [workstation_overhaul_completed_work.md](workstation_overhaul_completed_work.md) | Completed | Workstation completion walkthrough. |
| [workstation_redesign_overhaul.md](workstation_redesign_overhaul.md) | Historical | Original redesign blueprint; implementation delivered. |
| [ui_overhaul_piped_marble_completion.md](ui_overhaul_piped_marble_completion.md) | Completed | Full UI overhaul Tier 1–4 completion: mode decomposition, downloads side panel, search filter bar, settings help panel, Camelot wheel, energy/phrase/badge surfaces, tri-band waveform, drag-to-reorder, Rekordbox export, .orbsession bundle. |
| [network_resilience_and_library_reconciliation.md](network_resilience_and_library_reconciliation.md) | Completed | Soulseek ban detection (GlobalMessageReceived → SearchBanDetectedEvent → 30-min search lockout) and library reconciliation engine (ReconcilePhysicalFilesAsync + Reconcile Library button in Settings). |

## Execution Guidance
- Default implementation focus: avoid reopening Completed/Historical plans unless validating regressions.
- Only resume Parked items with explicit user request and new scope boundaries.
- For immediate new work, prefer Draft or Active Sidequest entries after explicit prioritization.

## Change Control
When a plan state changes, update this file first, then update the plan file header metadata.

## Validation Tasks
- Run `Docs: Validate Memory Plan Status Headers` to ensure each memory doc declares status metadata.
- Run `Docs: Validate Memory Index Coverage` to ensure every `DOCS/memory/*.md` file is represented in this index.
- Run `Docs: Validate Memory Governance` to execute both checks sequentially.
- Run `Docs: Validate Memory Governance (Script)` to execute the shared validator used by CI.
- `Phase2: Validation (Build + AutoDownload Tests)` now includes `Docs: Validate Memory Governance (Script)` at the start of the sequence.
- Run `Phase2: Test Gate (Hardening Subset)` for the focused hardening regression suite used in this stream.
- Run `Phase2: Validation (Pre-Commit Practical)` for the one-shot local gate: memory governance + build + hardening subset tests.

## CI Enforcement
- Workflow: `.github/workflows/memory-governance.yml`
- Validator script: `Tools/validate-memory-governance.ps1`
