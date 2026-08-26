using System.Windows.Input;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Minimal display contract shared by <see cref="DownloadRowViewModel"/> (live session rows) and
/// <see cref="UnfindableTrackViewModel"/> (persisted OnHold rows) so both can render through one
/// row template in the Download Center's Attention tab — the merge point for "why can't ORBIT
/// find this track," previously split across two unrelated, independently-filtered UI sections
/// (the runtime Failed/Stalled rows and the DB-driven Unfindable Tracks panel).
/// </summary>
public interface IHubRowDisplay
{
    string Title { get; }
    string StatusBadgeText { get; }
    string StatusText { get; }
    string StatusAccent { get; }
    ICommand? PrimaryAction { get; }
    string PrimaryActionLabel { get; }
    double Progress { get; }
    bool IsProgressVisible { get; }
    string PeerSummary { get; }
    string SpeedSummary { get; }
    ICommand SelectCommand { get; }
}
