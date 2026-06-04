# Workstation SVG Icons

Status: Task 2 migration pack (phase 1)

## Purpose
This folder hosts workstation-relevant vector icons for high-DPI rendering and consistent visual scaling.

## Naming
- Use lowercase kebab-case.
- Keep names action-oriented.
- Examples: `play.svg`, `zoom-in.svg`, `pan-left.svg`.

## Current Icon Set
- `play.svg`
- `stop.svg`
- `undo.svg`
- `redo.svg`
- `pan-left.svg`
- `pan-right.svg`
- `zoom-in.svg`
- `zoom-out.svg`
- `close.svg`

## Migration Map (Current Workstation Controls)
- Header transport and nav controls in `Views/Avalonia/WorkstationPage.axaml`:
  - Play/Pause button -> `play.svg`
  - Stop button -> `stop.svg`
  - Undo button -> `undo.svg`
  - Redo button -> `redo.svg`
  - Pan left/right buttons -> `pan-left.svg` / `pan-right.svg`
  - Zoom in/out buttons -> `zoom-in.svg` / `zoom-out.svg`
  - Exit loop icon -> `close.svg`

## Integration Notes
- The workstation currently uses `PathIcon` with geometry resources for active controls.
- This folder establishes the source asset pipeline for full SVG migration.
- Follow-up slices should unify loading strategy so controls consume these assets directly and consistently.
