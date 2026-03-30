using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Views.Avalonia;

public partial class TrackListView : UserControl
{
    public TrackListView()
    {
        InitializeComponent();

        var grid = this.FindControl<DataGrid>("TrackGrid");
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

        if (sender is not DataGrid grid)
            return;

        var selected = grid.SelectedItems
            .Cast<ViewModels.PlaylistTrackViewModel>()
            .ToList();

        vm.UpdateSelection(selected);
    }
}