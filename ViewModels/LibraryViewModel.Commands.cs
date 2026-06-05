using System;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.ObjectModel;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Services.Models;
using SLSKDONET.Services;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Views;
using SLSKDONET.Events;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.ViewModels;

public partial class LibraryViewModel
{
    // Commands delegate to child view models or orchestration paths and are assigned in InitializeCommands().
    public ICommand ViewHistoryCommand { get; set; } = null!;
    public ICommand OpenSourcesCommand { get; set; } = null!;
    public ICommand ToggleEditModeCommand { get; set; } = null!;
    public ICommand ToggleActiveDownloadsCommand { get; set; } = null!;
    public ICommand ToggleNavigationCommand { get; set; } = null!;
    public ICommand ExpandNavigationCommand { get; set; } = null!;
    public ICommand CollapseNavigationCommand { get; set; } = null!;
    
    public ICommand PlayTrackCommand { get; set; } = null!;
    public ICommand RefreshLibraryCommand { get; set; } = null!;
    public ICommand DeleteProjectCommand { get; set; } = null!;
    public ICommand PlayAlbumCommand { get; set; } = null!;
    public ICommand DownloadAlbumCommand { get; set; } = null!;
    public ICommand DownloadMissingCommand { get; set; } = null!;
    public ICommand RenameProjectCommand { get; set; } = null!;
    public ICommand DuplicateDetectionCommand { get; set; } = null!;
    public ICommand AutoOrganizeCommand { get; set; } = null!;
    public ICommand LoadDeletedProjectsCommand { get; set; } = null!;
    public ICommand RestoreProjectCommand { get; set; } = null!;
    public ICommand SyncProjectCommand { get; set; } = null!;
    public ICommand ExportPlaylistCommand { get; set; } = null!;

    public ICommand SwitchWorkspaceCommand { get; set; } = null!;
    public ICommand ToggleColumnCommand { get; set; } = null!;
    public ICommand ResetViewCommand { get; set; } = null!;

    public ICommand SyncPhysicalLibraryCommand { get; set; } = null!;
    public ICommand CreateSmartPlaylistCommand { get; set; } = null!;
    public ICommand OpenFlowBuilderCommand { get; set; } = null!;
    public ICommand ExportMonthlyDropCommand { get; set; } = null!;
    public ICommand FindBridgeBetweenSelectedCommand { get; set; } = null!;
    public ICommand SmartInsertBetweenSelectedCommand { get; set; } = null!;
    public ICommand SmartInsertAfterTrackCommand { get; set; } = null!;
    public ICommand PrepareSmartInsertAfterTrackCommand { get; set; } = null!;
    public ICommand ApplyPreparedSmartInsertCommand { get; set; } = null!;
    public ICommand PreviewSmartInsertContextCommand { get; set; } = null!;
    public ICommand SetSmartInsertStrictPresetCommand { get; set; } = null!;
    public ICommand SetSmartInsertNormalPresetCommand { get; set; } = null!;
    public ICommand SetSmartInsertLoosePresetCommand { get; set; } = null!;
    public ICommand SetLibraryIntelligenceTabCommand { get; set; } = null!;
    public ICommand FavoriteSelectedPairAsDoubleCommand { get; set; } = null!;
    public ICommand ActivateSavedDoubleCommand { get; set; } = null!;
    public ICommand RemoveSavedDoubleCommand { get; set; } = null!;
    public ICommand ViewAllSavedDoublesCommand { get; set; } = null!;
    public ICommand SuggestNextCandidateCommand { get; set; } = null!;
    public ICommand PlaylistUpgradeCandidateCommand { get; set; } = null!;
    public ICommand OpenSelectedPairInWorkstationCommand { get; set; } = null!;
    public ICommand AnalyzeSelectedPairCommand { get; set; } = null!;

    public ICommand SmartEscapeCommand { get; set; } = null!;

    // ── Batch Action FAB (Task 10.5) ────────────────────────────────────────
    public ICommand BatchTagEditCommand { get; set; } = null!;
    public ICommand BatchQueueAnalysisCommand { get; set; } = null!;
    public ICommand BatchAddToPlaylistCommand { get; set; } = null!;
    public ICommand BatchExportRekordboxCommand { get; set; } = null!;
    public ICommand BatchClearSelectionCommand { get; set; } = null!;




    partial void InitializeCommands()
    {
        ViewHistoryCommand = new AsyncRelayCommand(ExecuteViewHistoryAsync);
        OpenSourcesCommand = new RelayCommand<object>(param => 
        {
            if (param?.ToString() == "Close") IsSourcesOpen = false;
            else IsSourcesOpen = true;
        });
        ToggleEditModeCommand = new RelayCommand(() => IsEditMode = !IsEditMode);
        ToggleActiveDownloadsCommand = new RelayCommand(() => IsActiveDownloadsVisible = !IsActiveDownloadsVisible);
        ToggleNavigationCommand = new RelayCommand(ExecuteToggleNavigation);
        ExpandNavigationCommand = new RelayCommand(ExecuteHoverExpandNavigation);
        CollapseNavigationCommand = new RelayCommand(ExecuteHoverCollapseNavigation);
        
        PlayTrackCommand = new AsyncRelayCommand<object>(ExecutePlayTrackAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(ExecuteRefreshLibraryAsync);
        DeleteProjectCommand = new AsyncRelayCommand<object>(ExecuteDeleteProjectAsync);
        PlayAlbumCommand = new AsyncRelayCommand<object>(ExecutePlayAlbumAsync);
        DownloadAlbumCommand = new AsyncRelayCommand<object>(ExecuteDownloadAlbumAsync);
        DownloadMissingCommand = new AsyncRelayCommand<object>(ExecuteDownloadMissingAsync);
        RenameProjectCommand = new AsyncRelayCommand<object>(ExecuteRenameProjectAsync);
        SyncProjectCommand = new AsyncRelayCommand<object>(ExecuteSyncProjectAsync);
        ExportPlaylistCommand = new AsyncRelayCommand<object>(ExecuteExportPlaylistAsync);


        // Fluidity
        SwitchWorkspaceCommand = new RelayCommand<ActiveWorkspace>(ws => CurrentWorkspace = ws);

        DuplicateDetectionCommand = new AsyncRelayCommand(ExecuteDuplicateDetectionAsync);
        AutoOrganizeCommand = new AsyncRelayCommand(ExecuteAutoOrganizeAsync);
        SyncPhysicalLibraryCommand = new AsyncRelayCommand(ExecuteSyncPhysicalLibraryAsync);
        CreateSmartPlaylistCommand = SmartPlaylists.CreateCrateCommand;
        OpenFlowBuilderCommand = new RelayCommand(ExecuteOpenFlowBuilder);
        ExportMonthlyDropCommand = new AsyncRelayCommand(ExecuteExportMonthlyDropAsync);
        FindBridgeBetweenSelectedCommand = new RelayCommand(ExecuteFindBridgeBetweenSelected);
        SmartInsertBetweenSelectedCommand = new AsyncRelayCommand(ExecuteSmartInsertBetweenSelectedAsync);
        SmartInsertAfterTrackCommand = new AsyncRelayCommand<object>(ExecuteSmartInsertAfterTrackAsync);
        PrepareSmartInsertAfterTrackCommand = new AsyncRelayCommand<object>(ExecutePrepareSmartInsertAfterTrackAsync);
        ApplyPreparedSmartInsertCommand = Intelligence.ApplyPreparedSmartInsertCommand;
        PreviewSmartInsertContextCommand = new RelayCommand<object>(ExecutePreviewSmartInsertContext);
        SetSmartInsertStrictPresetCommand = Intelligence.SetSmartInsertStrictPresetCommand;
        SetSmartInsertNormalPresetCommand = Intelligence.SetSmartInsertNormalPresetCommand;
        SetSmartInsertLoosePresetCommand = Intelligence.SetSmartInsertLoosePresetCommand;
        SetLibraryIntelligenceTabCommand = Intelligence.SetLibraryIntelligenceTabCommand;
        FavoriteSelectedPairAsDoubleCommand = new AsyncRelayCommand(ExecuteFavoriteSelectedPairAsDoubleAsync);
        ActivateSavedDoubleCommand = new RelayCommand<object>(ExecuteActivateSavedDouble);
        RemoveSavedDoubleCommand = new AsyncRelayCommand<object>(ExecuteRemoveSavedDoubleAsync);
        ViewAllSavedDoublesCommand = new RelayCommand(ExecuteViewAllSavedDoubles);
        SuggestNextCandidateCommand = new RelayCommand<object>(ExecuteSuggestNextCandidate);
        PlaylistUpgradeCandidateCommand = new RelayCommand<object>(ExecutePlaylistUpgradeCandidate);
        OpenSelectedPairInWorkstationCommand = new RelayCommand(ExecuteOpenSelectedPairInWorkstation);
        AnalyzeSelectedPairCommand = new RelayCommand(ExecuteAnalyzeSelectedPair);
        ToggleColumnCommand = new RelayCommand<ColumnDefinition>(ExecuteToggleColumn);
        ResetViewCommand = new RelayCommand(ExecuteResetView);
        SwitchWorkspaceCommand = new RelayCommand<ActiveWorkspace>(ExecuteSwitchWorkspace);
        SmartEscapeCommand = new RelayCommand(ExecuteSmartEscape);

        // Batch Action FAB
        BatchTagEditCommand = new AsyncRelayCommand(ExecuteBatchTagEditAsync);
        BatchQueueAnalysisCommand = new AsyncRelayCommand(ExecuteBatchQueueAnalysisAsync);
        BatchAddToPlaylistCommand = new AsyncRelayCommand(ExecuteBatchAddToPlaylistAsync);
        BatchExportRekordboxCommand = new AsyncRelayCommand(ExecuteBatchExportRekordboxAsync);
        BatchClearSelectionCommand = new RelayCommand(() => Tracks.ClearSelection());
    }

    private void ExecuteToggleNavigation()
    {
        var willCollapse = !IsNavigationCollapsed;
        IsNavigationCollapsed = !IsNavigationCollapsed;
        _ = PersistNavigationCollapsedStateAsync();

        if (willCollapse)
        {
            RegisterManualNavigationCollapse();
        }
    }

    private void ExecuteHoverExpandNavigation()
    {
        if (!IsNavigationHoverAutoHideArmed)
        {
            return;
        }

        IsNavigationCollapsed = false;
    }

    private void ExecuteHoverCollapseNavigation()
    {
        if (!IsNavigationHoverAutoHideArmed)
        {
            return;
        }

        IsNavigationCollapsed = true;
    }

    private async Task PersistNavigationCollapsedStateAsync()
    {
        try
        {
            _appConfig.LibraryNavigationCollapsed = IsNavigationCollapsed;
            await _configManager.SaveAsync(_appConfig);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist library navigation collapsed state");
        }
    }

    internal async Task PersistLibrarySmartInsertConfigAsync()
    {
        try
        {
            if (_appConfig is null || _configManager is null)
                return;

            _appConfig.LibrarySmartInsertMinConfidence = Math.Clamp(_appConfig.LibrarySmartInsertMinConfidence, 0.0, 1.0);
            _appConfig.LibrarySmartInsertStructureSensitivity = Math.Clamp(_appConfig.LibrarySmartInsertStructureSensitivity, 0, 100);
            await _configManager.SaveAsync(_appConfig);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist library smart insert settings");
        }
    }

    private void ExecuteSetSmartInsertStrictPreset()
        => ApplySmartInsertPreset(0.80, 85, "Strict");

    private void ExecuteSetSmartInsertNormalPreset()
        => ApplySmartInsertPreset(0.72, 55, "Normal");

    private void ExecuteSetSmartInsertLoosePreset()
        => ApplySmartInsertPreset(0.65, 30, "Loose");

    private void ExecuteSetLibraryIntelligenceTab(object? parameter)
    {
        var tab = parameter?.ToString();
        FocusLibraryIntelligenceTab(tab ?? "SmartInsert");
    }

    private void ExecutePreviewSmartInsertContext(object? parameter)
    {
        if (parameter is SmartInsertContextRequest contextRequest
            && contextRequest.FromTrack.Model is PlaylistTrack fromModel
            && contextRequest.ToTrack.Model is PlaylistTrack toModel)
        {
            Intelligence.SetSmartInsertPreparationHint(fromModel, toModel);
            return;
        }

        Intelligence.ClearSmartInsertPreparationHint();
    }

    private void ApplySmartInsertPreset(double minConfidence, int structureSensitivity, string presetName)
    {
        Intelligence.ApplySmartInsertPreset(minConfidence, structureSensitivity);

        _notificationService.Show(
            "Smart Insert Preset",
            $"{presetName} preset applied ({Intelligence.LibrarySmartInsertMinConfidence:F2}, {Intelligence.LibrarySmartInsertStructureSensitivity}%).",
            NotificationType.Information);
    }

    private void ExecuteViewAllSavedDoubles()
    {
        SavedDoublesSidebarFocusRequestVersion++;
    }

    private void ExecuteSuggestNextCandidate(object? parameter)
    {
        // Slice 10 Commit 1 scaffold: row click is intentionally passive.
    }

    private void ExecutePlaylistUpgradeCandidate(object? parameter)
    {
        if (parameter is not PlaylistUpgradeCandidateViewModel candidate || candidate.Track is null)
            return;

        Tracks.UpdateSelection(new[] { candidate.Track });
        FocusLibraryIntelligenceTab(IntelligenceTabUpgrade);
    }

    private async Task ExecuteViewHistoryAsync()
    {
        await _importHistoryViewModel.LoadHistoryAsync();
        _navigationService.NavigateTo(PageType.Import);
    }

    private async Task ExecutePlayTrackAsync(object? param)
    {
        if (param is PlaylistTrackViewModel trackVM)
        {
            Operations.PlayTrackCommand.Execute(trackVM);
        }
    }

    private async Task ExecuteRefreshLibraryAsync()
    {
        try 
        {
            IsLoading = true;
            await _libraryCacheService.ClearCacheAsync();
            await Projects.LoadProjectsAsync();
            
            // Phase 18: Also reload tracks for the currently selected project
            var currentProject = SelectedProject;
            if (currentProject != null)
            {
                await Tracks.LoadProjectTracksAsync(currentProject);
                
                // Defensive check: SelectedProject might have changed during await
                int trackCount = Tracks.CurrentProjectTracks?.Count ?? 0;
                
                _notificationService.Show("Library Refreshed", 
                    $"Project '{currentProject.SourceTitle}' reloaded with {trackCount} tracks.", 
                    NotificationType.Success);
            }
            else
            {
                _notificationService.Show("Library Refreshed", "Project list updated from database.", NotificationType.Success);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh library");
            _notificationService.Show("Refresh Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteDeleteProjectAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            bool confirm = await _dialogService.ConfirmAsync(
                "Remove Playlist",
                $"Remove '{project.SourceTitle}' from the playlist list? Tracks and downloaded files will be kept in the library.");
            
            if (confirm)
            {
                try 
                {
                    await _libraryService.DeletePlaylistJobAsync(project.Id);
                    await Projects.LoadProjectsAsync();
                    _notificationService.Show("Project Deleted", project.SourceTitle, NotificationType.Success);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete project");
                    _notificationService.Show("Delete Failed", ex.Message, NotificationType.Error);
                }
            }
        }
    }

    private async Task ExecuteLoadDeletedProjectsAsync()
    {
        try
        {
            var deleted = await _libraryService.LoadDeletedPlaylistJobsAsync();
            DeletedProjects.Clear();
            foreach (var p in deleted) DeletedProjects.Add(p);
            IsRemovalHistoryVisible = true;
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to load deleted projects");
        }
    }

    private async Task ExecuteRestoreProjectAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            try
            {
                await _libraryService.RestorePlaylistJobAsync(project.Id);
                await Projects.LoadProjectsAsync();
                DeletedProjects.Remove(project);
                if (!DeletedProjects.Any()) IsRemovalHistoryVisible = false;
                _notificationService.Show("Project Restored", project.SourceTitle, NotificationType.Success);
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Failed to restore project");
            }
        }
    }

    private async Task ExecutePlayAlbumAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            if (tracks.Any())
            {
                 _notificationService.Show("Playing Album", project.SourceTitle, NotificationType.Information);
            }
        }
    }

    private async Task ExecuteDownloadAlbumAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            if (tracks.Any())
            {
                _notificationService.Show("Queueing Download", $"{tracks.Count} tracks from {project.SourceTitle}", NotificationType.Information);
                
                // Force Priority 0 so they hit the top of the queue immediately
                foreach (var t in tracks) t.Priority = 0;
                
                _downloadManager.QueueTracks(tracks);
            }
        }
    }

    private async Task ExecuteDownloadMissingAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            var missing = tracks.Where(t => t.Status == TrackStatus.Missing || t.Status == TrackStatus.Failed).ToList();
            if (missing.Any())
            {
                _notificationService.Show("Queueing Missing Tracks", $"{missing.Count} missing tracks from {project.SourceTitle}", NotificationType.Information);
                
                // Force Priority 0
                foreach (var t in missing) t.Priority = 0;
                
                _downloadManager.QueueTracks(missing);
            }
            else
            {
                _notificationService.Show("Download Missing", "All tracks are already downloaded or queued.", NotificationType.Information);
            }
        }
    }



    private async Task ExecuteAutoSortAsync()
    {
        try
        {
            IsLoading = true;
            _notificationService.Show("Auto-Sorting", "Analyzing library styles...", NotificationType.Information);
            
            var tracks = await _libraryService.LoadAllLibraryEntriesAsync();
            int updated = 0;
            
            foreach (var track in tracks)
            {
                if (string.IsNullOrEmpty(track.DetectedSubGenre))
                {
                    /*
                    var result = await _personalClassifier.ClassifyTrackAsync(track.FilePath);
                    if (result.Confidence > 0.7)
                    {
                        track.DetectedSubGenre = result.Label;
                        await _libraryService.SaveOrUpdateLibraryEntryAsync(track);
                        updated++;
                    }
                    */
                }
            }
            
            _notificationService.Show("Sort Complete", $"Categorized {updated} tracks.", NotificationType.Success);
            await Projects.LoadProjectsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-sort failed");
            _notificationService.Show("Sort Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }




    private async Task ExecuteRenameProjectAsync(object? param)
    {
        if (param is not PlaylistJob project)
        {
            project = SelectedProject!;
        }

        if (project == null) return;

        var newTitle = await _dialogService.ShowPromptAsync(
            "Rename Project",
            $"Enter a new name for '{project.SourceTitle}':",
            project.SourceTitle);

        if (!string.IsNullOrWhiteSpace(newTitle) && newTitle != project.SourceTitle)
        {
            try
            {
                var oldTitle = project.SourceTitle;
                project.SourceTitle = newTitle;
                await _libraryService.SavePlaylistJobAsync(project);
                
                _notificationService.Show("Project Renamed", $"'{oldTitle}' is now '{newTitle}'", NotificationType.Success);
                
                // Refresh project list
                await Projects.LoadProjectsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rename project {Id}", project.Id);
                _notificationService.Show("Rename Failed", ex.Message, NotificationType.Error);
            }
        }
    }

    private void ExecuteToggleColumn(ColumnDefinition? column)
    {
        if (column == null) return;
        column.IsVisible = !column.IsVisible;
        _columnConfigService.SaveConfiguration(AvailableColumns.ToList());
    }

    private async Task ExecuteResetViewAsync()
    {
        bool confirm = await _dialogService.ConfirmAsync(
            "Reset Studio View",
            "This will restore the default column layout. Are you sure?");
        
        if (confirm)
        {
            AvailableColumns.Clear();
            var defaults = _columnConfigService.GetDefaultConfiguration();
            foreach (var col in defaults) AvailableColumns.Add(col);
            _columnConfigService.SaveConfiguration(defaults);
            _notificationService.Show("View Reset", "Studio default layout restored.", NotificationType.Information);
        }
    }

    public void OnColumnLayoutChanged()
    {
        // Called from View when columns are reordered or resized
        _columnConfigService.SaveConfiguration(AvailableColumns.ToList());
    }

    private async Task ExecuteSyncProjectAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            Projects.SyncProjectCommand.Execute(project);
        }
        else if (SelectedProject != null)
        {
            Projects.SyncProjectCommand.Execute(SelectedProject);
        }
    }

    private async Task ExecuteInitiateMp3SearchAsync(object? param)
    {
        var tracksToSearch = new List<PlaylistTrackViewModel>();

        if (param is PlaylistTrackViewModel trackVM)
        {
            tracksToSearch.Add(trackVM);
        }
        else if (param is System.Collections.IEnumerable enumerable)
        {
            tracksToSearch.AddRange(enumerable.Cast<PlaylistTrackViewModel>());
        }
        else
        {
            // Default to selection
            tracksToSearch.AddRange(Tracks.SelectedTracks);
        }

        var onHoldTracks = tracksToSearch.Where(t => t.Model.Status == TrackStatus.OnHold).ToList();

        if (!onHoldTracks.Any())
        {
            _notificationService.Show("MP3 Search", "No 'On Hold' tracks selected. Manual MP3 search is only for tracks that failed all FLAC attempts.", NotificationType.Information);
            return;
        }

        bool confirm = await _dialogService.ConfirmAsync(
            "Initiate MP3 Search",
            $"Are you sure you want to search for MP3 versions of {onHoldTracks.Count} track(s)? This will unpause them and prioritize MP3 in the search results.");

        if (confirm)
        {
            foreach (var track in onHoldTracks)
            {
                // Unpause and let DownloadManager pick it up. 
                // DownloadDiscoveryService will see Status == OnHold and filter for MP3.
                track.Model.IsUserPaused = false;
            }

            _downloadManager.QueueTracks(onHoldTracks.Select(t => t.Model).ToList());
            _notificationService.Show("MP3 Search Initiated", $"Queueing {onHoldTracks.Count} tracks for MP3 search.", NotificationType.Success);
        }

    }

    private async Task ExecuteAutoOrganizeAsync()
    {
        try
        {
            IsLoading = true;
            _notificationService.Show("Auto-Organizer", "Scanning library for organization...", NotificationType.Information);
            
            var entries = await _libraryService.LoadAllLibraryEntriesAsync();
            int movedCount = 0;
            int errorCount = 0;
            
            var targetRoot = _appConfig.DownloadDirectory;
            if (string.IsNullOrEmpty(targetRoot) && _appConfig.LibraryRootPaths.Any())
                targetRoot = _appConfig.LibraryRootPaths.First();
                
            if (string.IsNullOrEmpty(targetRoot))
            {
                _notificationService.Show("Organizer Error", "No target directory configured.", NotificationType.Error);
                return;
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.FilePath) || !System.IO.File.Exists(entry.FilePath)) continue;
                
                var extension = System.IO.Path.GetExtension(entry.FilePath);
                var safeArtist = Utils.FilenameNormalizer.GetSafeFilename(entry.Artist ?? "Unknown Artist");
                var safeAlbum = Utils.FilenameNormalizer.GetSafeFilename(entry.Album ?? "Unknown Album");
                var safeTitle = Utils.FilenameNormalizer.GetSafeFilename(entry.Title ?? "Unknown Title");
                
                var newDir = System.IO.Path.Combine(targetRoot, safeArtist, safeAlbum);
                var newPath = System.IO.Path.Combine(newDir, $"{safeArtist} - {safeTitle}{extension}");
                
                if (entry.FilePath == newPath) continue;
                
                try 
                {
                    if (!System.IO.Directory.Exists(newDir))
                        System.IO.Directory.CreateDirectory(newDir);
                        
                    System.IO.File.Move(entry.FilePath, newPath, true);
                    entry.FilePath = newPath;
                    await _libraryService.SaveOrUpdateLibraryEntryAsync(entry);
                    movedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to move file: {Path}", entry.FilePath);
                    errorCount++;
                }
            }
            
            _notificationService.Show("Organization Complete", $"Moved {movedCount} files.", movedCount > 0 ? NotificationType.Success : NotificationType.Information);
            await ExecuteRefreshLibraryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Organizaton process failed");
            _notificationService.Show("Organizer Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteSyncPhysicalLibraryAsync()
    {
        try
        {
            IsLoading = true;
            _notificationService.Show("Syncing Library", "Scanning for missing files...", NotificationType.Information);

            var entries = await _databaseService.GetAllLibraryEntriesAsync();
            var orphans = new List<LibraryEntryEntity>();

            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.FilePath) && !System.IO.File.Exists(entry.FilePath))
                {
                    orphans.Add(entry);
                }
            }

            // Clear existing orphaned tracks
            OrphanedTracks.Clear();

            if (orphans.Any())
            {
                // Add to UI collection for user review
                foreach (var orphan in orphans)
                {
                    var fileInteraction = _serviceProvider?.GetService(typeof(Services.IFileInteractionService)) as Services.IFileInteractionService;
                    OrphanedTracks.Add(new OrphanedTrackViewModel(orphan, _libraryService, _dialogService, fileInteraction!, OrphanedTracks));
                }
                _notificationService.Show("Library Synced", $"Found {orphans.Count} orphaned entries. Review and remove manually.", NotificationType.Warning);
                IsOrphanedTracksVisible = true;
            }
            else
            {
                _notificationService.Show("Library Synced", "No orphaned entries found.", NotificationType.Information);
                IsOrphanedTracksVisible = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library sync failed");
            _notificationService.Show("Sync Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteDuplicateDetectionAsync()
    {
        try
        {
            IsLoading = true;
            _notificationService.Show("Searching Duplicates", "Hashing library entries...", NotificationType.Information);
            
            var entries = await _libraryService.LoadAllLibraryEntriesAsync();
            var duplicateHashes = entries
                .GroupBy(e => e.UniqueHash)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            if (!duplicateHashes.Any())
            {
                _notificationService.Show("Clean Library", "No duplicate hashes detected.", NotificationType.Success);
                Tracks.DuplicateHashesFilter = null;
            }
            else
            {
                _notificationService.Show("Review Required", $"Found {duplicateHashes.Count} duplicate groups.", NotificationType.Warning);
                Tracks.DuplicateHashesFilter = duplicateHashes;
            }
            
            Tracks.RefreshFilteredTracks();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Duplicate detection crashed");
            _notificationService.Show("Detection Error", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ExecuteOpenFlowBuilder()
    {
        try
        {
            var selectedModels = Tracks.SelectedTracks
                .Select(t => t.Model)
                .Where(m => m != null)
                .Select(m => m!)
                .ToList();

            if (selectedModels.Count > 0)
            {
                _eventBus.Publish(new AddToTimelineRequestEvent(selectedModels));
                _notificationService.Show(
                    "Flow Builder",
                    $"Sent {selectedModels.Count} selected track(s) to the flow timeline.",
                    NotificationType.Success);
                return;
            }

            _navigationService.NavigateTo("Workstation");
            _notificationService.Show(
                "Flow Builder",
                "Opened Workstation Flow view.",
                NotificationType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open flow builder");
            _notificationService.Show("Flow Builder Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteExportMonthlyDropAsync()
    {
        try
        {
            if (Tracks.SelectedTracks.Count == 0)
            {
                _notificationService.Show(
                    "Monthly Drop Export",
                    "Select tracks first, then export your monthly drop.",
                    NotificationType.Information);
                return;
            }

            await ExecuteBatchExportRekordboxAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monthly drop export failed");
            _notificationService.Show("Monthly Drop Export Failed", ex.Message, NotificationType.Error);
        }
    }

    private void ExecuteFindBridgeBetweenSelected()
    {
        try
        {
            var selected = Tracks.SelectedTracks
                .Where(t => t.Model != null && !string.IsNullOrWhiteSpace(t.Model.TrackUniqueHash))
                .Take(2)
                .ToList();

            if (selected.Count < 2)
            {
                _notificationService.Show(
                    "Bridge Finder",
                    "Select exactly two tracks in Library first.",
                    NotificationType.Information);
                return;
            }

            var from = selected[0];
            var to = selected[1];

            ReactiveUI.MessageBus.Current.SendMessage(
                new FindBridgeBetweenTracksEvent(
                    from.Model.TrackUniqueHash,
                    to.Model.TrackUniqueHash,
                    $"{from.ArtistName} - {from.TrackTitle}",
                    $"{to.ArtistName} - {to.TrackTitle}"));

            _notificationService.Show(
                "Bridge Finder",
                $"Searching bridge tracks between \"{from.TrackTitle}\" and \"{to.TrackTitle}\" in Similar Tracks panel.",
                NotificationType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger bridge finder from selection");
            _notificationService.Show("Bridge Finder Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteSmartInsertBetweenSelectedAsync()
    {
        try
        {
            if (_playlistIntelligenceService is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Playlist intelligence service is unavailable.",
                    NotificationType.Error);
                return;
            }

            var project = SelectedProject;
            if (project is null || project.Id == Guid.Empty)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select a specific playlist first.",
                    NotificationType.Information);
                return;
            }

            var selected = Tracks.SelectedTracks
                .Where(t => t.Model != null && !string.IsNullOrWhiteSpace(t.Model.TrackUniqueHash))
                .OrderBy(t => t.SortOrder)
                .Take(2)
                .ToList();

            if (selected.Count < 2)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select two playlist tracks to insert a best-fit bridge between them.",
                    NotificationType.Information);
                return;
            }

            var from = selected[0].Model;
            var to = selected[1].Model;
            if (from == null || to == null)
            {
                return;
            }

            FocusLibraryIntelligenceTab("SmartInsert");
            Intelligence.SetSmartInsertPairContext(from, to);

            if (string.Equals(from.TrackUniqueHash, to.TrackUniqueHash, StringComparison.OrdinalIgnoreCase))
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select two different tracks.",
                    NotificationType.Information);
                return;
            }

            IsLoading = true;

            var insertResult = await ExecuteSmartInsertBetweenCoreAsync(project, from, to);
            if (!insertResult.Success)
            {
                _notificationService.Show("Smart Insert", insertResult.Message, insertResult.Type);
                return;
            }

            _notificationService.Show(
                "Smart Insert",
                insertResult.Message,
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Smart insert between selected tracks failed");
            _notificationService.Show("Smart Insert Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteSmartInsertAfterTrackAsync(object? parameter)
    {
        try
        {
            if (parameter is not PlaylistTrackViewModel anchorVm || anchorVm.Model is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Choose a track row first.",
                    NotificationType.Information);
                return;
            }

            var project = SelectedProject;
            if (project is null || project.Id == Guid.Empty)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select a specific playlist first.",
                    NotificationType.Information);
                return;
            }

            var orderedProjectTracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id))
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.TrackNumber)
                .ThenBy(t => t.AddedAt)
                .ToList();

            var fromIndex = orderedProjectTracks.FindIndex(t => t.Id == anchorVm.Model.Id);
            if (fromIndex < 0)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "That track is not in the active playlist ordering.",
                    NotificationType.Warning);
                return;
            }

            if (fromIndex >= orderedProjectTracks.Count - 1)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "No next track exists after this row.",
                    NotificationType.Information);
                return;
            }

            var from = orderedProjectTracks[fromIndex];
            var to = orderedProjectTracks[fromIndex + 1];

            FocusLibraryIntelligenceTab("SmartInsert");
            Intelligence.SetSmartInsertPairContext(from, to);

            IsLoading = true;

            var insertResult = await ExecuteSmartInsertBetweenCoreAsync(project, from, to);
            if (!insertResult.Success)
            {
                _notificationService.Show("Smart Insert", insertResult.Message, insertResult.Type);
                return;
            }

            _notificationService.Show(
                "Smart Insert",
                $"{insertResult.Message} (after \"{from.Title}\").",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Smart insert after track failed");
            _notificationService.Show("Smart Insert Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteFavoriteSelectedPairAsDoubleAsync()
    {
        var first = DoubleInspector.TrackA;
        var second = DoubleInspector.TrackB;
        if (first is null || second is null)
        {
            _notificationService.Show("Double Inspector", "Select two tracks first.", NotificationType.Information);
            return;
        }

        first.IsLiked = true;
        second.IsLiked = true;

        if (_savedDoublesService is null)
        {
            _notificationService.Show(
                "Double Inspector",
                "Saved doubles persistence is unavailable in this session.",
                NotificationType.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(first.GlobalId) || string.IsNullOrWhiteSpace(second.GlobalId))
        {
            _notificationService.Show(
                "Double Inspector",
                "Selected tracks need stable IDs before saving.",
                NotificationType.Information);
            return;
        }

        var (trackA, trackB) = SavedDoublesService.Normalize(first.GlobalId, second.GlobalId);
        double? score = DoubleInspector.HasPairContext ? DoubleInspector.TransitionScore : null;
        var saved = new SavedDouble(trackA, trackB, DateTime.UtcNow, score, null);
        await _savedDoublesService.AddOrUpdateAsync(saved);
        await RefreshSavedDoublesAsync();

        _notificationService.Show(
            "Double Inspector",
            "Saved as favorite pair in Library.",
            NotificationType.Success);
    }

    private void ExecuteActivateSavedDouble(object? parameter)
    {
        if (parameter is not Library.SavedDoubleViewModel saved)
            return;

        SelectTrackPair(saved.TrackA, saved.TrackB);
    }

    private async Task ExecuteRemoveSavedDoubleAsync(object? parameter)
    {
        if (_savedDoublesService is null || parameter is not Library.SavedDoubleViewModel saved)
            return;

        await _savedDoublesService.RemoveAsync(saved.Model);
        await RefreshSavedDoublesAsync();
    }

    private void SelectTrackPair(PlaylistTrackViewModel first, PlaylistTrackViewModel second)
    {
        foreach (var selected in Tracks.SelectedTracks.ToList())
            selected.IsSelected = false;

        first.IsSelected = true;
        second.IsSelected = true;
        Tracks.UpdateSelection(new[] { first, second });
    }

    private void ExecuteOpenSelectedPairInWorkstation()
    {
        var selectedModels = Tracks.SelectedTracks
            .Take(2)
            .Where(track => track.Model != null)
            .Select(track => track.Model)
            .ToList();

        if (selectedModels.Count < 2)
        {
            _notificationService.Show("Double Inspector", "Select two tracks first.", NotificationType.Information);
            return;
        }

        _eventBus.Publish(new AddToTimelineRequestEvent(selectedModels));
        _notificationService.Show(
            "Double Inspector",
            "Sent selected pair to Workstation timeline.",
            NotificationType.Success);
    }

    private void ExecuteAnalyzeSelectedPair()
    {
        var selected = Tracks.SelectedTracks.Take(2).ToList();
        if (selected.Count < 2)
        {
            _notificationService.Show("Double Inspector", "Select two tracks first.", NotificationType.Information);
            return;
        }

        var queued = 0;
        foreach (var track in selected)
        {
            if (track.AnalyzeTrackCommand.CanExecute(null))
            {
                track.AnalyzeTrackCommand.Execute(null);
                queued++;
            }
        }

        if (queued == 0)
        {
            _notificationService.Show(
                "Double Inspector",
                "Both tracks need to be downloaded before analysis can start.",
                NotificationType.Information);
            return;
        }

        _notificationService.Show(
            "Double Inspector",
            queued == 1 ? "Queued analysis for one track." : "Queued analysis for both tracks.",
            NotificationType.Success);
    }

    private async Task ExecutePrepareSmartInsertAfterTrackAsync(object? parameter)
    {
        try
        {
            if (parameter is SmartInsertContextRequest contextRequest
                && contextRequest.FromTrack.Model is PlaylistTrack fromModel
                && contextRequest.ToTrack.Model is PlaylistTrack toModel)
            {
                FocusLibraryIntelligenceTab("SmartInsert");
                Intelligence.SetSmartInsertPairContext(fromModel, toModel);

                _notificationService.Show(
                    "Smart Insert",
                    $"Context ready between \"{fromModel.Title}\" and \"{toModel.Title}\".",
                    NotificationType.Information);
                return;
            }

            if (parameter is not PlaylistTrackViewModel anchorVm || anchorVm.Model is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Choose a track row first.",
                    NotificationType.Information);
                return;
            }

            var project = SelectedProject;
            if (project is null || project.Id == Guid.Empty)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select a specific playlist first.",
                    NotificationType.Information);
                return;
            }

            var orderedProjectTracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id))
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.TrackNumber)
                .ThenBy(t => t.AddedAt)
                .ToList();

            var fromIndex = orderedProjectTracks.FindIndex(t => t.Id == anchorVm.Model.Id);
            if (fromIndex < 0)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "That track is not in the active playlist ordering.",
                    NotificationType.Warning);
                return;
            }

            if (fromIndex >= orderedProjectTracks.Count - 1)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "No next track exists after this row.",
                    NotificationType.Information);
                return;
            }

            var from = orderedProjectTracks[fromIndex];
            var to = orderedProjectTracks[fromIndex + 1];

            FocusLibraryIntelligenceTab("SmartInsert");
            Intelligence.SetSmartInsertPairContext(from, to);

            _notificationService.Show(
                "Smart Insert",
                $"Context ready between \"{from.Title}\" and \"{to.Title}\".",
                NotificationType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing smart insert context from row failed");
            _notificationService.Show("Smart Insert", "Failed to prepare insert context.", NotificationType.Error);
        }
    }

    private async Task ExecuteApplyPreparedSmartInsertAsync()
    {
        try
        {
            if (_playlistIntelligenceService is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Playlist intelligence service is unavailable.",
                    NotificationType.Error);
                return;
            }

            var project = SelectedProject;
            if (project is null || project.Id == Guid.Empty)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Select a specific playlist first.",
                    NotificationType.Information);
                return;
            }

            if (!Intelligence.TryGetPendingSmartInsertContext(out var pendingFromTrack, out var pendingToTrack)
                || pendingFromTrack is null
                || pendingToTrack is null)
            {
                _notificationService.Show(
                    "Smart Insert",
                    "Prepare a Smart Insert context first.",
                    NotificationType.Information);
                return;
            }

            IsLoading = true;
            var insertResult = await ExecuteSmartInsertBetweenCoreAsync(project, pendingFromTrack, pendingToTrack);
            if (!insertResult.Success)
            {
                _notificationService.Show("Smart Insert", insertResult.Message, insertResult.Type);
                return;
            }

            _notificationService.Show(
                "Smart Insert",
                $"{insertResult.Message} (explicit apply).",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explicit Smart Insert apply failed");
            _notificationService.Show("Smart Insert Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal Task ApplyPreparedSmartInsertFromIntelligenceAsync()
        => ExecuteApplyPreparedSmartInsertAsync();

    private async Task<SmartInsertResult> ExecuteSmartInsertBetweenCoreAsync(PlaylistJob project, PlaylistTrack from, PlaylistTrack to)
    {
        var orderedProjectTracks = (await _libraryService.LoadPlaylistTracksAsync(project.Id))
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.AddedAt)
            .ToList();

        var fromIndex = orderedProjectTracks.FindIndex(t => string.Equals(t.TrackUniqueHash, from.TrackUniqueHash, StringComparison.OrdinalIgnoreCase));
        var toIndex = orderedProjectTracks.FindIndex(t => string.Equals(t.TrackUniqueHash, to.TrackUniqueHash, StringComparison.OrdinalIgnoreCase));
        if (fromIndex < 0 || toIndex < 0)
        {
            return new SmartInsertResult(false, "Selected tracks were not found in current playlist order.", NotificationType.Warning);
        }

        var existingHashes = new HashSet<string>(
            orderedProjectTracks.Select(t => t.TrackUniqueHash),
            StringComparer.OrdinalIgnoreCase);

        var candidateHashes = (await _libraryService.LoadDownloadedTracksAsync())
            .Select(e => e.UniqueHash)
            .Where(h => !string.IsNullOrWhiteSpace(h) && !existingHashes.Contains(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1200)
            .ToList();

        if (candidateHashes.Count == 0)
        {
            return new SmartInsertResult(false, "No external candidate tracks are available to insert.", NotificationType.Information);
        }

        var recommendations = await _playlistIntelligenceService!.InsertBetweenAsync(
            from.TrackUniqueHash,
            to.TrackUniqueHash,
            candidateHashes,
            TrackSimilarityProfile.BlendSafe,
            topK: 3,
            ct: default,
            minConfidenceThreshold: Math.Clamp(_appConfig.LibrarySmartInsertMinConfidence, 0.0, 1.0),
            structureSensitivity: Math.Clamp(_appConfig.LibrarySmartInsertStructureSensitivity / 100.0, 0.0, 1.0));

        var rankedRecommendations = recommendations
            .Select(recommendation => new
            {
                Recommendation = recommendation,
                BaseScore = recommendation.Score,
                Bonus = GetSavedDoubleBonus(from.TrackUniqueHash, to.TrackUniqueHash, recommendation.TrackHash)
            })
            .OrderByDescending(item => item.BaseScore + item.Bonus)
            .ToList();

        var bestRanked = rankedRecommendations.FirstOrDefault();
        var best = bestRanked?.Recommendation;
        if (best is null || string.IsNullOrWhiteSpace(best.TrackHash))
        {
            return new SmartInsertResult(false, "No suitable bridge track found for the selected pair.", NotificationType.Information);
        }

        var entry = await _libraryService.FindLibraryEntryAsync(best.TrackHash);
        if (entry is null)
        {
            return new SmartInsertResult(false, "A recommendation was found, but metadata is unavailable.", NotificationType.Warning);
        }

        var bridgeTrack = new PlaylistTrack
        {
            PlaylistId = project.Id,
            TrackUniqueHash = entry.UniqueHash,
            Artist = entry.Artist,
            Title = entry.Title,
            Album = entry.Album,
            ResolvedFilePath = entry.FilePath,
            Format = entry.Format,
            Status = TrackStatus.Downloaded,
            BPM = entry.BPM,
            MusicalKey = entry.MusicalKey,
            AddedAt = DateTime.UtcNow,
            SortOrder = to.SortOrder,
            TrackNumber = to.TrackNumber
        };

        await _libraryService.AddTracksToProjectAsync(new[] { bridgeTrack }, project.Id);

        var refreshed = await _libraryService.LoadPlaylistTracksAsync(project.Id);
        var insertedTrack = refreshed
            .Where(t => string.Equals(t.TrackUniqueHash, bridgeTrack.TrackUniqueHash, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.AddedAt)
            .FirstOrDefault();

        if (insertedTrack is not null)
        {
            var ordered = refreshed
                .OrderBy(t => t.SortOrder)
                .ThenBy(t => t.TrackNumber)
                .ThenBy(t => t.AddedAt)
                .ToList();

            ordered.RemoveAll(t => t.Id == insertedTrack.Id);

            var idxA = ordered.FindIndex(t => string.Equals(t.TrackUniqueHash, from.TrackUniqueHash, StringComparison.OrdinalIgnoreCase));
            var idxB = ordered.FindIndex(t => string.Equals(t.TrackUniqueHash, to.TrackUniqueHash, StringComparison.OrdinalIgnoreCase));
            var upper = Math.Max(idxA, idxB);
            var insertAt = Math.Clamp(upper >= 0 ? upper : ordered.Count, 0, ordered.Count);

            ordered.Insert(insertAt, insertedTrack);

            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].SortOrder = i + 1;
                ordered[i].TrackNumber = i + 1;
            }

            await _libraryService.SaveTrackOrderAsync(project.Id, ordered);
        }

        await Tracks.LoadProjectTracksAsync(project);
        Tracks.RefreshFilteredTracks();

        var savedDoublePriorSuffix = bestRanked is { Bonus: > 0.0 }
            ? " This choice aligns with one of your saved doubles."
            : string.Empty;

        return new SmartInsertResult(
            true,
            $"Inserted {entry.Artist} - {entry.Title} between tracks (fit {(best.Score * 100):F0}%, threshold {_appConfig.LibrarySmartInsertMinConfidence:F2}, structure {_appConfig.LibrarySmartInsertStructureSensitivity}%).{savedDoublePriorSuffix}",
            NotificationType.Success);
    }

    private double GetSavedDoubleBonus(string fromTrackId, string toTrackId, string? candidateTrackId)
    {
        if (string.IsNullOrWhiteSpace(fromTrackId) ||
            string.IsNullOrWhiteSpace(toTrackId) ||
            string.IsNullOrWhiteSpace(candidateTrackId))
        {
            return 0.0;
        }

        var bonus = 0.0;

        if (IsSavedDoublePair(fromTrackId, candidateTrackId))
            bonus += SavedDoublePriorBonus;

        if (IsSavedDoublePair(candidateTrackId, toTrackId))
            bonus += SavedDoublePriorBonus;

        return bonus;
    }

    private bool IsSavedDoublePair(string leftTrackId, string rightTrackId)
    {
        if (string.IsNullOrWhiteSpace(leftTrackId) || string.IsNullOrWhiteSpace(rightTrackId))
            return false;

        var (normalizedA, normalizedB) = SavedDoublesService.Normalize(leftTrackId, rightTrackId);

        return SavedDoubles.Any(saved =>
            string.Equals(saved.Model.TrackAId, normalizedA, StringComparison.Ordinal) &&
            string.Equals(saved.Model.TrackBId, normalizedB, StringComparison.Ordinal));
    }

    private sealed record SmartInsertResult(bool Success, string Message, NotificationType Type);

    private async Task ExecuteExportPlaylistAsync(object? param)
    {
        if (param is not PlaylistJob project) return;

        try
        {
            var defaultName = $"{Utils.FilenameNormalizer.GetSafeFilename(project.SourceTitle)}.xml";
            var path = await _dialogService.SaveFileAsync("Export Rekordbox XML", defaultName, "xml");
            
            if (string.IsNullOrEmpty(path)) return;

            IsLoading = true;
            _notificationService.Show("Exporting", $"Saving '{project.SourceTitle}' to Rekordbox XML...", NotificationType.Information);
            
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            await _exportService.ExportToRekordboxXmlAsync(project.SourceTitle, tracks, path);
            
            _notificationService.Show("Export Successful", $"Playlist exported to {Path.GetFileName(path)}", NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            _notificationService.Show("Export Failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ExecuteResetView()
    {
        // Reset view settings
        CurrentWorkspace = ActiveWorkspace.Selector;
        // Reset other view states
    }

    private void ExecuteSwitchWorkspace(ActiveWorkspace workspace)
    {
        CurrentWorkspace = workspace;
    }

    private void ExecuteSmartEscape()
    {
        // Close any open overlays in priority order
        if (IsOrphanedTracksVisible)
        {
            IsOrphanedTracksVisible = false;
        }
        else if (IsRemovalHistoryVisible)
        {
            IsRemovalHistoryVisible = false;
        }
        else if (IsSourcesOpen)
        {
            IsSourcesOpen = false;
        }
    }

    // ── Batch Action FAB implementations (Task 10.5) ────────────────────────

    private async Task ExecuteBatchTagEditAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;
        _logger.LogInformation("Batch tag edit for {Count} tracks", selected.Count);
        // Delegated to a future BatchTagEditDialog; placeholder logs intent.
        await Task.CompletedTask;
    }

    private async Task ExecuteBatchQueueAnalysisAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;
        foreach (var track in selected)
        {
            _eventBus.Publish(new Models.TrackAnalysisRequestedEvent(track.GlobalId));
        }
        _notificationService.Show(
            "Analysis Queued",
            $"{selected.Count} track(s) queued for audio analysis.",
            NotificationType.Success);
        await Task.CompletedTask;
    }

    private async Task ExecuteBatchAddToPlaylistAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;
        _logger.LogInformation("Batch add-to-playlist for {Count} tracks", selected.Count);
        // Delegated to a future PlaylistPickerDialog.
        await Task.CompletedTask;
    }

    private async Task ExecuteBatchExportRekordboxAsync()
    {
        var selected = Tracks.SelectedTracks.ToList();
        if (selected.Count == 0) return;
        try
        {
            var outputPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"orbit-rekordbox-{DateTime.Now:yyyyMMdd-HHmmss}.xml");
            var tracks = selected.Select(t => new Models.PlaylistTrack
            {
                Id               = t.Id,
                Title            = t.Title,
                Artist           = t.Artist,
                ResolvedFilePath = t.Model.ResolvedFilePath ?? string.Empty,
                BPM              = t.BPM > 0 ? t.BPM : null,
                MusicalKey       = t.MusicalKey,
            });
            await _exportService.ExportToRekordboxXmlAsync("ORBIT Batch Export", tracks, outputPath);
            _notificationService.Show(
                "Rekordbox Export Complete",
                $"{selected.Count} track(s) exported to {outputPath}",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch Rekordbox export failed");
            _notificationService.Show("Export Failed", ex.Message, NotificationType.Error);
        }
    }
}
