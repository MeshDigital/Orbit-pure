using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Media;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2: Represents a grouped collection of downloads (Album or Project).
/// Aggregates progress, speed, and status from underlying tracks.
/// </summary>
public class DownloadGroupViewModel : ReactiveObject, IDisposable
{
    private readonly IDisposable _cleanUp;
    private readonly DownloadManager _downloadManager;
    private readonly ILibraryService _libraryService;
    private readonly INotificationService? _notificationService;
    private bool _isExpanded = false;
    private double _totalProgress;
    private double _totalSpeed;
    private string _statusText = "Initializing";
    private bool _hasFailures;
    // private bool _isPaused; // Unused

    public Guid? GroupKey { get; } // AlbumId or ProjectId
    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }
    public string Subtitle { get; }
    public string? ArtworkUrl { get; }
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> Tracks { get; }

    /// <summary>
    /// Row projection of this group's tracks — lets grouped view reuse the same slim
    /// HubDownloadRowTemplate as the flat Active view instead of the heavier "Golden Row"
    /// template, so there's one row template for Active (flat or grouped), not two.
    /// </summary>
    public ReadOnlyObservableCollection<DownloadRowViewModel> Rows { get; }

    public DateTime LastActivity { get; private set; }

    private int? _manualOrder;
    /// <summary>
    /// Explicit position set by dragging this group in the "Group by playlist" Active view.
    /// Null means "not manually placed" — the group sorts by <see cref="LastActivity"/> instead.
    /// Once any group in the list is dragged, DownloadCenterViewModel.ReorderActiveGroup assigns
    /// sequential values to every currently-visible group, "freezing" that order; a group that
    /// disappears and later reappears (all its tracks left and a new batch arrived) starts back
    /// at null since this is deliberately in-memory/session-scoped state, not persisted — once a
    /// playlist finishes downloading and drops out of the active list, its manual position no
    /// longer means anything.
    /// </summary>
    public int? ManualOrder
    {
        get => _manualOrder;
        set => this.RaiseAndSetIfChanged(ref _manualOrder, value);
    }

    // Aggregate Properties
    public double TotalProgress
    {
        get => _totalProgress;
        set => this.RaiseAndSetIfChanged(ref _totalProgress, value);
    }

    public double TotalSpeed
    {
        get => _totalSpeed;
        set => this.RaiseAndSetIfChanged(ref _totalSpeed, value);
    }
    
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }
    
    public bool HasFailures
    {
        get => _hasFailures;
        set => this.RaiseAndSetIfChanged(ref _hasFailures, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    // True when at least one child track is actively searching or downloading.
    public bool IsActive => Tracks.Any(t => t.IsActive);

    // True when tracks are queued but none are actively processing yet.
    public bool IsWaitingInQueue => !IsActive && Tracks.Any(t =>
        t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Stalled);

    // #140: Aggregate speed sparkline — last 30 samples, computed from active track averages.
    private readonly double[] _aggregateSpeedHistory = new double[30];
    private int _aggregateSpeedIndex;
    public IReadOnlyList<double> AggregateSpeedHistory => _aggregateSpeedHistory;

    // ── Playlist-level priority ───────────────────────────────────────────

    private static readonly SolidColorBrush[] _priorityBrushes =
    {
        new(Color.Parse("#CCDD2222")), // Critical — red
        new(Color.Parse("#CCBB7700")), // High — amber
        new(Color.Parse("#22777777")), // Normal — muted (badge hidden anyway)
        new(Color.Parse("#22555555")), // Low — dimmed
    };

    private PlaylistPriority _jobPriority = PlaylistPriority.Normal;
    public PlaylistPriority JobPriority
    {
        get => _jobPriority;
        private set
        {
            this.RaiseAndSetIfChanged(ref _jobPriority, value);
            this.RaisePropertyChanged(nameof(PriorityLabel));
            this.RaisePropertyChanged(nameof(PriorityBrush));
            this.RaisePropertyChanged(nameof(IsPriorityBadgeVisible));
            this.RaisePropertyChanged(nameof(IsCritical));
            this.RaisePropertyChanged(nameof(IsHighPriority));
            this.RaisePropertyChanged(nameof(IsLowPriority));
        }
    }

    private bool _isFocused;
    public bool IsFocused
    {
        get => _isFocused;
        private set => this.RaiseAndSetIfChanged(ref _isFocused, value);
    }

    // Computed display helpers
    public string PriorityLabel => _jobPriority switch
    {
        PlaylistPriority.Critical => "⚡ CRITICAL",
        PlaylistPriority.High     => "↑ HIGH",
        PlaylistPriority.Normal   => "· NORM",
        PlaylistPriority.Low      => "↓ LOW",
        _                         => "· NORM",
    };

    public IBrush PriorityBrush => _priorityBrushes[Math.Clamp((int)_jobPriority, 0, 3)];

    // Badge hidden for Normal to reduce visual noise
    public bool IsPriorityBadgeVisible => _jobPriority != PlaylistPriority.Normal || _isFocused;
    public bool IsCritical     => _jobPriority == PlaylistPriority.Critical;
    public bool IsHighPriority => _jobPriority == PlaylistPriority.High;
    public bool IsLowPriority  => _jobPriority == PlaylistPriority.Low;

    // ── Priority Commands ─────────────────────────────────────────────────

    // Commands — typed as ICommand so Avalonia compiled bindings can resolve them correctly.
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand VipStartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand SetCriticalCommand { get; }
    public ICommand SetHighCommand { get; }
    public ICommand SetNormalCommand { get; }
    public ICommand SetLowCommand { get; }
    public ICommand ToggleFocusModeCommand { get; }

    public DownloadGroupViewModel(
        IGroup<UnifiedTrackViewModel, string, Guid> group,
        DownloadManager downloadManager,
        ILibraryService libraryService,
        INotificationService? notificationService = null,
        Action<DownloadRowViewModel>? onSelectRow = null)
    {
        GroupKey = group.Key;
        _downloadManager = downloadManager;
        _libraryService = libraryService;
        _notificationService = notificationService;

        // Connect to the group cache
        var tracksLoader = group.Cache.Connect()
            .Bind(out var tracks)
            .Subscribe();

        Tracks = tracks;

        var rowsLoader = group.Cache.Connect()
            .Transform(x => new DownloadRowViewModel(x, onSelectRow))
            .DisposeMany()
            .Bind(out var rows)
            .Subscribe();

        Rows = rows;

        // Initialize Metadata from first track (assuming homogenous groups for now)
        var firstTrack = Tracks.FirstOrDefault()?.Model;

        // Initialize playlist-level priority from the DownloadManager cache
        if (GroupKey.HasValue)
        {
            _jobPriority = downloadManager.GetJobPriority(GroupKey.Value);
            _isFocused   = downloadManager.GetJobFocused(GroupKey.Value);
        }
        
        var sourcePlaylistName = firstTrack?.SourcePlaylistName;

        if (!string.IsNullOrEmpty(sourcePlaylistName))
        {
            Title = sourcePlaylistName;
            
            // Avoid using firstTrack.Artist as it makes playlists look like individual track/album releases
            var distinctArtists = Tracks.Select(t => t.Model.Artist).Distinct().Take(2).Count();
            var firstArtist = firstTrack?.Artist;
            Subtitle = distinctArtists > 1 ? "Mixed Artists" : (string.IsNullOrEmpty(firstArtist) ? "Imported Playlist" : $"By {firstArtist}");
            ArtworkUrl = firstTrack?.AlbumArtUrl;
        }
        else if (GroupKey == null)
        {
            Title = "Singles & Ad-Hoc";
            Subtitle = "Individual Downloads";
            ArtworkUrl = null;
        }
        else
        {
            // SourcePlaylistName missing on this track — look up the job name async and patch it in.
            // Shows a generic placeholder in the meantime rather than a blank/jarring title flash.
            var distinctArtists = Tracks.Select(t => t.Model.Artist).Distinct().Take(2).Count();
            Title    = "Project Selection"; // resolved below
            Subtitle = distinctArtists > 1 ? "Mixed Artists" : (firstTrack?.Artist ?? "Various Artists");
            ArtworkUrl = firstTrack?.AlbumArtUrl;
            var jobId = GroupKey;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (jobId.HasValue)
                    {
                        var job = await libraryService.FindPlaylistJobAsync(jobId.Value).ConfigureAwait(false);
                        var name = job?.SourceTitle;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => Title = !string.IsNullOrEmpty(name) ? name : "Unknown Playlist");
                    }
                    else
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => Title = "Unknown Playlist");
                    }
                }
                catch { Avalonia.Threading.Dispatcher.UIThread.Post(() => Title = "Unknown Playlist"); }
            });
        }

        // Aggregate Progress & Speed
        // We observe property changes on all items in the collection
        var aggregates = group.Cache.Connect()
            .WhenAnyPropertyChanged(nameof(UnifiedTrackViewModel.Progress), nameof(UnifiedTrackViewModel.DownloadSpeed), nameof(UnifiedTrackViewModel.State))
            .Subscribe(_ => RecalculateAggregates());

        // Also recalculate when list changes (add/remove)
        var listChanges = group.Cache.Connect()
            .Subscribe(_ => RecalculateAggregates());

        RecalculateAggregates(); // Initial calc

        // Group Commands — all four batched through dedicated DownloadManager methods (see their
        // doc comments): looping each track's own command here used to serialize one DB
        // round-trip per track through TrackRepository's app-wide static write semaphore, which is
        // what actually froze the UI on any playlist with more than a handful of tracks. That
        // freeze wasn't unique to VIP Start — every one of Pause/Resume/Retry/Cancel had the exact
        // same shape.
        PauseCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var items = Tracks.ToList().Where(x => x.IsActive).ToList();
            if (items.Count > 0)
            {
                await _downloadManager.PauseTracksAsync(items.Select(t => t.GlobalId));
            }
            NotifyGroupAction(items.Count, "Paused {0} download(s)", "Nothing to pause — no active downloads in this playlist");
        });

        ResumeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var items = Tracks.ToList();
            int affected = 0;

            // Pending/Stalled -> ForceStartTracksAsync (same batched path as VipStartCommand).
            var toForceStart = items.Where(t => t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Stalled).ToList();
            if (toForceStart.Count > 0)
            {
                await _downloadManager.ForceStartTracksAsync(toForceStart.Select(t => t.GlobalId));
                affected += toForceStart.Count;
            }

            // Paused -> ResumePausedTracksAsync, Failed -> HardRetryTracksAsync. Each does
            // meaningfully different per-track work (retry counter resets, search-attempt-log
            // clearing for the latter) than a plain state flip, so each gets its own dedicated
            // batched method rather than reusing ForceStartTracksAsync.
            var toResume = items.Where(t => t.State == PlaylistTrackState.Paused).ToList();
            if (toResume.Count > 0)
            {
                await _downloadManager.ResumePausedTracksAsync(toResume.Select(t => t.GlobalId));
                affected += toResume.Count;
            }

            var toRetry = items.Where(t => t.State == PlaylistTrackState.Failed).ToList();
            if (toRetry.Count > 0)
            {
                await _downloadManager.HardRetryTracksAsync(toRetry.Select(t => t.GlobalId));
                affected += toRetry.Count;
            }

            NotifyGroupAction(affected, "Resumed {0} track(s)", "Nothing to resume — no paused, queued, or failed tracks");
        });

        // Explicit queue-bypass group action for playlist cards. Batched — see
        // DownloadManager.ForceStartTracksAsync's doc comment: looping each track's own
        // ForceStartCommand here used to serialize one DB round-trip per track through an
        // app-wide static write semaphore, which is what actually froze the UI on any playlist
        // with more than a handful of pending tracks.
        VipStartCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var items = Tracks.ToList().Where(x =>
                         x.State == PlaylistTrackState.Pending ||
                         x.State == PlaylistTrackState.Stalled ||
                         x.State == PlaylistTrackState.Paused).ToList();
            if (items.Count > 0)
            {
                await _downloadManager.ForceStartTracksAsync(items.Select(t => t.GlobalId));
            }
            NotifyGroupAction(items.Count, "Bumped {0} track(s) to the front of the queue", "Nothing to bump — no queued or paused tracks");
        });

        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            // NOTE on scope: unlike Pause/Resume/Retry above, this deliberately does NOT move to a
            // single batched DB write. DownloadCenterSoftClearContractTests pins a real contract —
            // the soft-clear flag persists via one ILibraryService.UpdatePlaylistTrackAsync(t.Model)
            // call per track, independently mockable/verifiable — and collapsing that into
            // DownloadManager's internal batched writer would silently drop that guarantee. What
            // IS fixed: CancelTrack's own DB write (via UpdateStateAsync) was already fire-and-forget,
            // not the bottleneck; the actual serialization was this loop `await`-ing each track's
            // UpdatePlaylistTrackAsync one at a time before starting the next. Task.WhenAll still
            // issues exactly one call per track (same call count Moq verifies), but lets them all
            // reach TrackRepository's write semaphore back-to-back instead of one full round-trip
            // apart, which is what let a handful of dispatcher continuations pile up and make the
            // UI feel frozen on a large playlist.
            var items = Tracks.ToList();
            var saveTasks = new List<Task>(items.Count);

            foreach (var t in items)
            {
                // Status Reset Safety Check: if not already in a terminal state, make sure the
                // explicit save below (which CancelTrack's own state transition can race against —
                // its UpdateStateAsync call is fire-and-forget) never persists a stale in-progress
                // status. Skipped, not Failed: DownloadManager.SyncDbAsync maps
                // PlaylistTrackState.Cancelled -> TrackStatus.Skipped everywhere else, and this
                // used to hardcode Failed instead — meaning a plain user-initiated cancel could
                // land in the DB as "Failed" with zero audit-log trace explaining why (the direct
                // property set bypasses the audit-logged UpdateStateAsync path entirely), which is
                // exactly the kind of silently-wrong status this app should never show.
                //
                // A denylist of the 3 genuinely terminal states, not an allowlist of in-flight
                // ones: the original allowlist (Downloading/Searching/Queued/Pending) missed
                // Converting/Paused/Deferred/Stalled/WaitingForConnection — Stalled in particular
                // is a very plausible real cancel target — and would silently miss any new state
                // added later too.
                if (t.State != PlaylistTrackState.Completed &&
                    t.State != PlaylistTrackState.Failed &&
                    t.State != PlaylistTrackState.Cancelled)
                {
                    t.Model.Status = TrackStatus.Skipped;
                }

                // Cancel
                _downloadManager.CancelTrack(t.GlobalId);

                // Soft clear
                t.IsClearedFromDownloadCenter = true;
                t.Model.IsClearedFromDownloadCenter = true;

                // Persist soft clear — collected, not awaited here (see comment above).
                saveTasks.Add(_libraryService.UpdatePlaylistTrackAsync(t.Model));
            }

            await Task.WhenAll(saveTasks);
            NotifyGroupAction(items.Count, "Cancelled {0} track(s) from " + Title, null);
        });

        ToggleExpandedCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });

        // Priority commands — fire-and-forget; scheduler picks up immediately via in-memory stamp
        SetCriticalCommand = ReactiveCommand.CreateFromTask(() => ApplyJobPriorityAsync(PlaylistPriority.Critical));
        SetHighCommand      = ReactiveCommand.CreateFromTask(() => ApplyJobPriorityAsync(PlaylistPriority.High));
        SetNormalCommand    = ReactiveCommand.CreateFromTask(() => ApplyJobPriorityAsync(PlaylistPriority.Normal));
        SetLowCommand       = ReactiveCommand.CreateFromTask(() => ApplyJobPriorityAsync(PlaylistPriority.Low));
        ToggleFocusModeCommand = ReactiveCommand.CreateFromTask(ToggleFocusAsync);

        _cleanUp = new System.Reactive.Disposables.CompositeDisposable(tracksLoader, rowsLoader, aggregates, listChanges);
    }

    private void RecalculateAggregates()
    {
        if (Tracks.Count == 0)
        {
            TotalProgress = 0;
            TotalSpeed = 0;
            StatusText = "Empty";
            return;
        }

        // Simple Average Progress
        TotalProgress = Tracks.Average(t => t.Progress);
        
        // Sum Speed
        TotalSpeed = Tracks.Sum(t => t.DownloadSpeed);

        // #140: Push aggregate speed into sparkline ring buffer
        _aggregateSpeedHistory[_aggregateSpeedIndex % 30] = TotalSpeed;
        _aggregateSpeedIndex++;
        this.RaisePropertyChanged(nameof(AggregateSpeedHistory));
        this.RaisePropertyChanged(nameof(IsActive));
        this.RaisePropertyChanged(nameof(IsWaitingInQueue));

        // Status Logic
        int completed = Tracks.Count(t => t.State == PlaylistTrackState.Completed);
        int failed = Tracks.Count(t => t.State == PlaylistTrackState.Failed);
        int searching = Tracks.Count(t => t.State == PlaylistTrackState.Searching);
        int downloading = Tracks.Count(t => t.State == PlaylistTrackState.Downloading);
        int queued = Tracks.Count(t => t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Stalled);

        HasFailures = failed > 0;

        if (searching > 0 || downloading > 0)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (searching > 0) parts.Add($"{searching} searching");
            if (downloading > 0) parts.Add($"{downloading} downloading");
            if (queued > 0) parts.Add($"{queued} on deck");
            StatusText = string.Join(", ", parts);
        }
        else if (queued > 0)
        {
            StatusText = $"{queued} on deck";
        }
        else if (HasFailures)
        {
            StatusText = $"{failed} failed";
        }
        else if (completed == Tracks.Count && Tracks.Count > 0)
        {
            StatusText = "Completed";
        }
        else
        {
            StatusText = $"{Tracks.Count} tracks";
        }

        LastActivity = Tracks.Any() ? Tracks.Max(t => t.Model.AddedAt) : DateTime.MinValue;
    }

    private async System.Threading.Tasks.Task ApplyJobPriorityAsync(PlaylistPriority priority)
    {
        if (!GroupKey.HasValue) return;
        await _downloadManager.SetJobPriorityAsync(GroupKey.Value, priority);
        JobPriority = priority;
    }

    private async System.Threading.Tasks.Task ToggleFocusAsync()
    {
        if (!GroupKey.HasValue) return;
        var newFocused = !_isFocused;
        await _downloadManager.ToggleFocusModeAsync(GroupKey.Value, newFocused);
        IsFocused = newFocused;
        if (newFocused)
            JobPriority = PlaylistPriority.Critical;
        else
            JobPriority = _downloadManager.GetJobPriority(GroupKey.Value);
    }

    public void Dispose()
    {
        _cleanUp.Dispose();
    }

    private static void ExecuteIfAllowed(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>
    /// Surfaces a toast after a group-level action (Pause/Resume/VIP Start/Cancel) — these
    /// commands silently no-op when nothing matches (e.g. clicking Pause with no active
    /// downloads), which previously looked identical to a click doing nothing at all.
    /// </summary>
    private void NotifyGroupAction(int affectedCount, string successFormat, string? zeroMessage)
    {
        if (affectedCount > 0)
        {
            _notificationService?.Show(Title, string.Format(successFormat, affectedCount), NotificationType.Success, TimeSpan.FromSeconds(3));
        }
        else if (zeroMessage != null)
        {
            _notificationService?.Show(Title, zeroMessage, NotificationType.Information, TimeSpan.FromSeconds(3));
        }
    }
}
