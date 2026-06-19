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
    public DateTime LastActivity { get; private set; }
    
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

    public DownloadGroupViewModel(IGroup<UnifiedTrackViewModel, string, Guid> group, DownloadManager downloadManager, ILibraryService libraryService)
    {
        GroupKey = group.Key;
        _downloadManager = downloadManager;
        _libraryService = libraryService;

        // Connect to the group cache
        var tracksLoader = group.Cache.Connect()
            .Bind(out var tracks)
            .Subscribe();

        Tracks = tracks;

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
            Title = "Project Selection";
            var distinctArtists = Tracks.Select(t => t.Model.Artist).Distinct().Take(2).Count();
            Subtitle = distinctArtists > 1 ? "Mixed Artists" : (firstTrack?.Artist ?? "Various Artists");
            ArtworkUrl = firstTrack?.AlbumArtUrl;
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

        // Group Commands
        PauseCommand = ReactiveCommand.Create(() => 
        {
            var items = Tracks.ToList();
            foreach (var t in items.Where(x => x.IsActive))
            {
                ExecuteIfAllowed(t.PauseCommand);
            }
        });
        
        ResumeCommand = ReactiveCommand.Create(() => 
        {
            var items = Tracks.ToList();
            foreach (var t in items)
            {
                if (t.State == PlaylistTrackState.Paused)
                {
                    ExecuteIfAllowed(t.ResumeCommand);
                    continue;
                }

                // "Initiate/continue" for queued or stalled items in a group action.
                if (t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Stalled)
                {
                    ExecuteIfAllowed(t.ForceStartCommand);
                    continue;
                }

                // "Restart" for failed items in a group action.
                if (t.State == PlaylistTrackState.Failed)
                {
                    ExecuteIfAllowed(t.RetryCommand);
                }
            }
        });

        // Explicit queue-bypass group action for playlist cards.
        VipStartCommand = ReactiveCommand.Create(() =>
        {
            var items = Tracks.ToList();
            foreach (var t in items.Where(x =>
                         x.State == PlaylistTrackState.Pending ||
                         x.State == PlaylistTrackState.Stalled ||
                         x.State == PlaylistTrackState.Paused))
            {
                ExecuteIfAllowed(t.ForceStartCommand);
            }
        });

        CancelCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var items = Tracks.ToList();
            foreach (var t in items)
            {
                // Status Reset Safety Check: If mid-download/searching, reset to Failed
                if (t.State == PlaylistTrackState.Downloading || 
                    t.State == PlaylistTrackState.Searching || 
                    t.State == PlaylistTrackState.Queued || 
                    t.State == PlaylistTrackState.Pending)
                {
                    t.Model.Status = TrackStatus.Failed;
                }
                
                // Cancel
                _downloadManager.CancelTrack(t.GlobalId);
                
                // Soft clear
                t.IsClearedFromDownloadCenter = true;
                t.Model.IsClearedFromDownloadCenter = true;
                
                // Persist soft clear
                await _libraryService.UpdatePlaylistTrackAsync(t.Model);
            }
        });

        ToggleExpandedCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });

        // Priority commands — fire-and-forget; scheduler picks up immediately via in-memory stamp
        SetCriticalCommand = ReactiveCommand.CreateFromTask(() => ApplyJobPriorityAsync(PlaylistPriority.Critical));
        SetHighCommand      = ReactiveCommand.CreateFromTask(() => ApplyJobPriorityAsync(PlaylistPriority.High));
        SetNormalCommand    = ReactiveCommand.CreateFromTask(() => ApplyJobPriorityAsync(PlaylistPriority.Normal));
        SetLowCommand       = ReactiveCommand.CreateFromTask(() => ApplyJobPriorityAsync(PlaylistPriority.Low));
        ToggleFocusModeCommand = ReactiveCommand.CreateFromTask(ToggleFocusAsync);

        _cleanUp = new System.Reactive.Disposables.CompositeDisposable(tracksLoader, aggregates, listChanges);
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
}
