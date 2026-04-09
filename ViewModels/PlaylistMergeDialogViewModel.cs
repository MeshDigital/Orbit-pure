using System.Collections.ObjectModel;
using ReactiveUI;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for the Playlist Merge dialog — allows the user to combine two playlists
/// with configurable conflict-resolution (keep first, keep last, keep both).
/// </summary>
public sealed class PlaylistMergeDialogViewModel : ReactiveObject
{
    public ObservableCollection<string> AvailablePlaylists { get; } = new();

    private string? _sourcePlaylist;
    public string? SourcePlaylist
    {
        get => _sourcePlaylist;
        set => this.RaiseAndSetIfChanged(ref _sourcePlaylist, value);
    }

    private string? _targetPlaylist;
    public string? TargetPlaylist
    {
        get => _targetPlaylist;
        set => this.RaiseAndSetIfChanged(ref _targetPlaylist, value);
    }

    private string _conflictResolution = "KeepBoth";
    /// <summary>How to handle duplicate tracks. Options: KeepFirst, KeepLast, KeepBoth.</summary>
    public string ConflictResolution
    {
        get => _conflictResolution;
        set => this.RaiseAndSetIfChanged(ref _conflictResolution, value);
    }

    private string? _mergedPlaylistName;
    public string? MergedPlaylistName
    {
        get => _mergedPlaylistName;
        set => this.RaiseAndSetIfChanged(ref _mergedPlaylistName, value);
    }

    public bool CanMerge =>
        !string.IsNullOrWhiteSpace(SourcePlaylist) &&
        !string.IsNullOrWhiteSpace(TargetPlaylist) &&
        SourcePlaylist != TargetPlaylist;
}
