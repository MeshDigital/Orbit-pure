using System;
using System.ComponentModel;
using System.Threading.Tasks;
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
    Searching,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One track row in the Download Center. Wraps UnifiedTrackViewModel into a stable, cell-sized
/// row contract (short status label + tooltip detail, tone for the status dot, which actions
/// apply right now). Tracks the underlying UnifiedTrackViewModel live — an earlier version
/// snapshotted everything at construction, which froze every row.
/// </summary>
public sealed class DownloadRowViewModel : ReactiveObject, IDisposable, IHubRowDisplay
{
    private readonly Func<DownloadRowViewModel, Task>? _onRemove;

    public DownloadRowViewModel(
        UnifiedTrackViewModel track,
        Action<DownloadRowViewModel>? onSelect = null,
        Func<DownloadRowViewModel, Task>? onRemove = null)
    {
        Track = track ?? throw new ArgumentNullException(nameof(track));
        _onRemove = onRemove;

        SelectCommand = ReactiveCommand.Create(() => onSelect?.Invoke(this));
        RemoveCommand = ReactiveCommand.CreateFromTask(() => _onRemove?.Invoke(this) ?? Task.CompletedTask);

        RefreshFromTrack();
        Track.PropertyChanged += OnTrackPropertyChanged;
    }

    public UnifiedTrackViewModel Track { get; }
    public ICommand SelectCommand { get; }

    /// <summary>Cancel (if still in flight) and remove the row from the Download Center. Files stay on disk.</summary>
    public ICommand RemoveCommand { get; }
    public bool CanRemove => _onRemove != null;

    private string _title = string.Empty;
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    private DownloadRowStatus _status;
    public DownloadRowStatus Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private DownloadRowPriority _priority;
    public DownloadRowPriority Priority { get => _priority; private set => this.RaiseAndSetIfChanged(ref _priority, value); }

    private double _progress;
    public double Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }

    private string _statusText = string.Empty;
    /// <summary>The engine's own (often long) status line. Shown in the details panel, not in the row.</summary>
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private string _statusLabel = string.Empty;
    /// <summary>2–3 word status for the row cell ("Downloading", "No results", "Peer queue #4").</summary>
    public string StatusLabel { get => _statusLabel; private set => this.RaiseAndSetIfChanged(ref _statusLabel, value); }

    private string _statusDetail = string.Empty;
    /// <summary>Full human explanation for the status-cell tooltip.</summary>
    public string StatusDetail { get => _statusDetail; private set => this.RaiseAndSetIfChanged(ref _statusDetail, value); }

    private DownloadStatusTone _tone;
    public DownloadStatusTone Tone
    {
        get => _tone;
        private set
        {
            if (_tone == value) return;
            this.RaiseAndSetIfChanged(ref _tone, value);
            this.RaisePropertyChanged(nameof(IsToneAccent));
            this.RaisePropertyChanged(nameof(IsToneInfo));
            this.RaisePropertyChanged(nameof(IsToneSuccess));
            this.RaisePropertyChanged(nameof(IsToneWarning));
            this.RaisePropertyChanged(nameof(IsToneError));
        }
    }
    public bool IsToneAccent => Tone == DownloadStatusTone.Accent;
    public bool IsToneInfo => Tone == DownloadStatusTone.Info;
    public bool IsToneSuccess => Tone == DownloadStatusTone.Success;
    public bool IsToneWarning => Tone == DownloadStatusTone.Warning;
    public bool IsToneError => Tone == DownloadStatusTone.Error;

    private DateTime _lastUpdatedUtc = DateTime.UtcNow;
    public DateTime LastUpdatedUtc { get => _lastUpdatedUtc; private set => this.RaiseAndSetIfChanged(ref _lastUpdatedUtc, value); }

    private string _primaryActionLabel = string.Empty;
    public string PrimaryActionLabel { get => _primaryActionLabel; private set => this.RaiseAndSetIfChanged(ref _primaryActionLabel, value); }

    private ICommand? _primaryAction;
    public ICommand? PrimaryAction { get => _primaryAction; private set => this.RaiseAndSetIfChanged(ref _primaryAction, value); }

    private bool _isProgressVisible;
    public bool IsProgressVisible { get => _isProgressVisible; private set => this.RaiseAndSetIfChanged(ref _isProgressVisible, value); }

    private bool _isSpeedVisible;
    public bool IsSpeedVisible { get => _isSpeedVisible; private set => this.RaiseAndSetIfChanged(ref _isSpeedVisible, value); }

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

    private string? _sizeDisplay;
    /// <summary>
    /// File size for completed rows. Lazy: the first read stats the file once, so only rows that
    /// are actually realised on screen pay the disk hit (not every hydrated row at startup).
    /// </summary>
    public string SizeDisplay
    {
        get
        {
            if (Status != DownloadRowStatus.Completed) return string.Empty;
            _sizeDisplay ??= Track.FileSizeDisplay;
            return _sizeDisplay == "—" ? string.Empty : _sizeDisplay;
        }
    }

    private bool _isInspected;
    /// <summary>True while this row is open in the details panel (drives the selected-row highlight).</summary>
    public bool IsInspected { get => _isInspected; set => this.RaiseAndSetIfChanged(ref _isInspected, value); }

    // ── Which actions apply right now (drive context-menu item visibility) ──
    private bool _canPause, _canResume, _canStartNow, _canRetry, _canReveal, _canCancel, _canForceDownload;
    public bool CanPause { get => _canPause; private set => this.RaiseAndSetIfChanged(ref _canPause, value); }
    public bool CanResume { get => _canResume; private set => this.RaiseAndSetIfChanged(ref _canResume, value); }
    public bool CanStartNow { get => _canStartNow; private set => this.RaiseAndSetIfChanged(ref _canStartNow, value); }
    public bool CanRetry { get => _canRetry; private set => this.RaiseAndSetIfChanged(ref _canRetry, value); }
    public bool CanReveal { get => _canReveal; private set => this.RaiseAndSetIfChanged(ref _canReveal, value); }
    public bool CanCancel { get => _canCancel; private set => this.RaiseAndSetIfChanged(ref _canCancel, value); }
    public bool CanForceDownload { get => _canForceDownload; private set => this.RaiseAndSetIfChanged(ref _canForceDownload, value); }

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
                RefreshStatusLabel();
                break;
            case nameof(UnifiedTrackViewModel.FailureEnum):
            case nameof(UnifiedTrackViewModel.FailureReason):
            case nameof(UnifiedTrackViewModel.RemoteQueuePosition):
            case nameof(UnifiedTrackViewModel.HasRejectionDetails):
                RefreshStatusLabel();
                break;
            case nameof(UnifiedTrackViewModel.DownloadSpeed):
            case nameof(UnifiedTrackViewModel.SpeedDisplay):
                SpeedSummary = Track.SpeedDisplay;
                break;
            case nameof(UnifiedTrackViewModel.PeerName):
                PeerSummary = BuildPeerSummary(Track);
                break;
            case nameof(UnifiedTrackViewModel.SearchAttemptCount):
                RetrySummary = BuildRetrySummary(Track);
                break;
            case nameof(UnifiedTrackViewModel.DetailedSearchStatus):
                DiagnosticsSummary = BuildDiagnosticsSummary(Track);
                RefreshStatusLabel();
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
        PeerSummary = BuildPeerSummary(Track);
        RetrySummary = BuildRetrySummary(Track);
        DiagnosticsSummary = BuildDiagnosticsSummary(Track);
    }

    private void ApplyStatus(DownloadRowStatus status)
    {
        Status = status;
        Priority = MapPriority(status);
        PrimaryActionLabel = ResolvePrimaryActionLabel(status);
        PrimaryAction = ResolvePrimaryAction(Track, status);
        IsProgressVisible = status is DownloadRowStatus.Downloading or DownloadRowStatus.Verifying;
        IsSpeedVisible = status == DownloadRowStatus.Downloading;

        var state = Track.State;
        CanPause = status is DownloadRowStatus.Searching or DownloadRowStatus.Downloading or DownloadRowStatus.Verifying
                   || state is PlaylistTrackState.Queued or PlaylistTrackState.WaitingForConnection;
        CanResume = state == PlaylistTrackState.Paused;
        CanStartNow = state is PlaylistTrackState.Pending or PlaylistTrackState.Stalled or PlaylistTrackState.Paused;
        CanRetry = status is DownloadRowStatus.Failed or DownloadRowStatus.Cancelled;
        CanForceDownload = state is PlaylistTrackState.Failed or PlaylistTrackState.Cancelled;
        CanReveal = status == DownloadRowStatus.Completed;
        CanCancel = status is not (DownloadRowStatus.Completed or DownloadRowStatus.Failed or DownloadRowStatus.Cancelled);

        // File path resolves when the download lands — refresh alongside status flips.
        FilePathSummary = string.IsNullOrWhiteSpace(Track.Model.ResolvedFilePath)
            ? "Not resolved yet"
            : Track.Model.ResolvedFilePath;
        _sizeDisplay = null;
        this.RaisePropertyChanged(nameof(SizeDisplay));

        RefreshStatusLabel();
    }

    private void RefreshStatusLabel()
    {
        var (label, detail, tone) = BuildStatusLabel(Track, Status);
        StatusLabel = label;
        StatusDetail = detail;
        Tone = tone;
    }

    /// <summary>Short label + tooltip detail + dot tone for a row. Pure, so it is unit-tested directly.</summary>
    public static (string Label, string Detail, DownloadStatusTone Tone) BuildStatusLabel(UnifiedTrackViewModel track, DownloadRowStatus status)
    {
        switch (status)
        {
            case DownloadRowStatus.Searching:
                return ("Searching", track.StatusText, DownloadStatusTone.Info);
            case DownloadRowStatus.Downloading:
                return ("Downloading", track.StatusText, DownloadStatusTone.Accent);
            case DownloadRowStatus.Verifying:
                return ("Verifying", "Checking the downloaded file", DownloadStatusTone.Accent);
            case DownloadRowStatus.Completed:
                return ("Done", track.FilePathOrEmpty(), DownloadStatusTone.Success);
            case DownloadRowStatus.Cancelled:
                return ("Cancelled", "Cancelled. Retry to search again.", DownloadStatusTone.Neutral);
            case DownloadRowStatus.Failed:
                if (track.State == PlaylistTrackState.Stalled)
                {
                    return ("Stalled", track.StatusText, DownloadStatusTone.Warning);
                }

                var label = track.FailureEnum.ToShortLabel(track.HasRejectionDetails);
                var parts = new System.Collections.Generic.List<string> { track.FailureDisplayMessage };
                if (!string.IsNullOrWhiteSpace(track.FailureActionSuggestion)) parts.Add(track.FailureActionSuggestion);
                if (!string.IsNullOrWhiteSpace(track.FailureReason) && track.FailureReason != track.FailureDisplayMessage) parts.Add(track.FailureReason!);
                return (label, string.Join("\n", parts), DownloadStatusTone.Error);
            default:
                return track.State switch
                {
                    PlaylistTrackState.Paused => ("Paused", "Paused. Resume to put it back in the queue.", DownloadStatusTone.Warning),
                    PlaylistTrackState.WaitingForConnection => ("Connecting", "Waiting for the peer to connect", DownloadStatusTone.Neutral),
                    PlaylistTrackState.Queued => (track.RemoteQueuePosition > 0 ? $"Peer queue #{track.RemoteQueuePosition}" : "In peer queue",
                        track.StatusText, DownloadStatusTone.Neutral),
                    _ => ("Queued", "Waiting for a free download slot", DownloadStatusTone.Neutral),
                };
        }
    }

    private static string BuildPeerSummary(UnifiedTrackViewModel track)
        => string.IsNullOrWhiteSpace(track.PeerName) ? string.Empty : track.PeerName;

    private static string BuildRetrySummary(UnifiedTrackViewModel track)
        => track.SearchAttemptCount <= 0 ? "No retries yet" : $"{track.SearchAttemptCount} attempt(s)";

    private static string BuildDiagnosticsSummary(UnifiedTrackViewModel track)
        => string.IsNullOrWhiteSpace(track.DetailedSearchStatus) ? track.TechnicalSummary : track.DetailedSearchStatus;

    private static string BuildTitle(UnifiedTrackViewModel track)
    {
        if (!string.IsNullOrWhiteSpace(track.ArtistName) && !string.IsNullOrWhiteSpace(track.TrackTitle))
        {
            return $"{track.ArtistName} – {track.TrackTitle}";
        }

        return track.TrackTitle;
    }

    public static DownloadRowStatus MapStatus(PlaylistTrackState state)
    {
        return state switch
        {
            PlaylistTrackState.Pending => DownloadRowStatus.Queued,
            PlaylistTrackState.Searching => DownloadRowStatus.Searching,
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

    private static string ResolvePrimaryActionLabel(DownloadRowStatus status)
    {
        return status switch
        {
            DownloadRowStatus.Completed => "Reveal",
            DownloadRowStatus.Failed => "Retry",
            DownloadRowStatus.Cancelled => "Retry",
            DownloadRowStatus.Searching => "Pause",
            DownloadRowStatus.Downloading => "Pause",
            DownloadRowStatus.Verifying => "Pause",
            _ => "Start now",
        };
    }

    private static ICommand ResolvePrimaryAction(UnifiedTrackViewModel track, DownloadRowStatus status)
    {
        return status switch
        {
            DownloadRowStatus.Completed => track.RevealFileCommand,
            DownloadRowStatus.Failed => track.RetryCommand,
            DownloadRowStatus.Cancelled => track.RetryCommand,
            DownloadRowStatus.Searching => track.PauseCommand,
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

internal static class UnifiedTrackRowExtensions
{
    public static string FilePathOrEmpty(this UnifiedTrackViewModel track)
        => track.Model.ResolvedFilePath ?? string.Empty;
}
