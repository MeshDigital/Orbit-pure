# Workstation Timeline

Status: Active
Last updated: 2026-05-08

## Principle
Timeline is a permanent canvas in Workstation. Tooling layers change around it; timeline context does not disappear when users switch tools.

## Always-Visible Contract
The timeline strip and deck timeline context remain visible across:
- Waveform
- Flow
- Stems
- Automation
- Samples
- Export

## Lane Architecture
A dedicated lane row sits below timeline status/ruler context.

Lane selection is mode-driven:
- Waveform lane
- Flow lane
- Stems lane
- Automation lane
- Samples lane
- Export lane

Each lane provides compact, action-oriented controls plus state summaries.

## Context Retention
Switching tools must preserve:
- Active playlist selection
- Focused deck context
- Timeline zoom/pan state
- Snapping and quantization state

## CTA Flow Model
The drawer and lane surfaces expose progression CTAs instead of dead ends.

Typical progression:
1. Select Playlist
2. Acquire tracks (Download or Import)
3. Analyze playlist
4. Load into workstation/decks

CTA visibility is state-driven in WorkstationViewModel.

## Overlay and Drawer Integration
Timeline workflows integrate with:
- Track list overlay
- Bottom flow drawer
- Queue/readiness summaries

Overlay and drawer spacing follow workstation DPI tokens for compact and touch-friendly scaling.

## Performance and UX Guardrails
- Keep timeline rendering stable while lane/inspector context switches.
- Avoid full-page transitions for tool changes.
- Prefer in-place overlays and contextual inspectors to reduce context loss.

## Validation Checklist
For timeline slices, validate:
- Tool switching with timeline continuity
- Playlist/no-playlist CTA state transitions
- Keyboard shortcuts for cockpit actions
- Build stability via dotnet build ORBIT-Pure.sln -nologo
