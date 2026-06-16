using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class PlaylistPickerDialog : Window
{
    public PlaylistPickerDialog()
    {
        InitializeComponent();
    }

    public PlaylistPickerDialog(PlaylistPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaylistPickerViewModel vm)
        {
            var result = new PlaylistPickerResult
            {
                IsConfirmed = true,
                SelectedPlaylist = vm.SelectedPlaylist,
                NewPlaylistName = vm.NewPlaylistName
            };
            Close(result);
        }
        else
        {
            Close(new PlaylistPickerResult { IsConfirmed = false });
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(new PlaylistPickerResult { IsConfirmed = false });
    }
}
