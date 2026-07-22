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
        }
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