using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Downloads;

namespace SLSKDONET.Services.Shell;

/// <summary>
/// Watches the real playback and download sources (PlayerViewModel, the Download Center and the
/// DownloadManager engine) and publishes a throttled <see cref="ShellState"/> snapshot for the tray
/// icon and the taskbar button. Also owns the shell's transport/engine actions so the tray menu and
/// the thumbnail toolbar execute exactly the commands the in-app player bar and the Download Center
/// header execute.
///
/// Throttling: a state change applies immediately when the last update was at least
/// <see cref="MinUpdateInterval"/> ago, otherwise it is coalesced into the next timer tick — so a
/// burst of PropertyChanged events (or live download progress) costs at most two updates a second.
/// Construction does no work and cannot throw; nothing is subscribed until <see cref="Start"/>.
/// </summary>
public sealed class ShellStateMonitor : IDisposable
{
    public static readonly TimeSpan MinUpdateInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly PlayerViewModel _player;
    private readonly DownloadCenterViewModel _downloads;
    private readonly DownloadManager _engine;
    private readonly ILogger<ShellStateMonitor> _logger;
    private readonly ShellUpdateThrottle _throttle = new(MinUpdateInterval);

    private DispatcherTimer? _timer;
    private Func<bool> _isWindowShown = () => true;
    private volatile bool _dirty = true;
    private bool _started;
    private bool _captureFailureLogged;

    public ShellStateMonitor(
        PlayerViewModel player,
        DownloadCenterViewModel downloads,
        DownloadManager engine,
        ILogger<ShellStateMonitor> logger)
    {
        _player = player;
        _downloads = downloads;
        _engine = engine;
        _logger = logger;
    }

    public ShellState Current { get; private set; } = ShellState.Initial;

    /// <summary>Raised on the UI thread, only when the snapshot actually changed.</summary>
    public event Action<ShellState>? StateChanged;

    /// <summary>UI thread. <paramref name="isWindowShown"/> reports whether the main window is on screen (visible and not minimised).</summary>
    public void Start(Func<bool> isWindowShown)
    {
        if (_started) return;
        _started = true;
        _isWindowShown = isWindowShown;

        _player.PropertyChanged += OnPlayerPropertyChanged;
        _player.Queue.CollectionChanged += OnQueueChanged;
        _downloads.PropertyChanged += OnDownloadsPropertyChanged;
        _engine.PropertyChanged += OnEnginePropertyChanged;

        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, OnTick);
        _timer.Start();

        Flush();
    }

    /// <summary>Marks the state stale and refreshes now if the throttle allows (else on the next tick).</summary>
    public void Invalidate()
    {
        _dirty = true;
        if (_started && Dispatcher.UIThread.CheckAccess() && _throttle.TryEnter(DateTime.UtcNow))
            Flush();
    }

    // ── Actions (the same commands the player bar / Download Center header bind to) ──────────

    public void TogglePlayPause() => Run(_player.TogglePlayPauseCommand, "play/pause");
    public void NextTrack() => Run(_player.NextTrackCommand, "next track");
    public void PreviousTrack() => Run(_player.PreviousTrackCommand, "previous track");

    /// <summary>Start (engine stopped) / Resume (paused) / Pause (running) — the Download Center header's one engine toggle.</summary>
    public void ToggleDownloads() =>
        Run(_downloads.IsEngineRunning ? _downloads.ToggleEnginePauseCommand : _downloads.StartEngineCommand, "download engine toggle");

    private void Run(ICommand command, string what)
    {
        try
        {
            if (command.CanExecute(null))
                command.Execute(null);
            else
                _logger.LogDebug("[Shell] {Action} ignored: command cannot execute right now", what);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Shell] {Action} from tray/taskbar failed", what);
        }
        Invalidate();
    }

    // ── Change tracking ────────────────────────────────────────────────────────────────

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsPlaying):
            case nameof(PlayerViewModel.TrackTitle):
            case nameof(PlayerViewModel.TrackArtist):
            case nameof(PlayerViewModel.CurrentTrack):
            case nameof(PlayerViewModel.HasCurrentTrack):
            case nameof(PlayerViewModel.CurrentQueueIndex):
            case nameof(PlayerViewModel.RepeatMode):
            case null:
                MarkDirty();
                break;
        }
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => MarkDirty();

    private void OnDownloadsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DownloadCenterViewModel.DownloadingCount):
            case nameof(DownloadCenterViewModel.SearchingCount):
            case nameof(DownloadCenterViewModel.QueuedCount):
            case nameof(DownloadCenterViewModel.TotalFailedCount):
            case nameof(DownloadCenterViewModel.IsEngineRunning):
            case nameof(DownloadCenterViewModel.IsEnginePaused):
                MarkDirty();
                break;
        }
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadManager.IsPaused) or nameof(DownloadManager.IsRunning))
            MarkDirty();
    }

    // PropertyChanged can arrive on any thread; only the UI thread reads the view models.
    private void MarkDirty()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Invalidate();
        else
            _dirty = true;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Live transfer progress has no change event worth listening to (it fires per chunk), so
        // poll it while something is moving; otherwise only refresh when something changed.
        var d = Current.Downloads;
        var moving = d.Downloading > 0 || d.Searching > 0;
        if ((_dirty || moving) && _throttle.TryEnter(DateTime.UtcNow))
            Flush();
    }

    private void Flush()
    {
        _dirty = false;
        ShellState next;
        try
        {
            next = Capture();
            _captureFailureLogged = false;
        }
        catch (Exception ex)
        {
            // Transient races over the download collections are possible; log once per streak.
            if (!_captureFailureLogged)
            {
                _captureFailureLogged = true;
                _logger.LogWarning(ex, "[Shell] Capturing tray/taskbar state failed; keeping the previous state");
            }
            return;
        }

        if (next == Current) return;
        Current = next;

        try
        {
            StateChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Shell] Applying tray/taskbar state failed");
        }
    }

    private ShellState Capture()
    {
        var hasTrack = _player.IsPlaying
                       || _player.HasCurrentTrack
                       || !string.IsNullOrWhiteSpace(_player.CurrentFilePath);

        var playback = new ShellPlaybackState(
            HasTrack: hasTrack,
            IsPlaying: _player.IsPlaying,
            Artist: hasTrack ? _player.TrackArtist : null,
            Title: hasTrack ? _player.TrackTitle : null,
            // TogglePlayPause has no CanExecute of its own: it can pause, resume a loaded track,
            // or start the queue — and nothing else.
            CanPlayPause: hasTrack || !_player.IsQueueEmpty,
            CanNext: _player.NextTrackCommand.CanExecute(null),
            CanPrevious: _player.PreviousTrackCommand.CanExecute(null));

        return new ShellState(playback, _downloads.GetShellDownloadState(), _isWindowShown());
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _timer?.Stop();
        _timer = null;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _player.Queue.CollectionChanged -= OnQueueChanged;
        _downloads.PropertyChanged -= OnDownloadsPropertyChanged;
        _engine.PropertyChanged -= OnEnginePropertyChanged;
    }
}
