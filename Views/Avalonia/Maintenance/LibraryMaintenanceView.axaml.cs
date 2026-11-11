using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Maintenance;

namespace SLSKDONET.Views.Avalonia.Maintenance;

public partial class LibraryMaintenanceView : UserControl
{
    public LibraryMaintenanceView()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) =>
        (DataContext as LibraryMaintenanceViewModel)?.Close();
}
