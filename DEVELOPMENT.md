dotnet restore
dotnet build
# Development Guide – SLSKDONET

## Environment Setup
- Install the .NET 8.0 SDK (or newer) and ensure `dotnet --info` reports the expected version.
- Preferred IDEs: Visual Studio 2022, JetBrains Rider, or VS Code with the C# extension.
- Clone the repository and open `SLSKDONET.sln` (Visual Studio/Rider) or the workspace file (`QMUSICSLSK.code-workspace`).

```powershell
dotnet restore
dotnet build
dotnet run --project SLSKDONET.csproj
```

Running from the solution (`dotnet run --project`) ensures the WPF application starts with the correct working directory for resource loading.

## Configuration & Database
- Configuration lives in `%AppData%\SLSKDONET\config.ini`; the file is generated on first launch with sensible defaults.
- Secure credential storage uses `ProtectedDataService`; passwords are encrypted when `RememberPassword` is enabled within the UI.
- SQLite database (`library.db`) is colocated in `%AppData%\SLSKDONET`; delete the file to reset the persistent library between sessions.
- Update settings through the `Settings` page (bound to `SaveSettingsCommand`) or edit the INI file manually and restart the app.

## Project Layout (Key Directories)

| Path | Purpose |
| --- | --- |
| `App.xaml` / `App.xaml.cs` | Application bootstrap, DI registration, global exception handling |
| `Views/MainWindow.xaml` | Navigation shell, status bar, hotkey bindings |
| `Views/*.xaml` | Page-level UI (Search, Imported, Downloads, Library, Settings, ImportPreview) |
| `Views/MainViewModel.cs` | Central view model exposing commands, orchestrated collections, diagnostics harness |
| `ViewModels/` | Supporting view models (`LibraryViewModel`, `ImportPreviewViewModel`, `PlaylistTrackViewModel`, etc.) |
| `Services/` | Soulseek integration, download orchestration, persistence helpers, metadata/tagging, CSV/Spotify input sources |
| `Data/` | EF Core `AppDbContext` and entity models for persistence |
| `Models/` | Domain models (Track, PlaylistJob, PlaylistTrack, LibraryEntry, OrchestratedQueryProgress) |
| `Converters/` | WPF value converters for status indicators |
| `Configuration/` | `AppConfig` and `ConfigManager` |
| `Themes/` | Resource dictionaries for the Windows 11 styled UI |

## Coding Patterns
- **MVVM**: `MainViewModel` (singleton) coordinates application state, while each page binds directly to this instance. Auxiliary view models (e.g., `LibraryViewModel`) subscribe to `DownloadManager` events for domain-specific projections.
- **Dependency Injection**: All services are registered in `App.ConfigureServices`; avoid manual `new` where DI can provide an instance. Use interfaces (`ILibraryService`, `INavigationService`) for testability.
- **Async/Await**: Use `AsyncRelayCommand` for long-running interactions. Cancellation tokens are surfaced for search/import operations (`_searchCts`).
- **Persistence**: All writes to SQLite go through `LibraryService`/`DatabaseService`. When adding fields to models, mirror them in EF entities and update conversion helpers.
- **Dispatcher Safety**: UI collections (`ObservableCollection<T>`) are updated via dispatcher helpers (`InvokeOnUi`, `InvokeOnUiAction`) to avoid cross-thread exceptions.

## Working on Features
1. **Add or adjust models** in `Models/` and entity mappings in `Data/` as needed.
2. **Create/extend services**; register them in `App.ConfigureServices` to leverage DI.
3. **Expose state/commands** via `MainViewModel` or a dedicated view model; ensure `INotifyPropertyChanged` notifications fire correctly.
4. **Bind to UI** by updating the relevant XAML page; use existing styles from `Themes/` for consistent look and feel.
5. **Persist data** using `LibraryService` to keep playlist/job indices in sync with UI state.

Example – adding a new playlist importer:
- Implement `IInputSource` to parse the external resource.
- Register it (`services.AddSingleton<NewInputSource>()`).
- Surface a command in `MainViewModel` to invoke the parser and merge results into `ImportedQueries`.
- Optionally extend the import preview UI for specialized metadata.

## Diagnostics & Logging
- Press `Ctrl+R` to run the diagnostics harness; it creates a temporary playlist, persists it, verifies UI hydration, then cleans up temp files.
- Elevate logging verbosity by editing `App.ConfigureServices` and setting `config.SetMinimumLevel(LogLevel.Debug)`.
- Use the harness when modifying library persistence, download orchestration, or dispatcher logic to ensure guards remain intact.

## Troubleshooting
- **Build failures**: run `dotnet clean`, delete `bin/` + `obj/`, and rebuild. Ensure NuGet feeds are reachable (`dotnet restore`).
- **Database mismatches**: remove `%AppData%\SLSKDONET\library.db` to force EF Core to recreate the schema.
- **Configuration resets**: delete `%AppData%\SLSKDONET\config.ini` to regenerate defaults.
- **Soulseek connection issues**: confirm credentials and port in `config.ini`; check firewall rules; use status text for live feedback.
- **UI binding problems**: verify `MainViewModel` properties raise `PropertyChanged`; ensure new properties are public and commands expose `CanExecute` when necessary.

## Roadmap & Open Items
- Implement pause/resume plus richer cancellation states in `DownloadManager`.
- Wire rate limiting and Spotify advanced filters for large playlist imports.
- Expand diagnostics harness to cover metadata tagging and Rekordbox export scenarios.
- Introduce automated tests (service-level first) and continuous integration builds.
- Address the pending `System.Text.Json` security advisory once dependency compatibility is validated.
