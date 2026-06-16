using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Library;

public class PlaylistPickerResult
{
    public bool IsConfirmed { get; set; }
    public PlaylistJob? SelectedPlaylist { get; set; }
    public string? NewPlaylistName { get; set; }
}

public sealed class PlaylistPickerViewModel : ReactiveObject
{
    private PlaylistJob? _selectedPlaylist;
    private string _newPlaylistName = string.Empty;

    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    public PlaylistJob? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPlaylist, value);
            if (value != null)
            {
                NewPlaylistName = string.Empty;
            }
            this.RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    public string NewPlaylistName
    {
        get => _newPlaylistName;
        set
        {
            this.RaiseAndSetIfChanged(ref _newPlaylistName, value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                SelectedPlaylist = null;
            }
            this.RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    public bool CanConfirm => SelectedPlaylist != null || !string.IsNullOrWhiteSpace(NewPlaylistName);

    public PlaylistPickerViewModel(IEnumerable<PlaylistJob> playlists)
    {
        foreach (var playlist in playlists)
        {
            Playlists.Add(playlist);
        }
    }
}
