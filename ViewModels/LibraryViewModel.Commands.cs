using System;
using System.IO;
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
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Library.Files;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Models.Musical;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels;

public partial class LibraryViewModel
{
    // De-bounce guard: track last download-missing execution per project to prevent rapid repeat calls
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _downloadMissingLastRun = new();

    // Commands delegate to child view models or orchestration paths and are assigned in InitializeCommands().
    public ICommand ViewHistoryCommand { get; set; } = null!;
    public ICommand OpenSourcesCommand { get; set; } = null!;
    public ICommand RemoveUnidentifiedTracksCommand { get; set; } = null!;
    public ICommand RemoveDuplicateTracksCommand { get; set; } = null!;
    public ICommand FixMissingFileFlagsCommand { get; set; } = null!;
    public ICommand BackfillDurationsCommand { get; set; } = null!;
    public ICommand MeasureMissingLoudnessCommand { get; set; } = null!;
    public ICommand EnrichLibraryMetadataCommand { get; set; } = null!;
    public ICommand AutoArrangePlaylistCommand { get; set; } = null!;
    public ICommand CombineSelectedPlaylistsCommand { get; set; } = null!;
    public ICommand ToggleEditModeCommand { get; set; } = null!;
    public ICommand ToggleActiveDownloadsCommand { get; set; } = null!;
    public ICommand ToggleNavigationCommand { get; set; } = null!;
    public ICommand ExpandNavigationCommand { get; set; } = null!;
    public ICommand CollapseNavigationCommand { get; set; } = null!;
    
    public ICommand PlayTrackCommand { get; set; } = null!;
    public ICommand RefreshLibraryCommand { get; set; } = null!;
    public ICommand DeleteProjectCommand { get; set; } = null!;
    public ICommand PlayAlbumCommand { get; set; } = null!;
    public ICommand MixPlaylistCommand { get; set; } = null!;
    public ICommand DownloadAlbumCommand { get; set; } = null!;
    public ICommand DownloadMissingCommand { get; set; } = null!;
    public ICommand ForceRedownloadCommand { get; set; } = null!;
    public ICommand DownloadPlaylistNormalCommand { get; set; } = null!;
    public ICommand DownloadPlaylistHighCommand { get; set; } = null!;
    public ICommand DownloadPlaylistCriticalCommand { get; set; } = null!;
    public ICommand AcquireMissingTracksCommand { get; set; } = null!;
    public ICommand RenameProjectCommand { get; set; } = null!;
    public ICommand DuplicateDetectionCommand { get; set; } = null!;
    public ICommand LoadDeletedProjectsCommand { get; set; } = null!;
    public ICommand RestoreProjectCommand { get; set; } = null!;
    public ICommand CloseRemovalHistoryCommand { get; set; } = null!;
    public ICommand CloseImportHistoryCommand { get; set; } = null!;
    public ICommand CloseOrphanedTracksCommand { get; set; } = null!;
    public ICommand OpenLibraryHealthCommand { get; set; } = null!;
    public ICommand CloseLibraryHealthCommand { get; set; } = null!;
    public ICommand OpenLibraryMaintenanceCommand { get; set; } = null!;
    public ICommand FindExactDuplicatesCommand { get; set; } = null!;
    public ICommand SyncProjectCommand { get; set; } = null!;
    public ICommand OpenSourceUrlCommand { get; set; } = null!;
    public ICommand ExportPlaylistCommand { get; set; } = null!;
    public ICommand ExportPlaylistM3uCommand { get; set; } = null!;
    public ICommand ExportPlaylistTraktorCommand { get; set; } = null!;
    public ICommand ExportPlaylistVirtualDjCommand { get; set; } = null!;
    public ICommand ExportPlaylistSeratoCommand { get; set; } = null!;
    public ICommand RelocateMissingFolderCommand { get; set; } = null!;
    public ICommand CopyPlaylistTracklistCommand { get; set; } = null!;
    public ICommand ExportPlaylistCsvCommand { get; set; } = null!;
    public ICommand ComparePlaylistsCommand { get; set; } = null!;
    public ICommand InitiateMp3SearchCommand { get; set; } = null!;

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
    public ICommand BulkRenameCommand { get; set; } = null!;
    public ICommand BulkMoveOrCopyCommand { get; set; } = null!;
    public ICommand BatchQueueAnalysisCommand { get; set; } = null!;
    public ICommand BatchAddToPlaylistCommand { get; set; } = null!;
    public ICommand BatchExportRekordboxCommand { get; set; } = null!;
    public ICommand BatchExportM3uCommand { get; set; } = null!;
    public ICommand BatchClearSelectionCommand { get; set; } = null!;

    // ── Genre View actions (Epic #561 / #566 GV5) ───────────────────────────
    public ICommand SaveGenreAsPlaylistCommand { get; set; } = null!;
    public ICommand SaveGenreAsSmartCrateCommand { get; set; } = null!;




    partial void InitializeCommands()
    {
        ViewHistoryCommand = new AsyncRelayCommand(ExecuteViewHistoryAsync);
        OpenSourcesCommand = new RelayCommand<object>(param =>
        {
            if (param?.ToString() == "Close") IsSourcesOpen = false;
            else IsSourcesOpen = true;
        });
        RemoveUnidentifiedTracksCommand = new AsyncRelayCommand(ExecuteRemoveUnidentifiedTracksAsync);
        RemoveDuplicateTracksCommand = new AsyncRelayCommand(ExecuteRemoveDuplicateTracksAsync);
        FixMissingFileFlagsCommand = new AsyncRelayCommand(ExecuteFixMissingFileFlagsAsync);
        BackfillDurationsCommand = new AsyncRelayCommand(ExecuteBackfillDurationsAsync);
        MeasureMissingLoudnessCommand = new RelayCommand(ExecuteMeasureMissingLoudness);
        EnrichLibraryMetadataCommand = new AsyncRelayCommand(ExecuteEnrichLibraryMetadataAsync);
        AutoArrangePlaylistCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteAutoArrangePlaylistAsync);
        CombineSelectedPlaylistsCommand = new AsyncRelayCommand(ExecuteCombineSelectedPlaylistsAsync);
        ToggleEditModeCommand = new RelayCommand(() => IsEditMode = !IsEditMode);
        ToggleActiveDownloadsCommand = new RelayCommand(() => IsActiveDownloadsVisible = !IsActiveDownloadsVisible);
        ToggleNavigationCommand = new RelayCommand(ExecuteToggleNavigation);
        ExpandNavigationCommand = new RelayCommand(ExecuteHoverExpandNavigation);
        CollapseNavigationCommand = new RelayCommand(ExecuteHoverCollapseNavigation);
        
        PlayTrackCommand = new AsyncRelayCommand<object>(ExecutePlayTrackAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(ExecuteRefreshLibraryAsync);
        DeleteProjectCommand = new AsyncRelayCommand<object>(ExecuteDeleteProjectAsync);
        PlayAlbumCommand = new AsyncRelayCommand<object>(ExecutePlayAlbumAsync);
        MixPlaylistCommand = new AsyncRelayCommand<object>(ExecuteMixPlaylistAsync);
        DownloadAlbumCommand = new AsyncRelayCommand<object>(ExecuteDownloadAlbumAsync);
        DownloadMissingCommand = new AsyncRelayCommand<object>(ExecuteDownloadMissingAsync);
        ForceRedownloadCommand = new AsyncRelayCommand<object>(ExecuteForceRedownloadAsync);
        DownloadPlaylistNormalCommand   = new AsyncRelayCommand<object>(p => ExecuteQueueMissingWithPriorityAsync(p, PlaylistPriority.Normal));
        DownloadPlaylistHighCommand     = new AsyncRelayCommand<object>(p => ExecuteQueueMissingWithPriorityAsync(p, PlaylistPriority.High));
        DownloadPlaylistCriticalCommand = new AsyncRelayCommand<object>(p => ExecuteQueueMissingWithPriorityAsync(p, PlaylistPriority.Critical));
        AcquireMissingTracksCommand = new AsyncRelayCommand<object>(ExecuteAcquireMissingTracksAsync);
        RenameProjectCommand = new AsyncRelayCommand<object>(ExecuteRenameProjectAsync);
        SyncProjectCommand = new AsyncRelayCommand<object>(ExecuteSyncProjectAsync);
        OpenSourceUrlCommand = new RelayCommand<object>(ExecuteOpenSourceUrl);
        LoadDeletedProjectsCommand = new AsyncRelayCommand(ExecuteLoadDeletedProjectsAsync);
        RestoreProjectCommand = new AsyncRelayCommand<object>(ExecuteRestoreProjectAsync);
        CloseRemovalHistoryCommand = new RelayCommand(() => IsRemovalHistoryVisible = false);
        CloseOrphanedTracksCommand = new RelayCommand(() => IsOrphanedTracksVisible = false);
        OpenLibraryHealthCommand = new RelayCommand(() =>
        {
            IsLibraryHealthVisible = true;
            LibraryHealthViewModel.RefreshAll();
        });
        CloseLibraryHealthCommand = new RelayCommand(() => IsLibraryHealthVisible = false);
        OpenLibraryMaintenanceCommand = new RelayCommand(() => _eventBus.Publish(new OpenLibraryMaintenanceRequestedEvent()));
        FindExactDuplicatesCommand = new RelayCommand(() => _eventBus.Publish(new OpenExactDuplicatesRequestedEvent()));
        CloseImportHistoryCommand = new RelayCommand(() => IsImportHistoryVisible = false);
        ExportPlaylistCommand = new AsyncRelayCommand<object>(ExecuteExportPlaylistAsync);
        InitiateMp3SearchCommand = new AsyncRelayCommand<object>(ExecuteInitiateMp3SearchAsync);
        ExportPlaylistM3uCommand = new AsyncRelayCommand<object>(ExecuteExportPlaylistM3uAsync);
        ExportPlaylistTraktorCommand = new AsyncRelayCommand<object>(ExecuteExportPlaylistTraktorAsync);
        ExportPlaylistVirtualDjCommand = new AsyncRelayCommand<object>(ExecuteExportPlaylistVirtualDjAsync);
        ExportPlaylistSeratoCommand = new AsyncRelayCommand<object>(ExecuteExportPlaylistSeratoAsync);
        RelocateMissingFolderCommand = new AsyncRelayCommand(ExecuteRelocateMissingFolderAsync);
        SaveGenreAsPlaylistCommand = new AsyncRelayCommand<object>(ExecuteSaveGenreAsPlaylistParamAsync);
        SaveGenreAsSmartCrateCommand = new AsyncRelayCommand<object>(ExecuteSaveGenreAsSmartCrateParamAsync);
        CopyPlaylistTracklistCommand = new AsyncRelayCommand<object>(ExecuteCopyPlaylistTracklistAsync);
        ExportPlaylistCsvCommand = new AsyncRelayCommand<object>(ExecuteExportPlaylistCsvAsync);
        ComparePlaylistsCommand = new AsyncRelayCommand<object>(ExecuteComparePlaylistsAsync);


        // Fluidity
        SwitchWorkspaceCommand = new RelayCommand<ActiveWorkspace>(ws => CurrentWorkspace = ws);

        DuplicateDetectionCommand = new AsyncRelayCommand(ExecuteDuplicateDetectionAsync);
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
        BulkRenameCommand = new AsyncRelayCommand(ExecuteBulkRenameAsync);
        BulkMoveOrCopyCommand = new AsyncRelayCommand(ExecuteBulkMoveOrCopyAsync);
        BatchQueueAnalysisCommand = new AsyncRelayCommand(ExecuteBatchQueueAnalysisAsync);
        BatchAddToPlaylistCommand = new AsyncRelayCommand(ExecuteBatchAddToPlaylistAsync);
        BatchExportRekordboxCommand = new AsyncRelayCommand(ExecuteBatchExportRekordboxAsync);
        BatchExportM3uCommand = new AsyncRelayCommand(ExecuteBatchExportM3uAsync);
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
            CollapseNavPanelWidth();
        }
        else
        {
            ExpandNavPanelWidth();
        }
    }

    private void ExecuteHoverExpandNavigation()
    {
        if (!IsNavigationHoverAutoHideArmed)
        {
            return;
        }

        IsNavigationCollapsed = false;
        ExpandNavPanelWidth();
    }

    private void ExecuteHoverCollapseNavigation()
    {
        if (!IsNavigationHoverAutoHideArmed)
        {
            return;
        }

        IsNavigationCollapsed = true;
        CollapseNavPanelWidth();
    }

    /// <summary>Remembers the user's drag-resized width before snapping to the collapsed rail width.</summary>
    private void CollapseNavPanelWidth()
    {
        if (LibraryNavPanelWidth > CollapsedNavPanelWidth)
        {
            _lastExpandedNavPanelWidth = LibraryNavPanelWidth;
        }

        LibraryNavPanelWidth = CollapsedNavPanelWidth;
    }

    private void ExpandNavPanelWidth()
    {
        LibraryNavPanelWidth = _lastExpandedNavPanelWidth;
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
            NumFormat.Ui($"{presetName} preset applied ({Intelligence.LibrarySmartInsertMinConfidence:F2}, {Intelligence.LibrarySmartInsertStructureSensitivity}%)."),
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
        IsImportHistoryVisible = true;
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
                 _notificationService.Show("Restore Failed", ex.Message, NotificationType.Error);
            }
        }
    }

    private Task ExecutePlayAlbumAsync(object? param) =>
        ExecutePlayPlaylistCoreAsync(param, mixModeEnabled: false);

    private Task ExecuteMixPlaylistAsync(object? param) =>
        ExecutePlayPlaylistCoreAsync(param, mixModeEnabled: true);

    private async Task ExecuteCopyPlaylistTracklistAsync(object? param)
    {
        if (param is not PlaylistJob project) return;
        try
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            if (tracks.Count == 0)
            {
                _notificationService.Show("Nothing to Copy", $"{project.SourceTitle} has no tracks.", NotificationType.Warning);
                return;
            }

            var text = PlaylistToolsService.FormatTracklist(tracks, includeBpmAndKey: true);
            var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow?.Clipboard != null)
            {
                await mainWindow.Clipboard.SetTextAsync(text);
                _notificationService.Show("Tracklist Copied", $"Copied {tracks.Count} tracks from \"{project.SourceTitle}\".", NotificationType.Success);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy tracklist for {Playlist}", project.SourceTitle);
            _notificationService.Show("Copy Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteExportPlaylistCsvAsync(object? param)
    {
        if (param is not PlaylistJob project) return;
        try
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            if (tracks.Count == 0)
            {
                _notificationService.Show("Nothing to Export", $"{project.SourceTitle} has no tracks.", NotificationType.Warning);
                return;
            }

            var safeName = string.Concat(project.SourceTitle.Split(Path.GetInvalidFileNameChars()));
            var path = await _dialogService.SaveFileAsync("Export playlist as CSV", string.IsNullOrWhiteSpace(safeName) ? "playlist" : safeName, "csv");
            if (string.IsNullOrWhiteSpace(path)) return;

            var csv = PlaylistToolsService.FormatCsv(tracks);
            await File.WriteAllTextAsync(path, csv, System.Text.Encoding.UTF8);
            _notificationService.Show("CSV Exported", $"Exported {tracks.Count} tracks to {Path.GetFileName(path)}.", NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export CSV for {Playlist}", project.SourceTitle);
            _notificationService.Show("Export Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteComparePlaylistsAsync(object? param)
    {
        if (param is not PlaylistJob playlistA) return;
        try
        {
            var others = Projects.AllProjects.Where(p => p.Id != Guid.Empty && p.Id != playlistA.Id).ToList();
            if (others.Count == 0)
            {
                _notificationService.Show("Compare Playlists", "You need at least two playlists to compare.", NotificationType.Information);
                return;
            }

            var playlistB = await _dialogService.ShowProjectPickerAsync(others);
            if (playlistB == null) return;

            var tracksA = await _libraryService.LoadPlaylistTracksAsync(playlistA.Id);
            var tracksB = await _libraryService.LoadPlaylistTracksAsync(playlistB.Id);
            var result = PlaylistToolsService.Compare(playlistA.SourceTitle, tracksA, playlistB.SourceTitle, tracksB);

            var summary = $"In both: {result.InBoth.Count} · Only in \"{playlistA.SourceTitle}\": {result.OnlyInA.Count} · Only in \"{playlistB.SourceTitle}\": {result.OnlyInB.Count}";
            _notificationService.Show("Playlist Comparison", summary, NotificationType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compare playlists");
            _notificationService.Show("Compare Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task<List<Services.Export.ExportTrackProjection>> BuildExportProjectionsAsync(Guid playlistId)
    {
        var tracks = await _libraryService.LoadPlaylistTracksAsync(playlistId);
        var hashes = tracks
            .Select(t => t.TrackUniqueHash)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var cuesByHash = new Dictionary<string, List<CuePointEntity>>(StringComparer.Ordinal);
        var featuresByHash = new Dictionary<string, AudioFeaturesEntity>(StringComparer.Ordinal);

        if (hashes.Count > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var cues = await db.CuePoints.AsNoTracking()
                .Where(c => hashes.Contains(c.TrackUniqueHash))
                .OrderBy(c => c.TimestampInSeconds)
                .ToListAsync();
            foreach (var c in cues)
            {
                if (!cuesByHash.TryGetValue(c.TrackUniqueHash, out var list))
                {
                    list = new List<CuePointEntity>();
                    cuesByHash[c.TrackUniqueHash] = list;
                }
                list.Add(c);
            }

            var features = await db.AudioFeatures.AsNoTracking()
                .Where(f => hashes.Contains(f.TrackUniqueHash))
                .ToListAsync();
            foreach (var f in features)
                featuresByHash.TryAdd(f.TrackUniqueHash, f);
        }

        return tracks.Select(t =>
        {
            var hash = t.TrackUniqueHash ?? string.Empty;
            cuesByHash.TryGetValue(hash, out var cues);
            featuresByHash.TryGetValue(hash, out var feat);
            return Services.Export.ExportTrackProjectionBuilder.Build(t, cues, feat);
        }).ToList();
    }

    private async Task ExecuteExportPlaylistTraktorAsync(object? param)
    {
        if (param is not PlaylistJob project) return;
        try
        {
            var projections = await BuildExportProjectionsAsync(project.Id);
            if (projections.Count == 0)
            {
                _notificationService.Show("Nothing to Export", $"{project.SourceTitle} has no tracks.", NotificationType.Warning);
                return;
            }

            var safeName = string.Concat(project.SourceTitle.Split(Path.GetInvalidFileNameChars()));
            var path = await _dialogService.SaveFileAsync("Export Traktor NML playlist", string.IsNullOrWhiteSpace(safeName) ? "playlist" : safeName, "nml");
            if (string.IsNullOrWhiteSpace(path)) return;

            var preflight = Services.Export.ExportFieldMapping.Preflight(projections, Services.Export.ExportTargetKind.Traktor);
            Services.Export.TraktorNmlExporter.WriteToFile(path, project.SourceTitle, projections);

            var detail = preflight.Warnings.Count > 0
                ? $"Exported {projections.Count} tracks to {Path.GetFileName(path)}. Note: {string.Join(" ", preflight.Warnings)}"
                : $"Exported {projections.Count} tracks to {Path.GetFileName(path)}. Import in Traktor via Import Playlist.";
            _notificationService.Show("Traktor NML Exported", detail, NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export Traktor NML for {Playlist}", project.SourceTitle);
            _notificationService.Show("Traktor Export Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteExportPlaylistVirtualDjAsync(object? param)
    {
        if (param is not PlaylistJob project) return;
        try
        {
            var projections = await BuildExportProjectionsAsync(project.Id);
            if (projections.Count == 0)
            {
                _notificationService.Show("Nothing to Export", $"{project.SourceTitle} has no tracks.", NotificationType.Warning);
                return;
            }

            var safeName = string.Concat(project.SourceTitle.Split(Path.GetInvalidFileNameChars()));
            var folderPath = await _dialogService.SaveFileAsync("Export VirtualDJ folder", string.IsNullOrWhiteSpace(safeName) ? "playlist" : safeName, "vdjfolder");
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            Services.Export.VirtualDjExporter.WriteVdjFolder(folderPath, projections);

            var defaultDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VirtualDJ", "database.xml");
            if (File.Exists(defaultDb))
            {
                var mergeConfirmed = await _dialogService.ConfirmAsync(
                    "Update VirtualDJ database.xml?",
                    $"Saved {Path.GetFileName(folderPath)}. Also merge ORBIT cues, BPM, and keys into VirtualDJ's database.xml? A timestamped backup will be created first.",
                    confirmLabel: "Merge cues",
                    cancelLabel: "Skip");

                if (mergeConfirmed)
                {
                    var merge = Services.Export.VirtualDjExporter.MergeDatabaseXml(defaultDb, projections);
                    if (!merge.Success)
                    {
                        _notificationService.Show("VirtualDJ Merge Skipped", merge.Error ?? "Could not update database.xml.", NotificationType.Warning);
                        return;
                    }
                    _notificationService.Show("VirtualDJ Exported", $"Saved {Path.GetFileName(folderPath)} and updated {merge.UpdatedSongs + merge.AddedSongs} tracks in database.xml.", NotificationType.Success);
                    return;
                }
            }

            _notificationService.Show("VirtualDJ Folder Exported", $"Exported {projections.Count} tracks to {Path.GetFileName(folderPath)}.", NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export VirtualDJ folder for {Playlist}", project.SourceTitle);
            _notificationService.Show("VirtualDJ Export Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteExportPlaylistSeratoAsync(object? param)
    {
        if (param is not PlaylistJob project) return;
        try
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            var localPaths = tracks.Select(t => t.ResolvedFilePath).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToList();
            if (localPaths.Count == 0)
            {
                _notificationService.Show("Nothing to Export", $"{project.SourceTitle} has no downloaded tracks.", NotificationType.Warning);
                return;
            }

            var crateResult = Services.Export.SeratoCrateExporter.WriteCratesForSelection(
                new[] { project.SourceTitle },
                localPaths);

            if (!crateResult.Success)
            {
                _notificationService.Show("Serato Export Blocked", crateResult.Error ?? "Could not write Serato crate.", NotificationType.Warning);
                return;
            }

            _notificationService.Show(
                "Serato Crate Exported",
                $"Wrote {crateResult.WrittenCratePaths.Count} crate file(s) with {crateResult.TracksWritten} track(s).",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export Serato crate for {Playlist}", project.SourceTitle);
            _notificationService.Show("Serato Export Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteRelocateMissingFolderAsync()
    {
        if (_fileRelocationService == null)
        {
            _notificationService.Show("Relocate Unavailable", "File relocation service is not available.", NotificationType.Warning);
            return;
        }

        try
        {
            var suggestions = await _fileRelocationService.SuggestMissingPrefixesAsync();
            if (suggestions.Count == 0)
            {
                _notificationService.Show("No missing files", "All library tracks already point to existing files on disk.", NotificationType.Information);
                return;
            }

            var suggestedPrefix = suggestions[0].Prefix;
            var oldPrefix = await _dialogService.ShowPromptAsync(
                "Relocate missing folder (1/2)",
                $"Enter the old folder prefix to replace ({suggestions[0].MissingCount} missing track(s) under this folder):",
                suggestedPrefix);
            if (string.IsNullOrWhiteSpace(oldPrefix)) return;

            var newFolder = await _dialogService.OpenFolderDialogAsync("Select the new folder location (2/2)");
            if (string.IsNullOrWhiteSpace(newFolder)) return;

            var preview = await _fileRelocationService.PlanRepointFolderAsync(oldPrefix.Trim(), newFolder);
            if (preview.FoundCount == 0)
            {
                _notificationService.Show(
                    "No matching files found",
                    $"0 of {preview.MatchedPrefixCount} missing track(s) under \"{oldPrefix}\" were found in \"{newFolder}\".",
                    NotificationType.Warning);
                return;
            }

            var message = $"Found {preview.FoundCount} track(s) at the new folder ({preview.StillMissingCount} still missing). No files will be moved on disk; only library paths will be re-pointed (undoable).";
            if (!string.IsNullOrWhiteSpace(preview.WarningMessage))
                message += "\n\n" + preview.WarningMessage;

            var sampleLines = preview.SampleMatches
                .Select(m => $"{Path.GetFileName(m.SourcePath)} → {m.TargetPath}")
                .ToList();

            var confirmed = await _dialogService.ConfirmListAsync(
                "Confirm bulk folder re-point",
                message,
                sampleLines,
                confirmLabel: $"Re-point {preview.FoundCount} track(s)",
                cancelLabel: "Cancel");
            if (!confirmed) return;

            var outcome = await _fileRelocationService.ExecuteRepointAsync(preview.Plan);
            _notificationService.Show(
                "Folder Relocated",
                $"Re-pointed {outcome.Moved} track(s) to \"{newFolder}\".",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk folder relocate failed");
            _notificationService.Show("Relocate Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecutePlayPlaylistCoreAsync(object? param, bool mixModeEnabled)
    {
        if (param is GenreKey genreParam)
        {
            await ExecutePlayGenreAsync(genreParam, shuffle: false, mixModeEnabled: mixModeEnabled);
            return;
        }

        var project = param as PlaylistJob ?? SelectedProject;
        if (project == null) return;

        var isCurrentView = SelectedProject != null && project.Id == SelectedProject.Id;
        List<PlaylistTrack> playable;

        if (isCurrentView)
        {
            // GV4 (#565): play the current view (all active filters incl. genre + grid sort),
            // working identically for playlists, All tracks (Guid.Empty), Liked songs, and genre views.
            playable = await Tracks.GetCurrentViewTracksAsync(playableOnly: true);
        }
        else
        {
            var tracks = await _libraryService.GetOrderedPlaylistTracksAsync(project.Id, downloadedOnly: true);
            playable = tracks.Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath)).ToList();
        }

        var viewTitle = isCurrentView && Tracks.GenreFilter is { } activeGenre && project.Id == Guid.Empty
            ? activeGenre.DisplayName
            : project.SourceTitle;

        if (playable.Count == 0)
        {
            _notificationService.Show("Nothing to Play", $"{viewTitle} has no downloaded tracks yet.", NotificationType.Warning);
            return;
        }

        // Start from the lead selected track when it's in the view.
        var selected = Tracks.LeadSelectedTrack?.Model;
        var startTrack = selected != null
            ? playable.FirstOrDefault(t => t.Id == selected.Id
                || (!string.IsNullOrEmpty(t.TrackUniqueHash) && t.TrackUniqueHash == selected.TrackUniqueHash))
            : null;

        _eventBus.Publish(new PlayAlbumRequestEvent(playable, mixModeEnabled, startTrack?.Id));
        _notificationService.Show(
            mixModeEnabled ? "Mixing" : "Playing",
            startTrack != null ? $"{viewTitle} — from \"{startTrack.Title}\"" : viewTitle,
            NotificationType.Information);
    }

    private async Task ExecutePlayGenreAsync(GenreKey key, bool shuffle, bool mixModeEnabled = false)
    {
        try
        {
            if (shuffle && !_playerViewModel.IsShuffling && _playerViewModel.ToggleShuffleCommand.CanExecute(null))
            {
                _playerViewModel.ToggleShuffleCommand.Execute(null);
            }

            List<PlaylistTrack> playable;
            if (Tracks.GenreFilter == key && (SelectedProject == null || SelectedProject.Id == Guid.Empty))
            {
                playable = await Tracks.GetCurrentViewTracksAsync(playableOnly: true);
            }
            else
            {
                var hashes = _genreIndex?.GetHashes(key);
                if (hashes == null || hashes.Count == 0)
                {
                    playable = new List<PlaylistTrack>();
                }
                else
                {
                    var tracks = await _libraryService.GetOrderedPlaylistTracksAsync(
                        Guid.Empty,
                        downloadedOnly: true,
                        hashFilter: hashes,
                        sortColumn: Tracks.SortColumn,
                        sortDescending: Tracks.SortDescending);
                    playable = tracks.Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath)).ToList();
                }
            }

            if (playable.Count == 0)
            {
                _notificationService.Show("Nothing to Play", $"{key.DisplayName} has no downloaded tracks yet.", NotificationType.Warning);
                return;
            }

            _eventBus.Publish(new PlayAlbumRequestEvent(playable, mixModeEnabled, null));
            _notificationService.Show(
                shuffle ? "Shuffling Genre" : (mixModeEnabled ? "Mixing Genre" : "Playing Genre"),
                key.DisplayName,
                NotificationType.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to play genre {Genre}", key.DisplayName);
            _notificationService.Show("Playback Failed", ex.Message, NotificationType.Error);
        }
    }

    private void ExecuteAnalyzeUnclassifiedGenre()
    {
        ShowGenre(GenreKey.Unclassified);
        _navigationService.NavigateTo("Analysis");
    }

    private Task ExecuteSaveGenreAsPlaylistParamAsync(object? param)
    {
        GenreKey? key = param is GenreKey gk ? gk : Tracks.GenreFilter;
        return key.HasValue ? ExecuteSaveGenreAsPlaylistAsync(key.Value) : Task.CompletedTask;
    }

    private Task ExecuteSaveGenreAsSmartCrateParamAsync(object? param)
    {
        GenreKey? key = param is GenreKey gk ? gk : Tracks.GenreFilter;
        return key.HasValue ? ExecuteSaveGenreAsSmartCrateAsync(key.Value) : Task.CompletedTask;
    }

    /// <summary>
    /// Prompts for a playlist name and saves a snapshot of the genre view's tracks in exact view order (Epic #561 / #566 GV5).
    /// </summary>
    private async Task ExecuteSaveGenreAsPlaylistAsync(GenreKey key)
    {
        try
        {
            var name = await _dialogService.ShowPromptAsync(
                "Save genre as playlist",
                "Playlist name:",
                key.DisplayName);
            if (string.IsNullOrWhiteSpace(name))
                return;

            var created = await SaveGenreAsPlaylistCoreAsync(key, name.Trim());
            if (created == null)
            {
                _notificationService.Show("Nothing to Save", $"{key.DisplayName} has no tracks to save.", NotificationType.Warning);
                return;
            }

            await Projects.LoadProjectsAsync();
            _notificationService.Show(
                "Playlist Saved",
                $"Saved {created.TotalTracks} tracks to \"{created.SourceTitle}\".",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save genre {Genre} as playlist", key.DisplayName);
            _notificationService.Show("Save Failed", ex.Message, NotificationType.Error);
        }
    }

    /// <summary>
    /// Testable core for GV5 "Save as playlist…": creates a new snapshot <see cref="PlaylistJob"/>
    /// with tracks in the exact current view order.
    /// </summary>
    internal async Task<PlaylistJob?> SaveGenreAsPlaylistCoreAsync(GenreKey key, string playlistName)
    {
        List<PlaylistTrack> viewTracks;
        if (Tracks.GenreFilter == key && (SelectedProject == null || SelectedProject.Id == Guid.Empty))
        {
            viewTracks = await Tracks.GetCurrentViewTracksAsync(playableOnly: false);
        }
        else
        {
            var hashes = _genreIndex?.GetHashes(key);
            if (hashes == null || hashes.Count == 0)
                return null;

            viewTracks = await _libraryService.GetOrderedPlaylistTracksAsync(
                Guid.Empty,
                hashFilter: hashes,
                sortColumn: Tracks.SortColumn,
                sortDescending: Tracks.SortDescending);
        }

        if (viewTracks.Count == 0)
            return null;

        var newJob = new PlaylistJob
        {
            Id = Guid.NewGuid(),
            SourceTitle = playlistName,
            SourceType = "GenreSnapshot",
            CreatedAt = DateTime.UtcNow,
            TotalTracks = viewTracks.Count,
            SuccessfulCount = viewTracks.Count(t => !string.IsNullOrEmpty(t.ResolvedFilePath))
        };

        for (int i = 0; i < viewTracks.Count; i++)
        {
            var src = viewTracks[i];
            newJob.PlaylistTracks.Add(new PlaylistTrack
            {
                Id = Guid.NewGuid(),
                PlaylistId = newJob.Id,
                Artist = src.Artist,
                Title = src.Title,
                Album = src.Album,
                TrackUniqueHash = src.TrackUniqueHash,
                Status = src.Status,
                ResolvedFilePath = src.ResolvedFilePath,
                Format = src.Format,
                Bitrate = src.Bitrate,
                CanonicalDuration = src.CanonicalDuration,
                BPM = src.BPM,
                MusicalKey = src.MusicalKey,
                Energy = src.Energy,
                AlbumArtUrl = src.AlbumArtUrl,
                SortOrder = i,
                TrackNumber = i + 1,
                AddedAt = DateTime.UtcNow
            });
        }

        await _libraryService.SavePlaylistJobWithTracksAsync(newJob);
        return newJob;
    }

    /// <summary>
    /// Prompts for a crate name and saves a live smart crate backed by <see cref="GenreKey"/> (Epic #561 / #566 GV5).
    /// </summary>
    private async Task ExecuteSaveGenreAsSmartCrateAsync(GenreKey key)
    {
        try
        {
            var name = await _dialogService.ShowPromptAsync(
                "Save genre as smart crate",
                "Smart crate name (updates live when genres change):",
                key.DisplayName);
            if (string.IsNullOrWhiteSpace(name))
                return;

            var rules = new SmartCrateRules();
            rules.SetGenreKey(key);
            await _smartCrateService.CreateCrateAsync(name.Trim(), rules);
            await SmartPlaylists.InitializeSmartPlaylists();

            _notificationService.Show(
                "Smart Crate Created",
                $"\"{name.Trim()}\" will track {key.DisplayName} automatically.",
                NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save genre {Genre} as smart crate", key.DisplayName);
            _notificationService.Show("Save Failed", ex.Message, NotificationType.Error);
        }
    }

    private async Task ExecuteDownloadAlbumAsync(object? param)
    {
        await ExecuteDownloadMissingAsync(param);
    }

    private async Task ExecuteQueueMissingWithPriorityAsync(object? param, PlaylistPriority urgency)
    {
        if (param is not PlaylistJob project) return;

        // Set the job-level priority tier so the scheduler picks these tracks first
        await _downloadManager.SetJobPriorityAsync(project.Id, urgency);

        var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
        var missing = tracks
            .Where(t => t.Status != TrackStatus.Downloaded && t.Status != TrackStatus.OnHold)
            .ToList();

        if (missing.Count == 0)
        {
            _notificationService.Show(
                "Nothing to Download",
                $"All tracks in \"{project.SourceTitle}\" are already downloaded.",
                NotificationType.Information);
            return;
        }

        // Track-level Priority 0 = explicit user action — bypasses lazy-buffer size gate.
        // Stamp SourcePlaylistName so the Downloads Hub groups correctly.
        foreach (var t in missing)
        {
            t.Priority = 0;
            if (string.IsNullOrEmpty(t.SourcePlaylistName))
            {
                t.SourcePlaylistName = project.SourceTitle;
                t.SourcePlaylistId   = project.Id;
            }
        }
        _downloadManager.QueueTracks(missing);

        var label = urgency switch
        {
            PlaylistPriority.Critical => "CRITICAL",
            PlaylistPriority.High     => "High",
            _                         => "Normal",
        };
        _notificationService.Show(
            $"Queued [{label}]",
            $"{missing.Count} missing track(s) from \"{project.SourceTitle}\" added to queue.",
            NotificationType.Success);
    }

    private async Task ExecuteDownloadMissingAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            var now = DateTime.UtcNow;
            if (_downloadMissingLastRun.TryGetValue(project.Id, out var last) && (now - last).TotalSeconds < 30)
                return;
            _downloadMissingLastRun[project.Id] = now;

            // Shown before the DB round-trip below, not after: LoadPlaylistTracksAsync eager-loads
            // TechnicalDetails/AudioFeatures for the whole playlist, which is not instant — without
            // an immediate acknowledgement here the click reads as not having registered at all.
            _notificationService.Show("Download Missing", $"Looking up missing tracks in {project.SourceTitle}...", NotificationType.Information);

            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            var missing = tracks.Where(t => t.Status != TrackStatus.Downloaded && t.Status != TrackStatus.OnHold).ToList();

            // OnHold tracks (background search gave up after repeated attempts) are a deliberate
            // dead end otherwise — normally the user has to find and click "Retry" on each one
            // individually in Downloads > Attention. That's exactly what clicking "Download
            // Missing" here is already asking for, so revive them into the same batch instead of
            // silently excluding them: without this, the sidebar could show a playlist with real
            // undownloaded tracks (visibly "Pending" in the track grid) while this command reports
            // "already downloaded or queued" — true only in the narrow DB sense that OnHold isn't
            // TrackStatus.Missing, misleading in the sense a user actually cares about.
            var onHold = tracks.Where(t => t.Status == TrackStatus.OnHold).ToList();
            if (onHold.Count > 0)
            {
                foreach (var t in onHold)
                {
                    t.Status = TrackStatus.Missing;
                    t.SearchRetryCount = 0;
                }
                await _libraryService.SavePlaylistTracksAsync(onHold);
                missing.AddRange(onHold);
            }

            if (missing.Any())
            {
                _notificationService.Show("Queued", $"{missing.Count} missing tracks from {project.SourceTitle} added to queue.", NotificationType.Success);

                foreach (var t in missing)
                {
                    t.Priority = 0;
                    if (string.IsNullOrEmpty(t.SourcePlaylistName))
                    {
                        t.SourcePlaylistName = project.SourceTitle;
                        t.SourcePlaylistId   = project.Id;
                    }
                }
                _downloadManager.QueueTracks(missing);
            }
            else
            {
                _notificationService.Show("Download Missing", "All tracks are already downloaded or queued.", NotificationType.Information);
            }
        }
    }

    private async Task ExecuteForceRedownloadAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            if (tracks.Any())
            {
                bool confirm = await _dialogService.ConfirmAsync(
                    "Force Redownload",
                    $"This cancels any in-progress downloads and re-queues all {tracks.Count} track(s) in '{project.SourceTitle}' from scratch. Continue?");
                if (!confirm) return;

                _notificationService.Show("Force Downloading Playlist", $"Force queueing {tracks.Count} tracks from {project.SourceTitle}...", NotificationType.Information);

                foreach (var t in tracks)
                {
                    _downloadManager.CancelTrack(t.TrackUniqueHash);
                    t.Priority = 0;
                    t.Status = TrackStatus.Missing;
                    t.IsClearedFromDownloadCenter = false;
                }

                // One batched upsert instead of one DB round-trip per track.
                await _libraryService.SavePlaylistTracksAsync(tracks);

                await Task.Delay(100);
                _downloadManager.QueueTracks(tracks);
            }
        }
    }

    private async Task ExecuteAcquireMissingTracksAsync(object? param)
    {
        if (param is PlaylistJob project)
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(project.Id);
            // Status=Downloaded overrides a stale/drifted Ghost — see PlaylistTrackViewModel.IsGhost.
            // Without this, a track already downloaded (file exists, plays fine) but stuck showing
            // Ghost got its Status reset back to Missing here — an actively destructive side effect
            // of the drift bug, not just a cosmetic badge issue.
            var ghostTracks = tracks.Where(t => t.AvailabilityState == TrackAvailabilityState.Ghost && t.Status != TrackStatus.Downloaded).ToList();
            if (ghostTracks.Any())
            {
                _notificationService.Show("Acquiring Missing Tracks", $"Starting acquisition for {ghostTracks.Count} tracks from {project.SourceTitle}...", NotificationType.Information);
                
                foreach (var t in ghostTracks)
                {
                    t.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                    t.Status = TrackStatus.Missing;
                    t.SearchRetryCount = 0;
                    t.NotFoundRestartCount = 0;
                }
                
                try
                {
                    await using var dbContext = _dbFactory.CreateDbContext();

                    // Batch both lookups instead of 2 individual queries per ghost track.
                    var ghostIds = ghostTracks.Select(t => t.Id).ToList();
                    var ghostHashes = ghostTracks.Select(t => t.TrackUniqueHash).ToList();

                    var dbTracksById = await dbContext.PlaylistTracks
                        .Where(dt => ghostIds.Contains(dt.Id))
                        .ToDictionaryAsync(dt => dt.Id);
                    var masterTracksByHash = await dbContext.Tracks
                        .Where(mt => ghostHashes.Contains(mt.GlobalId))
                        .ToDictionaryAsync(mt => mt.GlobalId);

                    foreach (var track in ghostTracks)
                    {
                        if (dbTracksById.TryGetValue(track.Id, out var dbTrack))
                        {
                            dbTrack.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                            dbTrack.Status = TrackStatus.Missing;
                            dbTrack.SearchRetryCount = 0;
                            dbTrack.NotFoundRestartCount = 0;
                        }

                        if (masterTracksByHash.TryGetValue(track.TrackUniqueHash, out var masterTrack))
                        {
                            masterTrack.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                            masterTrack.SearchRetryCount = 0;
                            masterTrack.NotFoundRestartCount = 0;
                        }
                    }
                    await dbContext.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist database updates for manual bulk acquisition");
                }
                
                _downloadManager.QueueTracks(ghostTracks);
            }
            else
            {
                _notificationService.Show("Acquire Missing Tracks", "No ghost tracks found in this playlist.", NotificationType.Information);
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

                // Push the new name into active download contexts so the Download Center
                // and any other screen reflecting SourcePlaylistName update immediately.
                _downloadManager.UpdatePlaylistSourceName(project.Id, newTitle);
                _eventBus.Publish(new ProjectUpdatedEvent(project.Id));

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

    private void ExecuteOpenSourceUrl(object? param)
    {
        if (param is PlaylistJob project)
        {
            Projects.OpenSourceUrlCommand.Execute(project);
        }
        else if (SelectedProject != null)
        {
            Projects.OpenSourceUrlCommand.Execute(SelectedProject);
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

    private async Task ExecuteSyncPhysicalLibraryAsync()
    {
        try
        {
            IsLoading = true;
            _notificationService.Show("Syncing Library", "Scanning for missing files...", NotificationType.Information);

            var entries = await _databaseService.GetAllLibraryEntriesAsync();

            // Thousands of synchronous File.Exists syscalls (worse yet on a slow/disconnected
            // network share) — this command was unreachable dead code until the Orphaned Tracks
            // panel got wired up, so this was never actually exercised on the UI thread before.
            var orphans = await Task.Run(() =>
            {
                var result = new List<LibraryEntryEntity>();
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrEmpty(entry.FilePath) && !System.IO.File.Exists(entry.FilePath))
                    {
                        result.Add(entry);
                    }
                }
                return result;
            });

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
            _notificationService.Show("Searching Duplicates", "Analyzing library for duplicates...", NotificationType.Information);

            var entries = await _libraryService.LoadAllLibraryEntriesAsync();

            // Exact identity duplicates. UniqueHash is the upsert key for LibraryEntry
            // (SaveOrUpdateLibraryEntryAsync dedupes on it), so this essentially never fires
            // in practice — kept as a defensive first pass in case that invariant is ever violated.
            var exactGroups = entries
                .GroupBy(e => e.UniqueHash)
                .Where(g => g.Count() > 1);

            // Fuzzy content duplicates: the same recording re-imported/re-encoded under a
            // different UniqueHash (which is derived from Artist/Title, so a retag — "feat.",
            // "(Remastered)", a typo fix — silently produces a new identity for the same audio).
            // AudioFingerprint/SpectralHash exist on the entities but are never actually computed
            // anywhere in the analysis pipeline, so instead of relying on those dead columns we
            // build an equivalent signature from fields that genuinely come from decoding the
            // audio (duration, BPM, musical key), which stay stable across re-encodes even when
            // tags differ. Requiring a normalized-artist match too keeps false positives low.
            var fuzzyGroups = entries
                .Where(e => (e.DurationSeconds ?? e.CanonicalDuration ?? 0) > 0
                            && (e.BPM ?? e.ManualBPM ?? e.SpotifyBPM ?? 0) > 0)
                .GroupBy(BuildContentSignature)
                .Where(g => g.Count() > 1);

            var duplicateHashes = exactGroups
                .Concat(fuzzyGroups)
                .SelectMany(g => g.Select(e => e.UniqueHash))
                .ToHashSet();

            if (!duplicateHashes.Any())
            {
                _notificationService.Show("Clean Library", "No duplicates detected.", NotificationType.Success);
                Tracks.DuplicateHashesFilter = null;
            }
            else
            {
                _notificationService.Show("Review Required", $"Found {duplicateHashes.Count} likely duplicate track(s).", NotificationType.Warning);
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

    /// <summary>
    /// Builds a content-based signature for fuzzy duplicate detection: normalized artist +
    /// duration bucketed to the nearest 2 seconds + rounded BPM + musical key. Two entries
    /// sharing this signature are very likely the same recording, even if title/tags differ.
    /// </summary>
    internal static string BuildContentSignature(LibraryEntry entry)
    {
        var duration = entry.DurationSeconds ?? entry.CanonicalDuration ?? 0;
        var durationBucket = (int)Math.Round(duration / 2.0) * 2;
        var bpm = entry.BPM ?? entry.ManualBPM ?? entry.SpotifyBPM ?? 0;
        var bpmBucket = (int)Math.Round(bpm);
        var key = (entry.CamelotKey ?? entry.MusicalKey ?? entry.ManualKey ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedArtist = NormalizeForSignature(entry.Artist);

        return $"{normalizedArtist}|{durationBucket}|{bpmBucket}|{key}";
    }

    private static string NormalizeForSignature(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

}
