using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class BulkRenameDialog : Window
{
    public BulkRenameDialog()
    {
        InitializeComponent();
    }

    public BulkRenameDialog(BulkRenameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BulkRenameViewModel vm && vm.CanSave)
        {
            Close(new BulkRenameResult { IsConfirmed = true, Pattern = vm.Pattern });
        }
        else
        {
            Close(new BulkRenameResult { IsConfirmed = false });
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(new BulkRenameResult { IsConfirmed = false });
    }
}
