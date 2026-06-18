# Workstation Cue/Loop System & UX Enhancements

Status: Completed
Last Updated: 2026-06-18

## Summary

Two implementation streams shipped in session 2026-06-18:

1. **Cue/Loop DB & Rekordbox system** — full persistence and export round-trip for hot cues, memory cues, and loop points
2. **Workstation UX enhancements** — live position display, loop active indicator, single-pad delete, WaveformInspector LOOP parity

---

## Stream 1: Cue/Loop DB Schema and Rekordbox Export

### DB Migration: `20260618000000_AddCuePointLoopAndSlot`

Three columns added to `CuePoints` table:

| Column | Type | Default | Purpose |
|---|---|---|---|
| `IsLoop` | bool | false | Marks this cue as a loop in/out pair |
| `LoopEndSeconds` | double | 0.0 | Loop out-point in seconds |
| `SlotIndex` | int | -1 | Hot cue pad assignment; -1 = memory cue (no pad) |

`Migrations/AppDbContextModelSnapshot.cs` updated accordingly.

### Hot Cue CDJ Behavior (`WorkstationDeckViewModel.TriggerHotCuePadAsync`)

- **Empty pad**: set cue at current playhead + persist to DB via `CueEditor.AddHotCueAtSlotCommand`
- **Occupied pad**: jump to the existing cue via `Deck.Engine.JumpToHotCue(slot)`

Pad colors (slots 0–7): `#FF0000 #FF8800 #FFFF00 #00FF00 #00FFFF #0088FF #8800FF #FF00FF`

### CueEditorViewModel Commands

- `AddHotCueAtSlotCommand` (ReactiveCommand<(double Timestamp, int Slot)>) — set/replace hot cue at pad
- `DeleteHotCueAtSlotCommand` (ReactiveCommand<int>) — remove cue at slot, save to DB
- `SetLoopCommand` (ReactiveCommand<(double, double)>) — persist loop cue
- `ClearLoopCommand` — remove the active loop cue

`OrbitCueToEntity` fix: previously used an ignored `slotIndex` parameter and always wrote 0. Now maps `SlotIndex = c.SlotIndex` correctly.

### Rekordbox XML Export (`Services/Library/PlaylistExportService.cs`)

`BuildCueList` now carries `IsLoop`, `LoopEndSeconds`, `SlotIndex` from all DB entities.

Two output formats:
- **Point cues** (`IsLoop = false`): `<POSITION_MARK Type="0" Num="{SlotIndex}"/>` — Num 0–7 = hot cue pad, Num -1 = memory cue
- **Loop cues** (`IsLoop = true`): `<POSITION_MARK Type="4" Start="X.XXX" End="Y.YYY" Num="-1"/>` — Rekordbox native loop format

### Loop Staging and Commit

`WorkstationDeckViewModel` loop flow:
1. `SetLoopInCommand` — stores `LoopInSeconds` at current playhead
2. `SetLoopOutCommand` — stores `LoopOutSeconds` at current playhead
3. `CommitLoopCommand` — calls `CueEditor.SetLoopCommand` to persist; `LoopIsReady` gates the SAVE button
4. `AutoLoopBarsCommand` (double bars) — computes `barLength = 240 / BPM`, sets staged loop in one step
5. Loop restored on track load from first `IsLoop` cue in `CueEditor.Cues`

---

## Stream 2: Workstation UX Enhancements

### Live Position Display

`WorkstationDeckViewModel.PositionDisplayText` — formatted `"M:SS.cc / M:SS.cc"` via `FormatSeconds(double s)`.

Wired to `Deck.WhenAnyValue(x => x.PositionSeconds, x => x.DurationSeconds, ...)` subscription alongside `PlaybackProgress` updates.

Shown as a monospace `Border` (dark background `#0D1419`) at the bottom of the track info header in both cockpit panels. Visible only when `IsLoaded`.

### Loop Active Indicator

`WorkstationDeckViewModel.IsLoopActive` — delegates to `Deck.IsLoopActive`.

Wired to `Deck.WhenAnyValue(x => x.IsLoopActive, ...)` subscription.

In the LOOP section header of both panels:
- Green "ACTIVE" pill (`#B8E986` on `#1A3020`) — visible only when `IsLoopActive`
- "EXIT" button — visible only when `IsLoopActive`, binds to `FocusedDeck.Deck.ExitLoopCommand`

### Single Hot Cue Pad Delete

`WorkstationDeckViewModel.DeleteHotCueAtSlotCommand` (ReactiveCommand<int>) — delegates to `CueEditor.DeleteHotCueAtSlotCommand`, then calls `ApplySuggestedHotCues` to refresh pad visuals.

Each of the 8 hot cue pad buttons in both `WorkstationPage.axaml` and `WaveformInspector.axaml` now has a right-click `ContextMenu` with `"Clear pad"` → `DeleteHotCueAtSlotCommand` with `CommandParameter="{slot index}"`.

### WaveformInspector LOOP Section Parity

`Views/Avalonia/Workstation/WaveformInspector.axaml` now has the full LOOP section (inserted between HOT CUES and Stems):
- Header row with LOOP label + ACTIVE pill + MEM button + EXIT button
- IN / OUT / CLR buttons → SetLoopInCommand / SetLoopOutCommand / ClearStagedLoopCommand
- LoopRegionText readout (green `#B8E986`) + SAVE button (gated on `LoopIsReady`)
- LoopLengthBarsText (grey `#A0A0A0`, only visible when `LoopIsReady`)
- Auto-loop presets row: ½ 1 2 4 8 → `AutoLoopBarsCommand`

Previously only `WorkstationPage.axaml` had this section.

---

## Architecture Notes

- Both `WorkstationPage.axaml` (cockpit drawer) and `WaveformInspector.axaml` (waveform mode inspector) share `DataContext = WorkstationViewModel` and bind via `FocusedDeck.*`.
- When adding workstation features, keep both panels in sync.
- `CommandParameter` strings are coerced to `int`/`double` by Avalonia's built-in TypeConverter — the same mechanism used by `AutoLoopBarsCommand`.
