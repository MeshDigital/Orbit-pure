# Sidebar and Like Feature Integration

## Summary
The application has undergone a significant architectural shift by promoting the **Contextual Sidebar** to a global shell component and moving its implementation into a dedicated `Features/LibrarySidebar` module. Additionally, the "Like" functionality has been integrated from the UI down to the database persistence layer.

## Architectural Changes
- **Module Relocation**: Sidebar ViewModels and Views moved from generic `ViewModels/Sidebar` and `Views/Avalonia/Sidebar` to `Features/LibrarySidebar`.
- **Global Integration**: `ContextualSidebarViewModel` is now managed by `MainViewModel`, allowing any part of the application (Library, Search, Playlists) to push track selections to the sidebar via an `IObservable` selection stream.
- **Like persistence Loop**:
    - `PlayerViewModel.ToggleLikeCommand` triggers the update.
    - `LibraryService.UpdateLikeStatusAsync` handles the business logic and cache invalidation.
    - `TrackRepository.UpdateLikeStatusAsync` persists the `IsLiked` status to the SQLite database.
    - **UI Polish**: Added an elastic pulsing animation to the heart icon in `PlayerControl.axaml` using Avalonia KeyFrame animations.

## Intelligence Engine
- **Similarity Module**: The `SonicMatchService` has been upgraded from a stub to a weighted Euclidean matching engine. It calculates proximity based on:
    - **Key**: ±1 Camelot step compatibility.
    - **BPM**: Industry-standard ±6% window.
    - **Energy**: EnergyScore proximity.
- **SideBar Reactivity**: `SimilaritySidebarViewModel` now performs real-time matching via `Task.Run` and updates the UI on the main thread when a track is selected globally.

## Resource Management
- **AnalysisWorker**: Implemented `IDisposable` in `AnalysisQueueService` (AnalysisWorker) to explicitly release Windows `PerformanceCounter` handles, ensuring system stability during long analysis sessions.

## Build Stability & Zero Warning Initiative (Feb 2026)
- **SkiaSharp Modernization**: Refactored `LiveBackground` rendering to use `SKImage`, eliminating obsolete API warnings and SkiaSharp-related build errors.
- **Null Safety Audit**: Applied exhaustive null checks and safe navigation patterns across the Hub and Forensic ViewModels, reaching a state of **Zero Warnings (0 Build Warnings)**.
- **URI Normalization**: Hardened `PathNormalizer` for cross-platform URI standards, ensuring reliable Rekordbox XML exports.
