# ORBIT — Documentation Index

This index was rewritten on 2026-08-10 after removing ~350 files of stale, auto-generated
process documentation (see [DOCUMENTATION_STATUS.md](DOCUMENTATION_STATUS.md) for what was
removed and why). Every link below was verified to point at a file that actually exists.

## Start here

| Doc | What it's for |
|---|---|
| [README.md](README.md) | What ORBIT is, how to build/run it |
| [FEATURES.md](FEATURES.md) | User-facing feature list |
| [ARCHITECTURE.md](ARCHITECTURE.md) | System design — layers, data flow, the major subsystems |
| [RECENT_CHANGES.md](RECENT_CHANGES.md) | Chronological changelog — the most reliably up to date doc in the repo |
| [TODO.md](TODO.md) | Roadmap / upgrade backlog |
| [USER_MANUAL.md](USER_MANUAL.md) | End-user usage guide |
| [BETA_TESTER_GUIDE.md](BETA_TESTER_GUIDE.md) | Onboarding for beta testers |
| [CONTRIBUTING.md](CONTRIBUTING.md) | How to contribute |
| [WORKSPACE_SETUP_GUIDE.md](WORKSPACE_SETUP_GUIDE.md) | VS Code workspace setup |

## Architecture deep-dives (root)

| Doc | Covers |
|---|---|
| [ARCHITECTURE_DEEPDIVE.md](ARCHITECTURE_DEEPDIVE.md) | Broader architectural deep-dive, complements ARCHITECTURE.md |
| [DOWNLOAD_CENTER_ARCHITECTURE.md](DOWNLOAD_CENTER_ARCHITECTURE.md) | Downloads Center overview |
| [QUEUE_SYSTEM_INVESTIGATION.md](QUEUE_SYSTEM_INVESTIGATION.md) | Download Center queue system investigation |
| [LIBRARY_VIRTUALIZATION_DEEPDIVE.md](LIBRARY_VIRTUALIZATION_DEEPDIVE.md) | Library UI virtualization & data pipeline |
| [SOULSEEK_LOGIN_AND_SERVICE_SIGNALS_TECHNICAL.md](SOULSEEK_LOGIN_AND_SERVICE_SIGNALS_TECHNICAL.md) | Soulseek login/service signal handling |
| [SEARCH_ENGINE_HEURISTIC_UPGRADE_PLAN.md](SEARCH_ENGINE_HEURISTIC_UPGRADE_PLAN.md) | Search ranking heuristics plan |
| [CONNECTION_SEARCH_HARDENING_IMPLEMENTATION_PLAN.md](CONNECTION_SEARCH_HARDENING_IMPLEMENTATION_PLAN.md) | Soulseek.NET connection/search hardening plan |

## Technical deep-dives (DOCS/)

| Doc | Covers |
|---|---|
| [DOCS/SOCIAL_LAYER_ARCHITECTURE.md](DOCS/SOCIAL_LAYER_ARCHITECTURE.md) | Soulseek serving/upload resolvers, share indexing, presence, chat, contacts, notifications |
| [DOCS/REKORDBOX_EXPORT_ARCHITECTURE.md](DOCS/REKORDBOX_EXPORT_ARCHITECTURE.md) | Rekordbox XML export, colour tags, hot loops/cues, tempo grid derivation, merge-mode re-export |
| [DOCS/cue-forge-architecture.md](DOCS/cue-forge-architecture.md) | Cue Forge (cue/loop generation) architecture |
| [DOCS/download_center_architecture_v2.md](DOCS/download_center_architecture_v2.md) | Download Center architecture, v2 |
| [DOCS/TRACK_COMPATIBILITY_SCORING.md](DOCS/TRACK_COMPATIBILITY_SCORING.md) | Harmonic/BPM/energy compatibility scoring |
| [DOCS/SEARCH_TO_MIX_WORKFLOW.md](DOCS/SEARCH_TO_MIX_WORKFLOW.md) | Search-result → mix-building workflow |
| [DOCS/STEM_CACHE_AND_PREFERENCES.md](DOCS/STEM_CACHE_AND_PREFERENCES.md) | Stem separation caching & preferences |
| [DOCS/IMPORT_ORCHESTRATION_AND_PREVIEW.md](DOCS/IMPORT_ORCHESTRATION_AND_PREVIEW.md) | Playlist import orchestration & preview |
| [DOCS/PLAYER_PREP_AND_ROUTING.md](DOCS/PLAYER_PREP_AND_ROUTING.md) | Player prep/routing readiness surfaces |
| [DOCS/ANALYSIS_QUEUE_UX_SURFACES.md](DOCS/ANALYSIS_QUEUE_UX_SURFACES.md) | Analysis queue UX |
| [DOCS/REACTIVE_SEARCH_RUNTIME_TECHNICAL_2026-03-22.md](DOCS/REACTIVE_SEARCH_RUNTIME_TECHNICAL_2026-03-22.md) | Reactive search runtime internals |
| [DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md](DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md) | Search stream hardening plan |
| [DOCS/ANTIGRAVITY_LIBRARY_SIDEPANEL_REDESIGN.md](DOCS/ANTIGRAVITY_LIBRARY_SIDEPANEL_REDESIGN.md) | Library sidepanel redesign notes |
| [DOCS/POST_METRICS_LIFECYCLE_AUDIT_PLAYBOOK.md](DOCS/POST_METRICS_LIFECYCLE_AUDIT_PLAYBOOK.md) | Post-download metrics lifecycle audit |
| [DOCS/strict_mode_gui_validation_checklist.md](DOCS/strict_mode_gui_validation_checklist.md) | Auto-download strict-mode GUI validation checklist |

### Workstation

| Doc | Covers |
|---|---|
| [DOCS/WORKSTATION_COCKPIT_ROUTING.md](DOCS/WORKSTATION_COCKPIT_ROUTING.md) | Workstation cockpit routing |
| [DOCS/WORKSTATION_SESSION_PERSISTENCE.md](DOCS/WORKSTATION_SESSION_PERSISTENCE.md) | Session autosave/restore |
| [DOCS/WORKSTATION_COCKPIT_FOUNDATION.md](DOCS/WORKSTATION_COCKPIT_FOUNDATION.md) | Canonical functional/UI spec |
| [DOCS/WORKSTATION_COCKPIT_EPIC.md](DOCS/WORKSTATION_COCKPIT_EPIC.md) | Cockpit refactor epic |
| [DOCS/WORKSTATION_COCKPIT_ISSUE_BACKLOG.md](DOCS/WORKSTATION_COCKPIT_ISSUE_BACKLOG.md) | Cockpit issue backlog |
| [DOCS/WORKSTATION_FLOW_INTELLIGENCE_A10_EPIC.md](DOCS/WORKSTATION_FLOW_INTELLIGENCE_A10_EPIC.md) | Flow Intelligence & similarity engine epic |
| [DOCS/workstation/architecture.md](DOCS/workstation/architecture.md) | Workstation architecture |
| [DOCS/workstation/flow-integration-blueprint.md](DOCS/workstation/flow-integration-blueprint.md) | Flow integration blueprint |
| [DOCS/workstation/flow-intelligence-design-note.md](DOCS/workstation/flow-intelligence-design-note.md) | Flow intelligence design note |
| [DOCS/workstation/flow-slice-4-transition-presets-blueprint.md](DOCS/workstation/flow-slice-4-transition-presets-blueprint.md) | Transition presets blueprint |
| [DOCS/workstation/runtime-qa-cockpit-gate.md](DOCS/workstation/runtime-qa-cockpit-gate.md) | Runtime QA cockpit gate |
| [DOCS/workstation/timeline.md](DOCS/workstation/timeline.md) | Workstation timeline |
| [DOCS/workstation/tools.md](DOCS/workstation/tools.md) | Workstation tools |

## Planning / investigation notes (historical, not guaranteed current)

These are point-in-time working notes from past feature passes — useful for *why* a design
decision was made, not a live reference. Treat anything here as possibly superseded by
[RECENT_CHANGES.md](RECENT_CHANGES.md) or the code itself.

- [DOCS/CURRENT_AND_FUTURE_PLAN_SUMMARY.md](DOCS/CURRENT_AND_FUTURE_PLAN_SUMMARY.md)
- [DOCS/ROADMAP_PROGRESS_AND_DOC_GAPS_2026-04-20.md](DOCS/ROADMAP_PROGRESS_AND_DOC_GAPS_2026-04-20.md)
- [DOCS/automatic_downloads_phase2_plan.md](DOCS/automatic_downloads_phase2_plan.md) / [DOCS/automatic_downloads_phase2_memory.md](DOCS/automatic_downloads_phase2_memory.md)
- [DOCS/discoverability/](DOCS/discoverability/) — routing/library-intelligence lane maps from the Phase 3/4 development passes
- [DOCS/recaps/](DOCS/recaps/) — recap packs for the same passes
- [DOCS/memory/](DOCS/memory/) — ~20 dated investigation/completion-report notes (see [DOCS/memory/MEMORY_INDEX.md](DOCS/memory/MEMORY_INDEX.md))

## Project meta

| Doc | Covers |
|---|---|
| [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) | Code of conduct |
| [DOCUMENTATION_STATUS.md](DOCUMENTATION_STATUS.md) | Current documentation health + the 2026-08-10 sprawl cleanup |
| [DOCUMENTATION_AUDIT_JAN2026.md](DOCUMENTATION_AUDIT_JAN2026.md) | Historical: the Jan 2026 documentation audit (superseded by this index, kept for record) |
| [CLEANUP_COMPLETE_SUMMARY.md](CLEANUP_COMPLETE_SUMMARY.md) | Historical: an earlier repo cleanup pass |

---

**Note on `.agent/` and `agent/`:** these directories hold scaffolding for a phase-based
autonomous development loop (checkpoints, driver workflows, phase memory). They're
process/tooling state, not user- or developer-facing documentation, and are intentionally
not indexed here.
