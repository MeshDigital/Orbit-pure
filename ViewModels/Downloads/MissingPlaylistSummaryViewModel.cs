using System;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// One playlist with tracks still missing, shown in the Download Center's
/// "Playlists with missing tracks" panel so downloads can be started from here
/// (previously only reachable via the Library page's per-playlist context menu).
/// </summary>
public sealed class MissingPlaylistSummaryViewModel : ReactiveObject
{
    public MissingPlaylistSummaryViewModel(
        Guid playlistId,
        string title,
        int missingCount,
        int totalCount,
        Func<MissingPlaylistSummaryViewModel, PlaylistPriority, System.Threading.Tasks.Task> onStart)
    {
        PlaylistId = playlistId;
        Title = title;
        MissingCount = missingCount;
        TotalCount = totalCount;

        StartCommand = ReactiveCommand.CreateFromTask(() => onStart(this, PlaylistPriority.Normal));
        StartHighPriorityCommand = ReactiveCommand.CreateFromTask(() => onStart(this, PlaylistPriority.High));
    }

    public Guid PlaylistId { get; }
    public string Title { get; }
    public int MissingCount { get; }
    public int TotalCount { get; }

    public string CountDisplay => $"{MissingCount} of {TotalCount} missing";

    private bool _isQueued;
    /// <summary>True when this playlist already has tracks in the active download queue.</summary>
    public bool IsQueued
    {
        get => _isQueued;
        set => this.RaiseAndSetIfChanged(ref _isQueued, value);
    }

    public ICommand StartCommand { get; }
    public ICommand StartHighPriorityCommand { get; }
}
