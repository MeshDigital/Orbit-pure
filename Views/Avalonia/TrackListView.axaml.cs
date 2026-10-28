using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Views.Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia;

public partial class TrackListView : UserControl
{
    private VirtualGrid? _grid;
    private ScrollViewer? _scroller;
    private TrackListViewModel? _viewModel;

    public TrackListView()
    {
        InitializeComponent();

        _grid = this.FindControl<VirtualGrid>("TrackGrid");
        if (_grid != null)
        {
            _grid.SelectionChanged += OnTrackGridSelectionChanged;
            _grid.ItemDragStarted += OnTrackGridItemDragStarted;
            _grid.ItemActivated += (_, index) =>
            {
                if (DataContext is not TrackListViewModel vm || vm.Operations?.PlayTrackCommand is not { } play)
                    return;
                if (index < 0 || index >= vm.FilteredTracks.Count)
                    return;
                var track = vm.FilteredTracks[index];
                if (!track.IsPlaceholder && play.CanExecute(track))
                    play.Execute(track);
            };
            // The list's width decides which optional columns fit (TrackListColumnLayout).
            _grid.SizeChanged += (_, e) => { if (DataContext is TrackListViewModel vm) vm.ListWidth = e.NewSize.Width; };
            _grid.AttachedToVisualTree += (_, _) => AttachScroller();
        }

        DataContextChanged += (_, _) => BindViewModel(DataContext as TrackListViewModel);
        AttachedToVisualTree += (_, _) => BindViewModel(DataContext as TrackListViewModel);
        DetachedFromVisualTree += (_, _) => BindViewModel(null);
    }

    private void AttachScroller()
    {
        if (_grid?.FindControl<ScrollViewer>("PartScrollViewer") is not { } scroller) return;
        if (!ReferenceEquals(_scroller, scroller))
        {
            if (_scroller is not null) _scroller.ScrollChanged -= OnTrackScrollChanged;
            _scroller = scroller;
            _scroller.ScrollChanged += OnTrackScrollChanged;
        }
        if (DataContext is TrackListViewModel vm) ScrollPositionRestorer.Restore(scroller, vm.ListScrollOffset);
    }

    private void OnTrackScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is TrackListViewModel vm && sender is ScrollViewer scroller)
            vm.OnListScrolled(scroller.Offset.Y);
    }

    private void BindViewModel(TrackListViewModel? next)
    {
        if (ReferenceEquals(_viewModel, next))
        {
            if (next is not null && _scroller is not null)
                ScrollPositionRestorer.Restore(_scroller, next.ListScrollOffset);
            return;
        }

        if (_viewModel is not null) _viewModel.ScrollRestoreRequested -= OnScrollRestoreRequested;
        _viewModel = next;
        if (_viewModel is not null)
        {
            _viewModel.ScrollRestoreRequested += OnScrollRestoreRequested;
            if (_grid?.Bounds.Width > 0) _viewModel.ListWidth = _grid.Bounds.Width;
            if (_scroller is not null) ScrollPositionRestorer.Restore(_scroller, _viewModel.ListScrollOffset);
        }
    }

    private void OnScrollRestoreRequested(object? sender, double offsetY)
    {
        if (_scroller is not null) ScrollPositionRestorer.Restore(_scroller, offsetY);
    }

    /// <summary>
    /// Initiates a drag of a library track so it can be dropped onto a playlist in the sidebar
    /// (LibraryPage's OnPlaylistDrop reads DragContext.LibraryTrackFormat) or onto the player
    /// queue. Previously nothing in the codebase ever set this format, so dragging a track from
    /// the library onto a playlist silently did nothing despite the drop handler existing.
    /// </summary>
    private async void OnTrackGridItemDragStarted(object? sender, VirtualGridItemDragEventArgs e)
    {
        if (e.Item is not PlaylistTrackViewModel track || string.IsNullOrEmpty(track.GlobalId))
            return;

        var data = new DataObject();
        data.Set(DragContext.LibraryTrackFormat, track.GlobalId);

        await DragDrop.DoDragDrop(e.PointerEventArgs, data, DragDropEffects.Copy);
    }

    private void OnTrackColumnResizeDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is TrackListViewModel vm && sender is Control { Tag: string column })
            vm.ResizeColumn(column, e.Vector.X);
    }

    private void OnTrackColumnResizeCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is TrackListViewModel vm) vm.SaveViewStateNow();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnTrackGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not TrackListViewModel vm)
            return;

        if (sender is not VirtualGrid grid)
            return;

        var selected = grid.SelectedItems
            .Cast<ViewModels.PlaylistTrackViewModel>()
            .ToList();

        vm.UpdateSelection(selected);
    }
}