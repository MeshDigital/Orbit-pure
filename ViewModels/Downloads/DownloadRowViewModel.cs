using System;
using System.ComponentModel;
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
/// Tracks the underlying UnifiedTrackViewModel live — the original implementation snapshotted
/// everything at construction, which left every hub row frozen (progress bars stuck, status
/// badges never flipping, rows never migrating between Active/Attention/Completed sections).
/// </summary>
public sealed class DownloadRowViewModel : ReactiveObject, IDisposable
{
    public DownloadRowViewModel(UnifiedTrackViewModel track, Action<DownloadRowViewModel>? onSelect = null)
    {
        Track = track ?? throw new ArgumentNullException(nameof(track));

        SelectCommand = ReactiveCommand.Create(() => onSelect?.Invoke(this));

        RefreshFromTrack();
        Track.PropertyChanged += OnTrackPropertyChanged;
    }

    public UnifiedTrackViewModel Track { get; }
    public ICommand SelectCommand { get; }

    private string _title = string.Empty;
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    private DownloadRowStatus _status;
    public DownloadRowStatus Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private DownloadRowPriority _priority;
    public DownloadRowPriority Priority { get => _priority; private set => this.RaiseAndSetIfChanged(ref _priority, value); }

    private double _progress;
    public double Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }

    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private string _statusBadgeText = string.Empty;
    public string StatusBadgeText { get => _statusBadgeText; private set => this.RaiseAndSetIfChanged(ref _statusBadgeText, value); }

    private string _statusAccent = "#FFB300";
    public string StatusAccent { get => _statusAccent; private set => this.RaiseAndSetIfChanged(ref _statusAccent, value); }

    private DateTime _lastUpdatedUtc = DateTime.UtcNow;
    public DateTime LastUpdatedUtc { get => _lastUpdatedUtc; private set => this.RaiseAndSetIfChanged(ref _lastUpdatedUtc, value); }

    private string _primaryActionLabel = string.Empty;
    public string PrimaryActionLabel { get => _primaryActionLabel; private set => this.RaiseAndSetIfChanged(ref _primaryActionLabel, value); }

    private bool _isProgressVisible;
    public bool IsProgressVisible { get => _isProgressVisible; private set => this.RaiseAndSetIfChanged(ref _isProgressVisible, value); }

    private string _peerSummary = string.Empty;
    public string PeerSummary { get => _peerSummary; private set => this.RaiseAndSetIfChanged(ref _peerSummary, value); }

    private string _retrySummary = string.Empty;
    public string RetrySummary { get => _retrySummary; private set => this.RaiseAndSetIfChanged(ref _retrySummary, value); }

    private string _diagnosticsSummary = string.Empty;
    public string DiagnosticsSummary { get => _diagnosticsSummary; private set => this.RaiseAndSetIfChanged(ref _diagnosticsSummary, value); }

    private string _filePathSummary = string.Empty;
    public string FilePathSummary { get => _filePathSummary; private set => this.RaiseAndSetIfChanged(ref _filePathSummary, value); }

    private string _speedSummary = string.Empty;
    public string SpeedSummary { get => _speedSummary; private set => this.RaiseAndSetIfChanged(ref _speedSummary, value); }

    private ICommand? _primaryAction;
    public ICommand? PrimaryAction { get => _primaryAction; private set => this.RaiseAndSetIfChanged(ref _primaryAction, value); }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UnifiedTrackViewModel.State):
                var newStatus = MapStatus(Track.State);
                if (newStatus != Status)
                {
                    LastUpdatedUtc = DateTime.UtcNow;
                }
                ApplyStatus(newStatus);
                break;
            case nameof(UnifiedTrackViewModel.Progress):
                Progress = NormalizeProgress(Track.Progress);
                break;
            case nameof(UnifiedTrackViewModel.StatusText):
                StatusText = Track.StatusText;
                break;
            case nameof(UnifiedTrackViewModel.DownloadSpeed):
            case nameof(UnifiedTrackViewModel.SpeedDisplay):
                SpeedSummary = Track.SpeedDisplay;
                break;
            case nameof(UnifiedTrackViewModel.PeerName):
                PeerSummary = string.IsNullOrWhiteSpace(Track.PeerName) ? "Unknown peer" : Track.PeerName;
                break;
            case nameof(UnifiedTrackViewModel.SearchAttemptCount):
                RetrySummary = BuildRetrySummary(Track);
                break;
            case nameof(UnifiedTrackViewModel.DetailedSearchStatus):
                DiagnosticsSummary = BuildDiagnosticsSummary(Track);
                break;
        }
    }

    private void RefreshFromTrack()
    {
        Title = BuildTitle(Track);
        ApplyStatus(MapStatus(Track.State));
        Progress = NormalizeProgress(Track.Progress);
        StatusText = Track.StatusText;
        SpeedSummary = Track.SpeedDisplay;
        PeerSummary = string.IsNullOrWhiteSpace(Track.PeerName) ? "Unknown peer" : Track.PeerName;
        RetrySummary = BuildRetrySummary(Track);
        DiagnosticsSummary = BuildDiagnosticsSummary(Track);
        FilePathSummary = string.IsNullOrWhiteSpace(Track.Model.ResolvedFilePath)
            ? "Not resolved yet"
            : Track.Model.ResolvedFilePath;
    }

    private void ApplyStatus(DownloadRowStatus status)
    {
        Status = status;
        Priority = MapPriority(status);
        StatusBadgeText = BuildStatusBadgeText(status);
        StatusAccent = BuildStatusAccent(status);
        PrimaryActionLabel = ResolvePrimaryActionLabel(status);
        PrimaryAction = ResolvePrimaryAction(Track, status);
        IsProgressVisible = status is DownloadRowStatus.Queued or DownloadRowStatus.Downloading or DownloadRowStatus.Verifying;

        // File path resolves when the download lands — refresh alongside status flips.
        FilePathSummary = string.IsNullOrWhiteSpace(Track.Model.ResolvedFilePath)
            ? "Not resolved yet"
            : Track.Model.ResolvedFilePath;
    }

    private static string BuildRetrySummary(UnifiedTrackViewModel track)
        => track.SearchAttemptCount <= 0 ? "No retries yet" : $"{track.SearchAttemptCount} attempt(s)";

    private static string BuildDiagnosticsSummary(UnifiedTrackViewModel track)
        => string.IsNullOrWhiteSpace(track.DetailedSearchStatus) ? track.TechnicalSummary : track.DetailedSearchStatus;

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

    public void Dispose()
    {
        Track.PropertyChanged -= OnTrackPropertyChanged;
    }
}
