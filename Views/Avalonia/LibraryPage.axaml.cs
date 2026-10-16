using System;
using System.Linq;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Views.Avalonia;

public partial class LibraryPage : UserControl
{
    private readonly ILogger<LibraryPage>? _logger;
    private LibraryViewModel? _boundLibraryViewModel;
    private Point? _playlistDragStartPoint;

    public LibraryPage()
    {
        InitializeComponent();
        AttachLibraryViewModel(DataContext as LibraryViewModel);
    }

    public LibraryPage(LibraryViewModel viewModel, ILogger<LibraryPage>? logger = null)
    {
        _logger = logger;
        DataContext = viewModel; // CRITICAL: Set DataContext from DI
        InitializeComponent();
        AttachLibraryViewModel(viewModel);
        
        // Drag-drop onto the playlist tree (tracks into a playlist, playlists/folders into a folder)
        AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        AddHandler(DragDrop.DropEvent, OnPlaylistDrop);

        // Playlist Folder Tree: drag-initiation for reorganizing playlists/folders
        var playlistTreeView = this.FindControl<TreeView>("PlaylistTreeView");
        if (playlistTreeView != null)
        {
            DragDrop.SetAllowDrop(playlistTreeView, true);
            playlistTreeView.AddHandler(PointerPressedEvent, OnPlaylistTreeItemPointerPressed, RoutingStrategies.Tunnel);
            playlistTreeView.AddHandler(PointerMovedEvent, OnPlaylistTreeItemPointerMoved, RoutingStrategies.Tunnel);
            playlistTreeView.AddHandler(PointerReleasedEvent, OnPlaylistTreeItemPointerReleased, RoutingStrategies.Tunnel);
        }
    }

    private void OpenDiscover_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel { SelectedProject: { } project } && project.Id != Guid.Empty)
            ReactiveUI.MessageBus.Current.SendMessage(new SLSKDONET.Events.OpenPlaylistDiscoverEvent(project.Id, project.SourceTitle));
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var stepSw = System.Diagnostics.Stopwatch.StartNew();
        base.OnLoaded(e);
        _logger?.LogInformation("[PERF] LibraryPage.OnLoaded: base.OnLoaded took {Ms}ms", stepSw.ElapsedMilliseconds);
        
        // BUGFIX: Ensure projects are loaded when user navigates to Library page
        // Previously only loaded during startup or after imports, not on manual navigation
        if (DataContext is LibraryViewModel vm)
        {
            try
            {
                stepSw.Restart();
                // FIX: Check if projects are already loaded to prevent aggressive reloading on tab switch
                if (!vm.Projects.AllProjects.Any())
                {
                    _logger?.LogInformation("[PERF] LibraryPage.OnLoaded: Starting LoadProjectsAsync");
                    await vm.LoadProjectsAsync();
                    _logger?.LogInformation("[PERF] LoadProjectsAsync took {Ms}ms", stepSw.ElapsedMilliseconds);
                }
                else
                {
                    _logger?.LogInformation("[PERF] Projects already loaded ({Count} items), check took {Ms}ms", vm.Projects.AllProjects.Count, stepSw.ElapsedMilliseconds);
                    
                    // FIX: Eagerly select first project if none selected to avoid 3s UI binding delay
                    if (vm.Projects.SelectedProject == null && vm.Projects.FilteredProjects.Count > 0)
                    {
                        stepSw.Restart();
                        _logger?.LogInformation("[PERF] Selecting first project...");
                        vm.Projects.SelectedProject = vm.Projects.FilteredProjects[0];
                        _logger?.LogInformation("[PERF] Project selection took {Ms}ms", stepSw.ElapsedMilliseconds);
                    }
                }
                
                stepSw.Restart();
                _logger?.LogInformation("[PERF] LoadProjectsAsync completed. AllProjects count: {Count}", vm.Projects.AllProjects.Count);
                _logger?.LogInformation("[PERF] First project: {Title}", vm.Projects.AllProjects.FirstOrDefault()?.SourceTitle ?? "none");
                _logger?.LogInformation("[PERF] Logging took {Ms}ms", stepSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PERF] EXCEPTION in LibraryPage.OnLoaded");
            }
        }
        else
        {
            _logger?.LogWarning("[PERF] LibraryPage.OnLoaded: DataContext is NOT LibraryViewModel!");
        }
        
        totalSw.Stop();
        _logger?.LogInformation("[PERF] TOTAL LibraryPage.OnLoaded took {Ms}ms", totalSw.ElapsedMilliseconds);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        AttachLibraryViewModel(null);
    }

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        // Reorganizing the tree itself: moving a playlist or folder into another folder
        if (e.Data.Contains(DragContext.PlaylistCardNodeFormat) || e.Data.Contains(DragContext.PlaylistFolderNodeFormat))
        {
            e.DragEffects = DragDropEffects.Move;
            return;
        }

        // Accept tracks from library or queue, to be added to the playlist dropped onto
        if (e.Data.Contains(DragContext.LibraryTrackFormat) || e.Data.Contains(DragContext.QueueTrackFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        var treeViewItem = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();

        // Reorganizing the tree: moving a dragged playlist/folder into the folder dropped on
        // (or to root level, if dropped on empty tree space).
        if (e.Data.Contains(DragContext.PlaylistCardNodeFormat) || e.Data.Contains(DragContext.PlaylistFolderNodeFormat))
        {
            if (DataContext is not LibraryViewModel libVm)
                return;

            Guid? targetFolderId;
            if (treeViewItem?.DataContext is PlaylistTreeFolderNodeViewModel targetFolderNode)
            {
                targetFolderId = targetFolderNode.Folder.Id;
            }
            else if (treeViewItem != null)
            {
                // Dropped on a playlist row - not a valid folder target
                return;
            }
            else
            {
                targetFolderId = null; // Empty tree space -> move to root
            }

            if (e.Data.Get(DragContext.PlaylistCardNodeFormat) is string playlistIdStr &&
                Guid.TryParse(playlistIdStr, out var playlistId))
            {
                _ = libVm.Projects.MovePlaylistToFolderAsync(playlistId, targetFolderId);
            }
            else if (e.Data.Get(DragContext.PlaylistFolderNodeFormat) is string folderIdStr &&
                     Guid.TryParse(folderIdStr, out var draggedFolderId))
            {
                _ = libVm.Projects.MoveFolderAsync(draggedFolderId, targetFolderId);
            }

            return;
        }

        // Adding a track to the playlist dropped onto
        if (treeViewItem?.DataContext is not PlaylistTreeCardNodeViewModel targetCardNode)
            return;
        var targetPlaylist = targetCardNode.Card.Model;

        // Get the dragged track GlobalId
        string? trackGlobalId = null;
        if (e.Data.Contains(DragContext.LibraryTrackFormat))
        {
            trackGlobalId = e.Data.Get(DragContext.LibraryTrackFormat) as string;
        }
        else if (e.Data.Contains(DragContext.QueueTrackFormat))
        {
            trackGlobalId = e.Data.Get(DragContext.QueueTrackFormat) as string;
        }

        if (string.IsNullOrEmpty(trackGlobalId))
            return;

        // Find the track in the library
        if (DataContext is not LibraryViewModel libraryViewModel)
            return;

        var sourceTrack = libraryViewModel.CurrentProjectTracks
            .FirstOrDefault(t => t.GlobalId == trackGlobalId);

        if (sourceTrack == null)
        {
            // Try to find in player queue
            var playerViewModel = libraryViewModel.PlayerViewModel;

            sourceTrack = playerViewModel?.Queue
                .FirstOrDefault(t => t.GlobalId == trackGlobalId);
        }

        if (sourceTrack != null && targetPlaylist != null)
        {
            // Use existing AddToPlaylist method (includes deduplication)
            libraryViewModel.AddToPlaylist(targetPlaylist, sourceTrack);
        }
    }

    private void OnPlaylistTreeItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var item = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();
            if (item?.DataContext is PlaylistTreeFolderNodeViewModel or PlaylistTreeCardNodeViewModel)
            {
                _playlistDragStartPoint = e.GetPosition(this);
            }
        }
    }

    private async void OnPlaylistTreeItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_playlistDragStartPoint.HasValue && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var currentPoint = e.GetPosition(this);
            var diff = currentPoint - _playlistDragStartPoint.Value;

            if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
            {
                var item = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();
                var data = new DataObject();

                if (item?.DataContext is PlaylistTreeCardNodeViewModel cardNode)
                {
                    data.Set(DragContext.PlaylistCardNodeFormat, cardNode.Card.Model.Id.ToString());
                }
                else if (item?.DataContext is PlaylistTreeFolderNodeViewModel folderNode)
                {
                    data.Set(DragContext.PlaylistFolderNodeFormat, folderNode.Folder.Id.ToString());
                }
                else
                {
                    _playlistDragStartPoint = null;
                    return;
                }

                _playlistDragStartPoint = null;
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
        }
    }

    private void OnPlaylistTreeItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _playlistDragStartPoint = null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachLibraryViewModel(DataContext as LibraryViewModel);
    }

    private void AttachLibraryViewModel(LibraryViewModel? viewModel)
    {
        if (_boundLibraryViewModel is not null)
            _boundLibraryViewModel.PropertyChanged -= OnLibraryViewModelPropertyChanged;

        _boundLibraryViewModel = viewModel;

        if (_boundLibraryViewModel is not null)
            _boundLibraryViewModel.PropertyChanged += OnLibraryViewModelPropertyChanged;
    }

    private void OnLibraryViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(LibraryViewModel.SavedDoublesSidebarFocusRequestVersion), StringComparison.Ordinal))
            return;

        var target = this.FindControl<Control>("SavedDoublesSidebarSection");
        target?.BringIntoView();
    }
}
