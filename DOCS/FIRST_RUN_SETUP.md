# First-run setup wizard and quick tour

New installs get a short setup wizard (Soulseek account, folders, optional Spotify, audio and
analysis) and an optional coach-mark tour of the main pages. An install that completed an older
wizard version — or predates the wizard — gets a short **What's new** re-onboarding instead of the
full flow; the full wizard only appears on a genuinely fresh install or when asked for it.

## When the wizard appears

`Services/Setup/FirstRunCoordinator` runs once at startup, right after the database is ready and
before the main window opens. It gathers signals and hands them to the pure
`FirstRunDetector.Evaluate`:

| Signal | Meaning |
|---|---|
| `[Setup] Completed = True`, `Version` ≥ `CurrentSetupVersion` | Already done: no wizard (`AlreadyCompleted`) |
| `[Setup] Completed = True`, `Version` < `CurrentSetupVersion` | Completed an older wizard: show **What's new** (`UpgradeWizard`) |
| Soulseek username in config, or a stored password | Existing install (pre-wizard): show **What's new** |
| Download folder that isn't the built-in fallback (`~/Downloads/SLSKDONET`), or a shared folder | Existing install (pre-wizard) |
| Any track in `LibraryEntries`/`Tracks`, or a Library Source other than the download folder | Existing install (pre-wizard) |
| None of the above | Fresh install: show the full wizard |

Finishing or skipping any flow sets `[Setup] Completed = True` and writes the current
`FirstRunDetector.CurrentSetupVersion`, so the popup does not reappear until the next release bumps it.

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
| Metadata and artwork | `[Enrichment] EnabledProviders`, `ProviderOrder`, `AutoRun`; Discogs token / AcoustID key in the DPAPI key store | `EnrichmentSetupGateway` (same keys as Settings → Connection → Metadata sources) |
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

## Import and enrichment during setup

- **Folders**: with new library folders, "Import these folders when I press Next" (default on)
  starts the import in the **background** (`ILibraryImportLauncher.StartBackgroundImportAsync`,
  no locking sheet). The wizard carries on; Done then doesn't offer a second "Scan now".
- **Metadata and artwork** (after Connections, step 5 of 7): per source a toggle and one
  "gives · needs · limit" line (file tags always on; Deezer and MusicBrainz + Cover Art Archive
  on; Discogs token with "Get a token"; AcoustID key + fpcalc state; Spotify shows the state
  the Connections step left; Beatport off, experimental) and "Enrich automatically after
  imports". Saved on Next. **Start now** is enabled once the library has tracks (the count
  follows a running import); it saves first, then starts the orchestrator and shows the meter,
  done/total, per-source status (matched / waiting / paused for its rate limit / disabled) and
  Pause / Resume inline. Leaving the step or closing the wizard never stops it.
- **Done**: "Start enrichment" if it wasn't started, and "Running in the background" with live
  counts for import / enrichment / analysis, plus where to follow them later.

## Background activity

`ViewModels/Activity/BackgroundActivityHubViewModel` is the one indicator for long-running
jobs: library import, metadata enrichment, analysis and loudness backfill. A top-bar chip
(`Views/Avalonia/Controls/BackgroundActivityChip`, also in the wizard header) with a flyout per
job: progress, counts, status, Pause/Resume (enrichment) and Open (import summary sheet /
Analysis page). It replaces the import sheet's own "Run in background" chip. Sources publish
from their own threads; the hub keeps the latest snapshot and applies it on the UI thread at
most every 250 ms.

## Stability with everything running

The sequence Folders → import 3,000+ tracks → keep going → Start enrichment → finish → tour →
browse the Library while import, analysis and enrichment run:

- The scanner publishes `LibraryEntriesAddedEvent` per committed batch (500 rows). All Tracks
  shows them every ~2 s with tag (or file-name) titles: visible pages are re-read at their new
  offsets before the swap, rows keep their view model, newest-first raises Add at 0 (selection
  and scroll stay). Header counts follow every ~3 s.
- Enrichment / analysis / loudness row updates are coalesced per list: one batched query off
  the UI thread and one UI post per 250 ms. The row cache is thread-safe.
- Enrichment writes re-read the row in one IMMEDIATE transaction and fill only fields still
  empty (never BPM/key/energy). SQLITE_BUSY/LOCKED on import batches and enrichment writes is
  retried on a fresh context (`Data/SqliteBusyRetry`).
- A run started mid-import follows the import's batches; the completion trigger skips tracks
  finished in the last 7 days, so nothing is queued twice. While an import writes rows
  enrichment runs two at a time, while analysis has work one at a time (it used to stop).

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
