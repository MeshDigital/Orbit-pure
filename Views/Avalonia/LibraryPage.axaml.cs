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
    private TreeViewItem? _playlistDragSource;
    private TreeViewItem? _playlistDropTarget;

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
        AddHandler(DragDrop.DragLeaveEvent, OnPlaylistDragLeave);
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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // The playlist panel's max width is a share of the page (LibraryViewModel.FitNavPanelMaxWidth).
        (DataContext as LibraryViewModel)?.OnLibraryPageResized(e.NewSize.Width);
    }

    /// <summary>Below this width the playlist header drops its action labels (icon buttons with
    /// tooltips remain), so the title and counts keep room to trim instead of being overlapped.</summary>
    internal const double NarrowPlaylistHeaderWidth = 560;

    private void OnPlaylistHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control header)
            header.Classes.Set("narrow", e.NewSize.Width < NarrowPlaylistHeaderWidth);
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
        var treeViewItem = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();
        var isPlaylistDrag = e.Data.Contains(DragContext.PlaylistCardNodeFormat);
        var isFolderDrag = e.Data.Contains(DragContext.PlaylistFolderNodeFormat);

        if (isPlaylistDrag || isFolderDrag)
        {
            if (isPlaylistDrag && treeViewItem?.DataContext is PlaylistTreeCardNodeViewModel targetCard)
            {
                var draggedId = e.Data.Get(DragContext.PlaylistCardNodeFormat) as string;
                var isSelfDrop = string.Equals(draggedId, targetCard.Card.Model.Id.ToString(), StringComparison.Ordinal);
                if (isSelfDrop)
                {
                    ClearPlaylistDropTarget();
                }
                else
                {
                    SetPlaylistDropTarget(treeViewItem, IsPlaylistInsertionAfter(e, treeViewItem));
                }

                e.DragEffects = DragDropEffects.Move;
            }
            else if (treeViewItem?.DataContext is PlaylistTreeFolderNodeViewModel)
            {
                SetFolderDropTarget(treeViewItem);
                e.DragEffects = DragDropEffects.Move;
            }
            else
            {
                ClearPlaylistDropTarget();
                // Empty tree space remains a valid move-to-root target. A folder cannot be
                // inserted between playlists, so don't advertise a drop that would do nothing.
                e.DragEffects = treeViewItem is null ? DragDropEffects.Move : DragDropEffects.None;
            }

            return;
        }

        ClearPlaylistDropTarget();

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

    private async void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        var treeViewItem = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();

        try
        {
            // Playlist rows are insertion targets; folder rows and empty tree space retain their
            // original move-into-folder / move-to-root behavior.
            if (e.Data.Contains(DragContext.PlaylistCardNodeFormat) || e.Data.Contains(DragContext.PlaylistFolderNodeFormat))
            {
                if (DataContext is not LibraryViewModel libVm)
                    return;

                if (e.Data.Get(DragContext.PlaylistCardNodeFormat) is string playlistIdStr &&
                    Guid.TryParse(playlistIdStr, out var playlistId))
                {
                    if (treeViewItem?.DataContext is PlaylistTreeCardNodeViewModel targetCardNode)
                    {
                        await libVm.Projects.ReorderPlaylistAsync(
                            playlistId,
                            targetCardNode.Card.Model.Id,
                            IsPlaylistInsertionAfter(e, treeViewItem));
                        e.Handled = true;
                        return;
                    }

                    if (!TryResolveFolderDropTarget(treeViewItem, out var targetFolderId))
                        return;

                    await libVm.Projects.MovePlaylistToFolderAsync(playlistId, targetFolderId);
                    e.Handled = true;
                }
                else if (e.Data.Get(DragContext.PlaylistFolderNodeFormat) is string folderIdStr &&
                         Guid.TryParse(folderIdStr, out var draggedFolderId) &&
                         TryResolveFolderDropTarget(treeViewItem, out var targetFolderId))
                {
                    await libVm.Projects.MoveFolderAsync(draggedFolderId, targetFolderId);
                    e.Handled = true;
                }

                return;
            }

            // Adding a track to the playlist dropped onto
            if (treeViewItem?.DataContext is not PlaylistTreeCardNodeViewModel targetCard)
                return;
            var targetPlaylist = targetCard.Card.Model;

            string? trackGlobalId = null;
            if (e.Data.Contains(DragContext.LibraryTrackFormat))
            {
                trackGlobalId = e.Data.Get(DragContext.LibraryTrackFormat) as string;
            }
            else if (e.Data.Contains(DragContext.QueueTrackFormat))
            {
                trackGlobalId = e.Data.Get(DragContext.QueueTrackFormat) as string;
            }

            if (string.IsNullOrEmpty(trackGlobalId) || DataContext is not LibraryViewModel libraryViewModel)
                return;

            var sourceTrack = libraryViewModel.CurrentProjectTracks
                .FirstOrDefault(t => t.GlobalId == trackGlobalId);

            if (sourceTrack == null)
            {
                sourceTrack = libraryViewModel.PlayerViewModel?.Queue
                    .FirstOrDefault(t => t.GlobalId == trackGlobalId);
            }

            if (sourceTrack != null && targetPlaylist != null)
            {
                // Use existing AddToPlaylist method (includes deduplication)
                libraryViewModel.AddToPlaylist(targetPlaylist, sourceTrack);
            }
        }
        finally
        {
            ClearPlaylistDropTarget();
        }
    }

    private static bool IsPlaylistInsertionAfter(DragEventArgs e, TreeViewItem target) =>
        e.GetPosition(target).Y >= target.Bounds.Height / 2;

    private static bool TryResolveFolderDropTarget(TreeViewItem? treeViewItem, out Guid? folderId)
    {
        if (treeViewItem is null)
        {
            folderId = null;
            return true;
        }

        if (treeViewItem.DataContext is PlaylistTreeFolderNodeViewModel folderNode)
        {
            folderId = folderNode.Folder.Id;
            return true;
        }

        folderId = null;
        return false;
    }

    private void SetPlaylistDropTarget(TreeViewItem target, bool insertAfter)
    {
        if (!ReferenceEquals(_playlistDropTarget, target))
            ClearPlaylistDropTarget();

        _playlistDropTarget = target;
        target.Classes.Set("folder-drop-target", false);
        target.Classes.Set("drop-before", !insertAfter);
        target.Classes.Set("drop-after", insertAfter);
    }

    private void SetFolderDropTarget(TreeViewItem target)
    {
        if (!ReferenceEquals(_playlistDropTarget, target))
            ClearPlaylistDropTarget();

        _playlistDropTarget = target;
        target.Classes.Set("drop-before", false);
        target.Classes.Set("drop-after", false);
        target.Classes.Set("folder-drop-target", true);
    }

    private void OnPlaylistDragLeave(object? sender, RoutedEventArgs e) => ClearPlaylistDropTarget();

    private void ClearPlaylistDropTarget()
    {
        if (_playlistDropTarget is null) return;
        _playlistDropTarget.Classes.Set("drop-before", false);
        _playlistDropTarget.Classes.Set("drop-after", false);
        _playlistDropTarget.Classes.Set("folder-drop-target", false);
        _playlistDropTarget = null;
    }

    private void OnPlaylistTreeItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var item = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();
            if (item?.DataContext is PlaylistTreeFolderNodeViewModel or PlaylistTreeCardNodeViewModel)
            {
                _playlistDragStartPoint = e.GetPosition(this);
                // Keep the pressed row: the pointer can move onto an expander, icon or adjacent
                // row before the threshold is reached, but that must not change the drag source.
                _playlistDragSource = item;
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
                var item = _playlistDragSource;
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
                    _playlistDragSource = null;
                    return;
                }

                _playlistDragStartPoint = null;
                item.Classes.Set("drag-source", true);
                try
                {
                    await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
                }
                finally
                {
                    item.Classes.Set("drag-source", false);
                    if (ReferenceEquals(_playlistDragSource, item))
                        _playlistDragSource = null;
                    ClearPlaylistDropTarget();
                }
            }
        }
    }

    private void OnPlaylistTreeItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _playlistDragStartPoint = null;
        if (_playlistDragSource?.Classes.Contains("drag-source") != true)
            _playlistDragSource = null;
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
