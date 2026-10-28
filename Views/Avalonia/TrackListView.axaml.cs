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
    public TrackListView()
    {
        InitializeComponent();

        var grid = this.FindControl<VirtualGrid>("TrackGrid");
        if (grid != null)
        {
            grid.SelectionChanged += OnTrackGridSelectionChanged;
            grid.ItemDragStarted += OnTrackGridItemDragStarted;
            // The list's width decides which optional columns fit (TrackListColumnLayout).
            grid.SizeChanged += (_, e) => { if (DataContext is TrackListViewModel vm) vm.ListWidth = e.NewSize.Width; };
            // Scrolling the list collapses the playlist header above it (LibraryPage).
            bool scrollHooked = false;
            grid.AttachedToVisualTree += (_, _) =>
            {
                if (scrollHooked || grid.FindControl<ScrollViewer>("PartScrollViewer") is not { } scroller) return;
                scrollHooked = true;
                scroller.ScrollChanged += (_, _) => { if (DataContext is TrackListViewModel vm) vm.OnListScrolled(scroller.Offset.Y); };
            };
        }
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TrackListViewModel vm && grid != null && grid.Bounds.Width > 0) vm.ListWidth = grid.Bounds.Width;
        };
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