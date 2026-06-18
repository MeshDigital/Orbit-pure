using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class BatchTagEditDialog : Window
{
    public BatchTagEditDialog()
    {
        InitializeComponent();
    }

    public BatchTagEditDialog(BatchTagEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BatchTagEditViewModel vm)
        {
            var result = new BatchTagEditResult
            {
                IsConfirmed = true,
                Artist = vm.Artist,
                Title = vm.Title,
                Album = vm.Album,
                Genre = vm.Genre,
                Year = vm.Year,
                NewFileName = vm.IsSingleTrack ? vm.NewFileName : null,
            };
            Close(result);
        }
        else
        {
            Close(new BatchTagEditResult { IsConfirmed = false });
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(new BatchTagEditResult { IsConfirmed = false });
    }
}
