using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class BulkMoveOrCopyDialog : Window
{
    public BulkMoveOrCopyDialog()
    {
        InitializeComponent();
    }

    public BulkMoveOrCopyDialog(BulkMoveOrCopyViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BulkMoveOrCopyViewModel vm)
        {
            Close(new BulkMoveOrCopyResult { IsConfirmed = true, IsCopy = vm.IsCopy });
        }
        else
        {
            Close(new BulkMoveOrCopyResult { IsConfirmed = false });
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(new BulkMoveOrCopyResult { IsConfirmed = false });
    }
}
