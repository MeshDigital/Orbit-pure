# Workstation Architecture

Status: Active cockpit migration surface
Last updated: 2026-05-08

## Purpose
Workstation is the primary playback and editing cockpit in Orbit. The architecture prioritizes timeline continuity, contextual tooling, and compact high-density operation across DPI scales.

## Primary Surfaces
1. Header
- Tool selector and essential transport controls.
- Density controls and global cockpit status.

2. Main canvas
- Timeline is always visible.
- Tool-specific lanes render in place beneath the timeline strip.
- Deck rows remain available as persistent production context.

3. Right inspector
- Contextual content by active workstation tool.
- Action-heavy detail panels for waveform, flow, stems, automation, samples, and export contexts.

4. Bottom drawer
- Flow builder, track list, and overlay trigger controls.
- Actionable CTA strip for acquisition and readiness transitions.

## View and ViewModel
- View: Views/Avalonia/WorkstationPage.axaml
- Code-behind: Views/Avalonia/WorkstationPage.axaml.cs
- ViewModel: ViewModels/Workstation/WorkstationViewModel.cs

## Tool Model
Workstation tools are modeled as a shared collection in WorkstationViewModel and rendered by the header selector.

Current tools:
- Waveform
- Flow
- Stems
- Automation
- Samples
- Export

Tool switches change lane and inspector context. They do not navigate away from Workstation.

## Navigation Semantics
Workstation is the primary cockpit destination for playback/edit workflows.

- Direct Workstation navigation remains first-class.
- Legacy Player/NowPlaying navigation requests are remapped to Workstation.

## DPI and Density
DPI-aware values are tokenized under src/UI/Tokens/DpiTokens.axaml.

Density classes:
- ws-density-compact
- ws-density-normal
- ws-density-touch

Tokenized workstation regions include:
- Header spacing and chip rhythm
- Lane paddings
- Inspector paddings
- Drawer status strip spacing
- Track overlay paddings and header margin
- Splitter height and track-pool paddings

## Event and Command Boundaries
- CTA navigation uses IEventBus with NavigateToPageEvent.
- Workstation code-behind handles focused shell interactions:
  - Overlay open/close
  - Drawer sizing behavior
  - CTA click routing
  - Cockpit keyboard shortcuts

## Non-Goals
- Creating a second parallel cockpit implementation.
- Moving timeline ownership to page-level mode routing.

## Validation Baseline
Use:
- dotnet build ORBIT-Pure.sln -nologo

Current migration slices target build-stable incremental diffs with warning baseline unchanged unless explicitly addressed.
