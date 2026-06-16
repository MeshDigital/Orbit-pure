using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
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
            grid.SelectionChanged += OnTrackGridSelectionChanged;
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