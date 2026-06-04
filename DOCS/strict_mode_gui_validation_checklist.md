# Strict-Mode GUI Validation Checklist

Status: Final Phase 2 GUI gate  
Date: 2026-05-21  
Scope: Settings + Downloads projection + per-track state projection for strict ON/OFF runs

## Purpose

Validate that strict-mode behavior is correctly projected through the GUI, not only in backend logs/tests.

## Preconditions

1. Build passes:
   - `dotnet build SLSKDONET.sln -nologo`
2. AutoDownload strict test bundle is green.
3. Evidence collector available:
   - [Tools/collect-strict-gate-evidence.ps1](Tools/collect-strict-gate-evidence.ps1)

## Surface 1: Settings Page

Target surfaces:

1. [Views/Avalonia/SettingsPage.axaml](Views/Avalonia/SettingsPage.axaml)
2. [ViewModels/SettingsViewModel.cs](ViewModels/SettingsViewModel.cs)

Checks:

1. Toggle `EnableAutoDownloadStrictMode` ON and OFF from GUI.
2. Toggle `AutoDownloadDiagnosticsEnabled` ON and OFF from GUI.
3. Restart app and confirm both toggles persist.
4. Confirm no binding errors in runtime output while toggling.

Pass criteria:

1. GUI state and persisted config stay aligned across restart.

## Surface 2: Downloads Page Projection

Target surfaces:

1. [ViewModels/Downloads/DownloadCenterViewModel.cs](ViewModels/Downloads/DownloadCenterViewModel.cs)
2. [Services/DownloadManager.cs](Services/DownloadManager.cs)

Checks:

1. Queue a track while strict mode is ON.
2. Observe row transition progression in GUI:
   - `Searching -> Downloading -> Completed` OR terminal failure (`Failed`/`No match`).
3. Confirm row leaves active lists when terminal and appears in completed/failed history as expected.
4. Confirm no stale `Searching` rows after terminal outcome.
5. Confirm no duplicate or ghost rows in active queue.

Pass criteria:

1. List membership and row states remain consistent with backend completion/failure outcomes.

## Surface 3: Per-Track VM State

Target surface:

1. [ViewModels/Downloads/UnifiedTrackViewModel.cs](ViewModels/Downloads/UnifiedTrackViewModel.cs)

Checks:

1. During strict ON run, per-track state text/progress reflect lifecycle transitions.
2. Progress updates continue during transfer.
3. Failed/no-match outcomes do not leave command surface in inconsistent state.
4. OFF run does not retain strict-mode-specific UI behavior from previous run.

Pass criteria:

1. Track row status/progress/actions are coherent for both ON and OFF runs.

## Strict ON GUI Pass

1. Set:
   - `EnableAutoDownloadStrictMode = true`
   - `AutoDownloadDiagnosticsEnabled = true`
2. Queue known downloadable track from GUI.
3. Observe UI lifecycle and final state.
4. Capture evidence window with script.

Expected supporting evidence:

1. Runtime log contains strict gate selection line.
2. `ActivityLogs` contains `autodownload_search_started` and terminal strict action (`autodownload_selected` or `autodownload_no_match`).

## Strict OFF GUI Pass

1. Set:
   - `EnableAutoDownloadStrictMode = false`
2. Queue comparable track from GUI.
3. Observe normal lifecycle and final state.
4. Capture evidence window with script.

Expected supporting evidence:

1. No strict selection lines for OFF window.
2. No new `autodownload_*` entries for OFF window.
3. Legacy discovery behavior used.

## Evidence Capture Commands

Use exact session windows for ON and OFF:

```powershell
pwsh -NoProfile -File Tools/collect-strict-gate-evidence.ps1 -Since "YYYY-MM-DD HH:mm:ss" -Until "YYYY-MM-DD HH:mm:ss" -Limit 30
```

## Defect Triggers (Fail Conditions)

1. Toggle values do not persist across restart.
2. Track row stuck in `Searching` after terminal backend outcome.
3. Progress values not updating during active transfer.
4. Queue/history lists out of sync with terminal state.
5. Strict OFF window still emits strict diagnostics/actions.

## Final GUI Gate Verdict

1. Strict ON GUI pass: PASS | FAIL
2. Strict OFF GUI pass: PASS | FAIL
3. GUI gate verdict: PASS only if both are PASS.
