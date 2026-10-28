using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Downloads;

namespace SLSKDONET.Views.Avalonia.Dialogs;

/// <summary>
/// Read-only spectral forensics report for one download. The layout lives in XAML
/// (compiled bindings to <see cref="UnifiedTrackViewModel"/>); this file only handles the
/// two buttons.
/// </summary>
public partial class SpectralForensicsDialog : Window
{
    public SpectralForensicsDialog()
    {
        InitializeComponent();
    }

    private void Requeue_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UnifiedTrackViewModel vm && vm.RetryCommand.CanExecute(null))
            vm.RetryCommand.Execute(null);
        Close();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
