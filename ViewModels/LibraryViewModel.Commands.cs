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
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace SLSKDONET.ViewModels;

public partial class LibraryViewModel
{
    // Commands that delegate to child ViewModels or handle coordination
    // CS8618 Fix: Initialize with null! since they are set in InitializeCommands()
    public ICommand ViewHistoryCommand { get; set; } = null!;
    public ICommand OpenSourcesCommand { get; set; } = null!;
    public ICommand ToggleEditModeCommand { get; set; } = null!;
    public ICommand ToggleActiveDownloadsCommand { get; set; } = null!;
    public ICommand ToggleNavigationCommand { get; set; } = null!;
    public ICommand ExpandNavigationCommand { get; set; } = null!;
    public ICommand CollapseNavigationCommand { get; set; } = null!;
    public ICommand ToggleViewModeCommand { get; set; } = null!;
    
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

    public ICommand SmartEscapeCommand { get; set; } = null!;




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
        ToggleViewModeCommand = new RelayCommand(() => UseCardView = !UseCardView);
        
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
        ToggleColumnCommand = new RelayCommand<ColumnDefinition>(ExecuteToggleColumn);
        ResetViewCommand = new RelayCommand(ExecuteResetView);
        SwitchWorkspaceCommand = new RelayCommand<ActiveWorkspace>(ExecuteSwitchWorkspace);
        SmartEscapeCommand = new RelayCommand(ExecuteSmartEscape);
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

    public ICommand SetViewModeCommand { get; set; } = null!;

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
                    OrphanedTracks.Add(new OrphanedTrackViewModel(orphan, _libraryService, _dialogService, OrphanedTracks));
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
}
