# PerfBench (Epic #598 / PF0 #606)

Repeatable performance measurement harness for ORBIT's Library queries, scrolling virtualization, search, genre filtering, `LibraryGenreIndex` build, and Play-current-view list building at 5k–30k tracks.

## Quick Start

```bash
# 1. Generate a deterministic 30k-track synthetic library
dotnet run --project Tools/SyntheticLibraryGenerator -c Release -- --count 30000 --out ./scratch/synthetic-30k.db

# 2. Run the library benchmark suite
dotnet run --project Tests/PerfBench -c Release -- --db ./scratch/synthetic-30k.db --iterations 5
```

## Startup Marks

ORBIT logs `[StartupMark]` lines to `logs/run_*.txt` with elapsed milliseconds from `Program.Main`:

1. `DI built` — after `App.ConfigureServices()`
2. `Main window shown` — after `mainWindow.Show()`
3. `First Library page rendered` — when `VirtualizedTrackCollection` completes its first page fetch
4. `Dashboard snapshot shown` — when `HomeViewModel` applies its first `DashboardSnapshot`

Filter them from any run log with:

```powershell
Select-String -Path logs/run_*.txt -Pattern "\[StartupMark\]"
```

## Idle Timers & Hosted Services Inventory (Labs Off)

| Component | Type | Cadence / Trigger | Notes |
|---|---|---|---|
| `BackgroundJobWorker` | `BackgroundService` | Channel-driven (idle when empty) | Wakes only on queued jobs |
| `GhostAcquisitionOrchestrator` | `BackgroundService` | Periodic loop | Skips when auto-download disabled |
| `KnowledgeSwarmHost` | `BackgroundService` | Hosted service | Should stay off when Labs is disabled |
| `UiStallWatchdog` | `DispatcherTimer` | Periodic background tick | Monitors UI thread stalls |
| `ShellStateMonitor` | `DispatcherTimer` | Periodic background tick | Updates shell/tray status |
| `AmbientModeService` | `System.Timers.Timer` | 10 s | Checks idle ambient trigger |
| `AudioPlayerService._timer` | `System.Timers.Timer` | 50 ms (active playback) | Stopped when not playing |
| `DeckViewModel._positionPoll` | `System.Timers.Timer` | 30 ms (Workstation) | Active only when Workstation deck is playing |
| `DownloadCenterViewModel._uiBatchTimer` | `DispatcherTimer` | UI batching | Active during downloads |
| `GenreGalaxyCanvas` / `LiveBackground` / `VibeVisualizer` | `DispatcherTimer` | 33 ms (Render priority) | Active only when respective visual control is attached/visible |
