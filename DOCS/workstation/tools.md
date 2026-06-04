# Workstation Tools

Status: Active
Last updated: 2026-05-08

## Overview
Workstation tools are contextual surfaces inside one cockpit. Selecting a tool updates lane and inspector content while preserving timeline continuity.

## Tool Registry
The tool list is defined in WorkstationViewModel and bound into the cockpit header selector.

### Waveform
Goal:
- Deck-focused transport, cue, loop, and waveform operations.

Lane:
- Waveform lane summary and quick deck context.

Inspector:
- Focused deck status
- Loop controls
- Deck action summaries

### Flow
Goal:
- Playlist shaping and transition readiness in timeline context.

Lane:
- Playlist flow summary
- Readiness and analysis queue chips
- Contextual actions:
  - Select Playlist
  - Download Tracks
  - Analyze Playlist
  - Track Overlay

Inspector:
- Flow summary and queue context
- Playlist-level guidance

### Stems
Goal:
- Stem isolation and quick transform actions without leaving the cockpit.

Lane:
- Focused deck stem status
- Quick stem actions (separate/instrumental/drums)

Inspector:
- Stem state and focused deck control surface

### Automation
Goal:
- Snap/quantize/metronome and timeline automation guidance in shell context.

Lane:
- Automation summary
- Quantization toggles and nudge controls

Inspector:
- Automation mode summary
- Queue-aware context and timing toggles

### Samples
Goal:
- Sample prep workflow aligned to playlist and deck loading.

Lane:
- Sample prep summary
- Import/select/load actions

Inspector:
- Samples summary
- CTA state context for import/download/load progression

### Export
Goal:
- Fast format and launch controls for output.

Lane:
- Export summary
- Format presets and export action

Inspector:
- Export pipeline status and mixdown controls

## Tool Behavior Rules
1. Tool switches do not navigate between pages.
2. Timeline remains visible in all tools.
3. Actions in disabled state expose actionable hint tooltips.
4. Empty states are CTA-driven, not warning-only.

## Keyboard and Shell Notes
- Core lane CTA shortcuts are handled in WorkstationPage code-behind.
- Tool behavior should remain deterministic with or without active playlist selection.

## Future Expansion
- Transition templates in Flow tooling.
- Envelope/keyframe editing depth for Automation.
- Expanded sample mapping and slot routing in Samples.
