using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// One track that <see cref="Services.AutoDownload.GhostAcquisitionOrchestrator"/> gave up
/// searching for (TrackStatus.OnHold, after 3 failed background search attempts), shown in the
/// Download Center's "Unfindable Tracks" panel alongside its source playlist so the user can see
/// what got skipped and optionally retry it.
/// </summary>
public sealed class UnfindableTrackViewModel : ReactiveObject, IHubRowDisplay
{
    public UnfindableTrackViewModel(
        Guid playlistTrackId,
        Guid playlistId,
        string artist,
        string title,
        string playlistTitle,
        int searchRetryCount,
        Func<UnfindableTrackViewModel, Task> onRetry)
    {
        PlaylistTrackId = playlistTrackId;
        PlaylistId = playlistId;
        Artist = artist;
        Title = title;
        PlaylistTitle = playlistTitle;
        SearchRetryCount = searchRetryCount;

        RetryCommand = ReactiveCommand.CreateFromTask(() => onRetry(this));
    }

    public Guid PlaylistTrackId { get; }
    public Guid PlaylistId { get; }
    public string Artist { get; }
    public string Title { get; }
    public string PlaylistTitle { get; }
    public int SearchRetryCount { get; }

    public string DisplayName => $"{Artist} — {Title}";

    public ICommand RetryCommand { get; }

    // ── IHubRowDisplay — lets this render through the same Attention-tab row surface as
    // DownloadRowViewModel. Title is explicit-interface since this class's own `Title` already
    // means "track title only," while the shared row contract expects the full display title.
    string IHubRowDisplay.Title => DisplayName;
    public string StatusBadgeText => "ON HOLD";
    public string StatusText => $"{SearchRetryCount} attempt(s) — background search gave up in {PlaylistTitle}";
    public string StatusAccent => "#9AA0A6";
    public ICommand? PrimaryAction => RetryCommand;
    public string PrimaryActionLabel => "↻ Retry";
    public double Progress => 0;
    public bool IsProgressVisible => false;
    public string PeerSummary => string.Empty;
    public string SpeedSummary => string.Empty;
    // No forensic session detail exists for a track that was never actively downloaded this
    // session — the inline Retry action above is sufficient, so selecting the row is a no-op
    // rather than opening a detail panel with nothing meaningful to show.
    public ICommand SelectCommand { get; } = ReactiveCommand.Create(() => { });
}
