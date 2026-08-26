using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Linq;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Views.Avalonia.Dialogs;

public partial class CombinePlaylistsDialog : Window
{
    public CombinePlaylistsDialog()
    {
        InitializeComponent();
    }

    public CombinePlaylistsDialog(CombinePlaylistsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Combine_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CombinePlaylistsViewModel vm && vm.CanConfirm)
        {
            var result = new CombinePlaylistsResult
            {
                IsConfirmed = true,
                SelectedPlaylists = vm.Playlists.Where(p => p.IsSelected).Select(p => p.Playlist).ToList(),
                NewPlaylistName = vm.NewPlaylistName,
                SkipDuplicateTracks = vm.SkipDuplicateTracks
            };
            Close(result);
        }
        else
        {
            Close(new CombinePlaylistsResult { IsConfirmed = false });
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(new CombinePlaylistsResult { IsConfirmed = false });
    }
}
