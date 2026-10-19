# First-run setup wizard and quick tour

New installs get a short setup wizard (Soulseek account, folders, optional Spotify, audio and
analysis) and an optional coach-mark tour of the main pages. Existing installs never see the
wizard unless they ask for it.

## When the wizard appears

`Services/Setup/FirstRunCoordinator` runs once at startup, right after the database is ready and
before the main window opens. It gathers signals and hands them to the pure
`FirstRunDetector.Evaluate`:

| Signal | Meaning |
|---|---|
| `[Setup] Completed = True` | Already done: no wizard (`AlreadyCompleted`) |
| Soulseek username in config, or a stored password | Existing install |
| Download folder that isn't the built-in fallback (`~/Downloads/SLSKDONET`), or a shared folder | Existing install |
| Any track in `LibraryEntries`/`Tracks`, or a Library Source other than the download folder | Existing install |
| None of the above | Fresh install: show the wizard |

An existing install is marked `[Setup] Completed = True` / `Version = 1` silently and keeps
working as before. Finishing or skipping the wizard sets the same flag.

## Steps and what they write

Edits stay on each step (Back keeps them). Nothing is saved until **Next** on that step.
"Skip this step" saves nothing for that step. "Skip setup" (or Esc, then confirm) closes the
wizard, marks it completed and shows a toast that it can be reopened from Settings.

| Step | Writes (on Next) | Through |
|---|---|---|
| Welcome | nothing | |
| Soulseek (recommended) | `[Soulseek] Username`, `RememberPassword`, `AutoConnectEnabled`; stored password (or deletes it) | `SoulseekSetupGateway` → `ConfigManager`, `ISoulseekCredentialService` |
| Folders (recommended) | `[Download] Directory`, `SharedFolder`, `[Library] EnableLibrarySharing`, `[EditLab] OutputDirectory`; new Library Sources (with watch flag); creates missing folders | `ConfigSetupSettingsGateway`, `LibraryFolderSetupGateway` |
| Connections | Spotify tokens, only when "Connect Spotify" is pressed | `SettingsViewModel.ConnectSpotifyAsync` (same flow as Settings) |
| Audio and analysis | `[Playback] AudioOutputMode`, `AudioOutputDeviceName`, `[Analysis] AnalyzeNewDownloads` | `ConfigSetupSettingsGateway` (+ `IAudioPlayerService.ApplyOutputSettings`) |
| Done | nothing; "Scan now" opens the import sheet over every Library Source (phases, Cancel, Run in background, summary) | `ILibraryImportLauncher` → `LibraryImportViewModel` → `LibraryFolderScannerService.ImportFoldersAsync` |

**Test connection** signs in for real through `IConnectionLifecycleService` (the same path as the
login dialog), so a successful test leaves ORBIT connected. Next without a successful test runs
the test first and stays on the step if it fails. Results (`SoulseekLoginResultMapper`):

| Server answer | Shown as |
|---|---|
| Logged in | Connected (a new name is now registered to you) |
| `INVALIDPASS` | Wrong password, or the name is taken. Soulseek returns the same answer for both |
| `INVALIDUSERNAME` | Username not allowed |
| Refused / no such host / timeout / no answer within 30 s | Server unreachable |
| `SVRFULL`, cooldown after a kick, listen port in use | Their own messages |

A failed test restores the previous username in memory and saves nothing.

New settings that the wizard introduced are also in Settings, so both pages show the same value:
Downloads → **Edit Lab output folder** (empty = `<download folder>/ORBIT Edits`, honoured by
`EditLabRenderService`), Library → **Analyse new downloads automatically** (gates the
post-download analysis request in `DownloadManager`).

FFmpeg uses the Settings check and "Get FFmpeg" link. The analysis models ship in
`Tools/Essentia/models`; ORBIT has no download step for them, so the wizard reports which are
missing (`AnalysisModelInventory`) instead of offering a fake fix.

## Quick tour

`QuickTourViewModel` lists ten steps (Search, Downloads/Inbox, Import, Library, Analysis, Cue Forge,
Flow Builder, Edit Lab, Workstation, Settings). Each names the `x:Name` of its control in
`MainWindow.axaml` (`NavSearch`, `NavInbox`, …; Settings falls back to `TopBarSettings`).
`Views/Avalonia/Setup/TourOverlay` asks the window for each control and leaves out steps whose
control isn't visible (collapsed sidebar or group). Positions come from the real bounds on every
layout pass, so resize and collapse are followed. Next / Back / Skip tour, "3 / 10",
Esc = skip, Enter or Right = next, Left = back. Finishing or skipping sets `[Setup] TourCompleted`.

## Reopening

Settings → Advanced → **Setup**: "Run setup again" (publishes `OpenSetupWizardRequestedEvent`) and
"Take the tour" (`StartTourRequestedEvent`). `SetupShellViewModel` (as `MainViewModel.Setup`)
handles both. A rerun shows the current values; an already connected, unchanged account is kept
without signing in again. After the wizard closes, `ConnectionViewModel.ReloadFromConfig` and
`SettingsViewModel.RefreshFromConfig` pick up what it saved.

## Previewing safely (separate profile)

```
ORBIT.exe --setup-preview          # brand-new temp profile under %TEMP%\ORBIT-setup-preview\<time>
ORBIT.exe --profile D:\orbit-test  # a profile folder you choose (kept between runs)
```

Both set `ORBIT_PROFILE_DIR`. `OrbitPaths.RoamingRoot` / `LocalRoot` then point config.ini,
library.db, stored Soulseek/Spotify credentials, workstation session, column/keyboard layouts,
saved doubles, discover cache, track logs and backups at that folder. The real
`%AppData%\ORBIT\config.ini` and `library.db` are not opened. Shared caches (artwork, stems,
playback conversions) stay shared. A profile instance also picks a free Soulseek listen port, so
it can run next to the real ORBIT. Don't sign in to Soulseek in the preview with the account the
real ORBIT uses: the server allows one session per name and would disconnect the other one. The
wizard shows a warning about this on a preview profile.

## Code map

- `Services/Setup/`: `FirstRunDetector`, `FirstRunCoordinator`, `SoulseekLoginResult(Mapper)`,
  `SetupFolderService`, `SetupContracts` (interfaces, events), `SetupGateways`, `PortProbe`
- `ViewModels/Setup/`: `SetupWizardViewModel`, step VMs, `QuickTourViewModel`,
  `SetupShellViewModel`, `SettingsBackedSetupGateways` (Spotify, audio)
- `Views/Avalonia/Setup/`: `SetupWizardView`, `TourOverlay`
- `Configuration/StartupArguments.cs`, `Data/OrbitPaths.cs`
- Tests: `Tests/SLSKDONET.Tests/Setup/` (temp config and folders only)
