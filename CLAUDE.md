# ORBIT (SLSKDONET) — Repository Rules

ORBIT is an **Avalonia** (.NET) desktop music manager / DJ prep workstation (Soulseek acquisition,
library, analysis, Cue Forge, Flow Builder, workstation). It is moving from rapid prototyping to a
production-grade pro-audio tool (Rekordbox / Ableton / Linear level of restraint). Main project:
`SLSKDONET.csproj`. Tests: `Tests/SLSKDONET.Tests`. Backlog: `DOCS/OPEN_WORK_PLAN_2026-09-27.md`.
UI contract: `DOCS/UI_DESIGN_SYSTEM.md`.

## Git
- Never commit to `master`. Work on a feature/hardening branch; small, focused commits.
- Every commit: `dotnet build SLSKDONET.csproj` with 0 errors, and `Tests/SLSKDONET.Tests/Architecture/*` green.

## First-run & upgrade onboarding ("What's new")

Every release gets the same popup behaviour, driven by one version number — never by ad-hoc code:

- `Services/Setup/FirstRunDetector.CurrentSetupVersion` is the single source of truth. At startup the
  saved `[Setup] Version` in `config.ini` is compared against it.
- Fresh install → the full first-run wizard. An install with an older `SetupVersion` (or a pre-wizard
  install) → the "What's new" re-onboarding. Current version → nothing. Settings → Advanced → Setup
  always reopens the full wizard by hand.

**Release checklist (skip this and the popup silently never fires):**
1. Any release that ships user-facing changes MUST bump `CurrentSetupVersion`.
2. Refresh `ViewModels/Setup/WhatsNewStepViewModel.Items` to describe that release's changes — that
   list is the popup's copy; stale items mislead existing users.
3. Keep `Tests/SLSKDONET.Tests/Setup/FirstRunDetectorTests` covering "older version → UpgradeWizard".

The version int is independent of the marketing version (the 0.10 → 0.2 line reset), so never compare
against `Assembly`/`FileVersion` — use `CurrentSetupVersion` only. When a future release adds a new
*configurable* wizard step (not just copy), add it to the upgrade list in `SetupWizardViewModel`'s
step builder so upgraders are offered only that step.

## No vibecoding
1. **No placebo UI.** Every Button / MenuItem / ToggleButton / Slider binds to a real command or
   property backed by real behaviour. Unfinished features are hidden, not shown as dead buttons.
2. **No silent failures.** No empty `catch {}`; log it (`ILogger` / `EngineDiagnosticsService`) or
   surface it (`NotificationCenterService` / toast). Never report success when an operation failed.
3. **No raw hex colours or magic margins in Views.** Use tokens from `Themes/DesignTokens.axaml` and
   the style classes documented in `DOCS/UI_DESIGN_SYSTEM.md`. Spacing on the 4px grid (0,4,8,12,16,24).
4. **No hack scripts or backups in source dirs.** No `.py/.ps1/.bat/.bak` under `Services/`,
   `ViewModels/`, `Views/`, `Tests/` (tooling belongs in `Tools/`).
5. **Avalonia, not WPF.** `.axaml`, `IsVisible` not `Visibility`, prefer compiled bindings (`x:DataType`).
6. **No emoji as icons** in buttons/headers — use `PathIcon` geometries. Icon-only buttons get a `ToolTip.Tip`.

## UI / UX rules
- At-a-glance first: one header row (title left, max one primary action right), at most one toolbar
  row, filters as a single chip/segmented strip with a "Clear" that only shows when filters are active.
  More than 3 actions in a row/card → overflow `…` button with a `MenuFlyout`/`ContextMenu`.
- BPM, key, durations, bitrates, counts, positions use the mono/tabular font so columns don't jitter.
- AI/analysis-derived data (genre, mood, confidence) is shown as **bars/percentages, not coloured pills**.
  Small fixed-width badges are fine for Camelot key, BPM and format/quality tier.
- Every panel has designed empty / loading / error states — never a blank dark void.
- Row heights 32 (compact) / 40 (standard); radius 4 (inputs/badges) / 6–8 (cards). No bubbly 16px+.

## Architecture guardrails (enforced by tests in `Tests/SLSKDONET.Tests/Architecture/`)
- No `<DataGrid>` for track lists — use `VirtualGrid` / `VirtualizedTrackCollection`.
- ViewModels that subscribe to the event bus, collections or timers implement `IDisposable` and unsubscribe.
- Respect workstation timeline layout guards.

## Known pitfalls (each caused a real incident)
- `((Type)Prop)` casts inside Binding paths fail silently → dead commands.
- `RelayCommand<T>` with a mismatched parameter type crashes on unboxing (double→float).
- Never read StyledProperty getters inside `ICustomDrawOperation.Render` (render thread). Don't allocate
  brushes/pens in `Render`; cache them.
- `WindowState.FullScreen` + Mica/Acrylic kills the process; use Maximized + no decorations.
- A throwing constructor poisons a DI singleton for the app lifetime — non-critical services degrade, never throw.
- Never set `IWavePlayer.Volume` (it is the Windows master volume on WASAPI); use `VolumeSampleProvider`.
- Never seek `AudioFileReader` directly on FLAC/M4A; use `Services/Audio/ExactSeek`.
- Schema changes: never `dotnet ef migrations add`; patch via `SchemaMigratorService` raw SQL. `SchemaReconciler`
  adds anything the EF model has that a DB lacks, and `FreshDatabaseSchemaParityTests` guards new installs
  (see "Schema changes" in ARCHITECTURE.md).
- Tests must use the isolated DB (`ORBIT_DB_PATH` + `TestDatabaseSetup`, `OrbitPaths.LibraryDbPath`).
  Never touch the user's `%AppData%/ORBIT/config.ini` or live `library.db`.
- Don't launch/close the user's running ORBIT.exe unless a build genuinely needs the file unlocked.
- For "should work but doesn't" bugs, check first whether the feature was built but never wired.
