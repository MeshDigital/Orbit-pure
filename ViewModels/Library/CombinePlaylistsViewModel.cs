using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Library;

public class CombinePlaylistsResult
{
    public bool IsConfirmed { get; set; }
    public List<PlaylistJob> SelectedPlaylists { get; set; } = new();
    public string NewPlaylistName { get; set; } = string.Empty;
    public bool SkipDuplicateTracks { get; set; } = true;
}

/// <summary>Checkbox-selectable wrapper around a playlist for the Combine Playlists dialog.</summary>
public sealed class SelectablePlaylistItem : ReactiveObject
{
    private bool _isSelected;

    public SelectablePlaylistItem(PlaylistJob playlist, bool isSelected)
    {
        Playlist = playlist;
        _isSelected = isSelected;
    }

    public PlaylistJob Playlist { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

public sealed class CombinePlaylistsViewModel : ReactiveObject
{
    private string _newPlaylistName = string.Empty;
    private bool _skipDuplicateTracks = true;

    public ObservableCollection<SelectablePlaylistItem> Playlists { get; } = new();

    public string NewPlaylistName
    {
        get => _newPlaylistName;
        set
        {
            this.RaiseAndSetIfChanged(ref _newPlaylistName, value);
            this.RaisePropertyChanged(nameof(CanConfirm));
        }
    }

    public bool SkipDuplicateTracks
    {
        get => _skipDuplicateTracks;
        set => this.RaiseAndSetIfChanged(ref _skipDuplicateTracks, value);
    }

    public bool CanConfirm => Playlists.Count(p => p.IsSelected) >= 2 && !string.IsNullOrWhiteSpace(NewPlaylistName);

    public CombinePlaylistsViewModel(IEnumerable<PlaylistJob> available, IReadOnlyList<PlaylistJob>? preSelected = null)
    {
        var preSelectedIds = (preSelected ?? Enumerable.Empty<PlaylistJob>()).Select(p => p.Id).ToHashSet();

        foreach (var playlist in available)
        {
            var item = new SelectablePlaylistItem(playlist, preSelectedIds.Contains(playlist.Id));
            item.WhenAnyValue(x => x.IsSelected).Subscribe(_ => this.RaisePropertyChanged(nameof(CanConfirm)));
            Playlists.Add(item);
        }

        if (preSelectedIds.Count > 0)
        {
            var names = Playlists.Where(p => p.IsSelected).Select(p => p.Playlist.SourceTitle);
            NewPlaylistName = string.Join(" + ", names);
        }
    }
}
