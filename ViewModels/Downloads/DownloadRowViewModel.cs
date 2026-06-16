using System;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Downloads;

public enum DownloadRowPriority
{
    Active,
    Attention,
    Completed,
}

public enum DownloadRowStatus
{
    Queued,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Slice 1 projection model for Download Center v2 rows.
/// Wraps UnifiedTrackViewModel into a stable, UI-facing row contract.
/// </summary>
public sealed class DownloadRowViewModel : ReactiveObject
{
    public DownloadRowViewModel(UnifiedTrackViewModel track, Action<DownloadRowViewModel>? onSelect = null)
    {
        Track = track ?? throw new ArgumentNullException(nameof(track));

        Title = BuildTitle(track);
        Status = MapStatus(track.State);
        Priority = MapPriority(Status);
        Progress = NormalizeProgress(track.Progress);
        StatusText = track.StatusText;
        LastUpdatedUtc = DateTime.UtcNow;
        StatusBadgeText = BuildStatusBadgeText(Status);
        StatusAccent = BuildStatusAccent(Status);
        PrimaryActionLabel = ResolvePrimaryActionLabel(Status);
        IsProgressVisible = Status is DownloadRowStatus.Queued or DownloadRowStatus.Downloading or DownloadRowStatus.Verifying;

        PeerSummary = string.IsNullOrWhiteSpace(track.PeerName) ? "Unknown peer" : track.PeerName;
        RetrySummary = track.SearchAttemptCount <= 0
            ? "No retries yet"
            : $"{track.SearchAttemptCount} attempt(s)";
        DiagnosticsSummary = string.IsNullOrWhiteSpace(track.DetailedSearchStatus)
            ? track.TechnicalSummary
            : track.DetailedSearchStatus;
        FilePathSummary = string.IsNullOrWhiteSpace(track.Model.ResolvedFilePath)
            ? "Not resolved yet"
            : track.Model.ResolvedFilePath;

        PrimaryAction = ResolvePrimaryAction(track, Status);
        SelectCommand = ReactiveCommand.Create(() => onSelect?.Invoke(this));
    }

    public UnifiedTrackViewModel Track { get; }
    public string Title { get; }
    public DownloadRowStatus Status { get; }
    public DownloadRowPriority Priority { get; }
    public double Progress { get; }
    public string StatusText { get; }
    public string StatusBadgeText { get; }
    public string StatusAccent { get; }
    public DateTime LastUpdatedUtc { get; }
    public string PrimaryActionLabel { get; }
    public bool IsProgressVisible { get; }
    public string PeerSummary { get; }
    public string RetrySummary { get; }
    public string DiagnosticsSummary { get; }
    public string FilePathSummary { get; }
    public ICommand PrimaryAction { get; }
    public ICommand SelectCommand { get; }

    private static string BuildTitle(UnifiedTrackViewModel track)
    {
        if (!string.IsNullOrWhiteSpace(track.ArtistName) && !string.IsNullOrWhiteSpace(track.TrackTitle))
        {
            return $"{track.ArtistName} - {track.TrackTitle}";
        }

        return track.TrackTitle;
    }

    private static DownloadRowStatus MapStatus(PlaylistTrackState state)
    {
        return state switch
        {
            PlaylistTrackState.Pending => DownloadRowStatus.Queued,
            PlaylistTrackState.Searching => DownloadRowStatus.Queued,
            PlaylistTrackState.Queued => DownloadRowStatus.Queued,
            PlaylistTrackState.WaitingForConnection => DownloadRowStatus.Queued,
            PlaylistTrackState.Paused => DownloadRowStatus.Queued,
            PlaylistTrackState.Downloading => DownloadRowStatus.Downloading,
            PlaylistTrackState.Converting => DownloadRowStatus.Verifying,
            PlaylistTrackState.Completed => DownloadRowStatus.Completed,
            PlaylistTrackState.Failed => DownloadRowStatus.Failed,
            PlaylistTrackState.Stalled => DownloadRowStatus.Failed,
            PlaylistTrackState.Cancelled => DownloadRowStatus.Cancelled,
            _ => DownloadRowStatus.Queued,
        };
    }

    private static DownloadRowPriority MapPriority(DownloadRowStatus status)
    {
        return status switch
        {
            DownloadRowStatus.Completed => DownloadRowPriority.Completed,
            DownloadRowStatus.Failed => DownloadRowPriority.Attention,
            DownloadRowStatus.Cancelled => DownloadRowPriority.Attention,
            _ => DownloadRowPriority.Active,
        };
    }

    private static double NormalizeProgress(double progress)
    {
        if (progress <= 1.0)
        {
            return Math.Clamp(progress, 0.0, 1.0);
        }

        return Math.Clamp(progress / 100.0, 0.0, 1.0);
    }

    private static string BuildStatusBadgeText(DownloadRowStatus status)
    {
        return status switch
        {
            DownloadRowStatus.Downloading => "DOWNLOADING",
            DownloadRowStatus.Verifying => "VERIFYING",
            DownloadRowStatus.Completed => "COMPLETED",
            DownloadRowStatus.Failed => "ATTENTION",
            DownloadRowStatus.Cancelled => "CANCELLED",
            _ => "QUEUED",
        };
    }

    private static string BuildStatusAccent(DownloadRowStatus status)
    {
        return status switch
        {
            DownloadRowStatus.Downloading => "#00BCD4",
            DownloadRowStatus.Verifying => "#4DD0E1",
            DownloadRowStatus.Completed => "#30C97A",
            DownloadRowStatus.Failed => "#FF6B6B",
            DownloadRowStatus.Cancelled => "#E57373",
            _ => "#FFB300",
        };
    }

    private static string ResolvePrimaryActionLabel(DownloadRowStatus status)
    {
        return status switch
        {
            DownloadRowStatus.Completed => "Reveal",
            DownloadRowStatus.Failed => "Retry",
            DownloadRowStatus.Cancelled => "Retry",
            DownloadRowStatus.Downloading => "Pause",
            DownloadRowStatus.Verifying => "Pause",
            _ => "Start",
        };
    }

    private static ICommand ResolvePrimaryAction(UnifiedTrackViewModel track, DownloadRowStatus status)
    {
        return status switch
        {
            DownloadRowStatus.Completed => track.RevealFileCommand,
            DownloadRowStatus.Failed => track.RetryCommand,
            DownloadRowStatus.Cancelled => track.RetryCommand,
            DownloadRowStatus.Downloading => track.PauseCommand,
            DownloadRowStatus.Verifying => track.PauseCommand,
            _ => track.ForceStartCommand,
        };
    }
}
