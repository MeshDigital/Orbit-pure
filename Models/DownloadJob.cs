using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.Models;

/// <summary>
/// Represents a download job for a track.
/// </summary>
public class DownloadJob : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Track Track { get; set; } = null!;

    /// <summary>
    /// Per-job cancellation token to allow UI-triggered cancellation.
    /// Resettable for resume/requeue scenarios.
    /// </summary>
    private CancellationTokenSource _cancellationTokenSource = new();
    public CancellationTokenSource CancellationTokenSource => _cancellationTokenSource;

    private DownloadState _state = DownloadState.Pending;
    public DownloadState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    /// <summary>
    /// Alias for State to align with UI naming.
    /// </summary>
    public DownloadStatus Status
    {
        get => (DownloadStatus)State;
        set => State = (DownloadState)value;
    }

    private double _progress; // 0.0 to 1.0
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    private long? _bytesDownloaded;
    public long? BytesDownloaded
    {
        get => _bytesDownloaded;
        set => SetProperty(ref _bytesDownloaded, value);
    }

    private double? _speedBps; // Speed in Bytes per second
    public double? SpeedBps
    {
        get => _speedBps;
        set => SetProperty(ref _speedBps, value);
    }


    public string? OutputPath { get; set; }

    /// <summary>
    /// Final destination path for easy access from UI.
    /// </summary>
    public string? DestinationPath { get; set; }

    /// <summary>
    /// Name of the originating source (e.g., playlist name).
    /// </summary>
    public string? SourceTitle { get; set; }

    public ICommand OpenFolderCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand StartCommand { get; }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    private DateTime? _startedAt;
    public DateTime? StartedAt
    {
        get => _startedAt;
        set => SetProperty(ref _startedAt, value);
    }

    private DateTime? _completedAt;
    public DateTime? CompletedAt
    {
        get => _completedAt;
        set => SetProperty(ref _completedAt, value);
    }

    public TimeSpan? ElapsedTime =>
        (CompletedAt ?? DateTime.UtcNow) - (StartedAt ?? CreatedAt);

    public double? SizeInMb => Track.Size.HasValue ? (double)Track.Size.Value / 1024 / 1024 : null;

    /// <summary>
    /// Tracks the number of retry attempts made for this download.
    /// </summary>
    private int _retryCount;
    public int RetryCount
    {
        get => _retryCount;
        set => SetProperty(ref _retryCount, value);
    }

    /// <summary>
    /// Timestamp of the last download attempt.
    /// </summary>
    private DateTime? _lastAttemptTime;
    public DateTime? LastAttemptTime
    {
        get => _lastAttemptTime;
        set => SetProperty(ref _lastAttemptTime, value);
    }

    /// <summary>
    /// Resets the cancellation token so a cancelled job can be resumed/requeued.
    /// </summary>
    public void ResetCancellationToken()
    {
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public DownloadManager? DownloadManager { get; set; }

    public DownloadJob(DownloadManager? downloadManager = null)
    {
        DownloadManager = downloadManager;
        OpenFolderCommand = new RelayCommand(OpenContainingFolder);
        StopCommand = new AsyncRelayCommand(CancelDownloadAsync, CanStop);
        StartCommand = new AsyncRelayCommand(ResumeDownloadAsync, CanStart);
    }

    // Parameterless constructor for serialization
    public DownloadJob() : this(null) { }

    private void OpenContainingFolder()
    {
        var directory = string.IsNullOrWhiteSpace(DestinationPath)
            ? (string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetDirectoryName(OutputPath))
            : Path.GetDirectoryName(DestinationPath);

        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
                Verb = "open"
            });
        }
    }

    private Task CancelDownloadAsync()
    {
        if (!CancellationTokenSource.IsCancellationRequested)
        {
            CancellationTokenSource.Cancel();
            Status = DownloadStatus.Cancelled;
        }
        return Task.CompletedTask;
    }

    private Task ResumeDownloadAsync()
    {
        // Directly ask the DownloadManager to requeue this job.
        // DownloadManager?.RequeueJob(this); // Deprecated
        return Task.CompletedTask;
    }

    private bool CanStart()
    {
        return State == DownloadState.Cancelled || State == DownloadState.Failed;
    }

    private bool CanStop()
    {
        return State == DownloadState.Downloading || State == DownloadState.Retrying || State == DownloadState.Searching || State == DownloadState.Pending;
    }

    public override string ToString()
    {
        return $"[{State}] {Track.Artist} - {Track.Title} ({Progress:P})";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// Represents the state of a download.
/// </summary>
public enum DownloadState
{
    Pending,
    Searching,
    Downloading,
    Retrying,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Alias enum to align with UI wording.
/// </summary>
public enum DownloadStatus
{
    Pending,
    Searching,
    Downloading,
    Completed,
    Failed,
    Cancelled
}
