using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Configuration;
using SLSKDONET.ViewModels;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2.5: Global Download Center - Singleton observer that tracks all downloads.
/// Manages Active, Completed, and Failed collections with real-time event subscriptions.
/// </summary>
public class DownloadCenterViewModel : ReactiveObject, IDisposable
{
    private readonly DownloadManager _downloadManager;
    private readonly IEventBus _eventBus;
    private readonly AppConfig _config;
    private readonly CompositeDisposable _subscriptions = new();
    
    // Collections (DynamicData Source)
    private readonly SourceCache<UnifiedTrackViewModel, string> _downloadsSource = new(x => x.GlobalId);

    // Public ReadOnly Collections (Bound to UI)
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _activeDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> ActiveDownloads => _activeDownloads;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _completedDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> CompletedDownloads => _completedDownloads;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _failedDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> FailedDownloads => _failedDownloads;

    // Phase 2: Active Groups (Album-Centric)
    private readonly ReadOnlyObservableCollection<DownloadGroupViewModel> _activeGroups;
    public ReadOnlyObservableCollection<DownloadGroupViewModel> ActiveGroups => _activeGroups;

    // Swimlanes (Derived from Active)
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _expressItems;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> ExpressItems => _expressItems;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _activeTracks;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> ActiveTracks => _activeTracks;

    private readonly ReadOnlyObservableCollection<DownloadGroupViewModel> _expressGroups;
    public ReadOnlyObservableCollection<DownloadGroupViewModel> ExpressGroups => _expressGroups;

    private readonly ReadOnlyObservableCollection<DownloadGroupViewModel> _standardGroups;
    public ReadOnlyObservableCollection<DownloadGroupViewModel> StandardGroups => _standardGroups;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _standardItems;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> StandardItems => _standardItems;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _backgroundItems;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> BackgroundItems => _backgroundItems;

    // Ongoing vs Queued Split
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _ongoingDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> OngoingDownloads => _ongoingDownloads;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _queuedDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> QueuedDownloads => _queuedDownloads;

    // Stats
    private int _activeCount;
    public int ActiveCount
    {
        get => _activeCount;
        set 
        {
            this.RaiseAndSetIfChanged(ref _activeCount, value);
            this.RaisePropertyChanged(nameof(HasAnyActiveOrQueued));
        }
    } 

    private int _queuedCount;
    public int QueuedCount
    {
        get => _queuedCount;
        set 
        {
            this.RaiseAndSetIfChanged(ref _queuedCount, value);
            this.RaisePropertyChanged(nameof(HasAnyActiveOrQueued));
        }
    }

    private int _completedTodayCount;
    public int CompletedTodayCount
    {
        get => _completedTodayCount;
        set => this.RaiseAndSetIfChanged(ref _completedTodayCount, value);
    }

    private bool _isGlobalSearching;
    public bool IsGlobalSearching
    {
        get => _isGlobalSearching;
        set => this.RaiseAndSetIfChanged(ref _isGlobalSearching, value);
    }

    private int _searchingCount;
    public int SearchingCount 
    {
        get => _searchingCount;
        set => this.RaiseAndSetIfChanged(ref _searchingCount, value);
    }
    
    private int _downloadingCount;
    public int DownloadingCount
    {
        get => _downloadingCount;
        set => this.RaiseAndSetIfChanged(ref _downloadingCount, value);
    }
    
    private int _failedCount;
    public int FailedCount
    {
        get => _failedCount;
        set => this.RaiseAndSetIfChanged(ref _failedCount, value);
    }
    
    private string _globalSpeed = "0 MB/s";
    public string GlobalSpeed
    {
        get => _globalSpeed;
        set => this.RaiseAndSetIfChanged(ref _globalSpeed, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    private string? _globalStatusMessage;
    public string? GlobalStatusMessage
    {
        get => _globalStatusMessage;
        set => this.RaiseAndSetIfChanged(ref _globalStatusMessage, value);
    }

    private bool _isGlobalStatusVisible;
    public bool IsGlobalStatusVisible
    {
        get => _isGlobalStatusVisible;
        set => this.RaiseAndSetIfChanged(ref _isGlobalStatusVisible, value);
    }

    private bool _isGlobalStatusError;
    public bool IsGlobalStatusError
    {
        get => _isGlobalStatusError;
        set => this.RaiseAndSetIfChanged(ref _isGlobalStatusError, value);
    }

    private bool _showEngineLogs;
    public bool ShowEngineLogs
    {
        get => _showEngineLogs;
        set => this.RaiseAndSetIfChanged(ref _showEngineLogs, value);
    }

    public ICommand ToggleLogsCommand { get; }
    public ICommand ClearSecurityQualityLogsCommand { get; }
    
    // Alias for HomeViewModel compatibility
    public string GlobalSpeedDisplay => GlobalSpeed;

    // Phase 12.3: Bulk Selection State
    private ObservableCollection<UnifiedTrackViewModel> _selectedItems = new();
    public ObservableCollection<UnifiedTrackViewModel> SelectedItems
    {
        get => _selectedItems;
        set => this.RaiseAndSetIfChanged(ref _selectedItems, value);
    }

    private bool _hasSelection;
    public bool HasSelection
    {
        get => _hasSelection;
        set => this.RaiseAndSetIfChanged(ref _hasSelection, value);
    }

    public bool HasAnyActiveOrQueued => ActiveCount > 0 || QueuedCount > 0;

    private bool _isAutoEnrichEnabled;
    public bool IsAutoEnrichEnabled
    {
        get => _isAutoEnrichEnabled;
        set 
        {
            if (this.RaiseAndSetIfChanged(ref _isAutoEnrichEnabled, value))
            {
                _config.IsAutoEnrichEnabled = value;
            }
        }
    }

    // Engine Master Status
    public bool IsEngineRunning => _downloadManager.IsRunning;
    public bool IsEnginePaused => _downloadManager.IsPaused;
    public string EngineStatusText => !IsEngineRunning ? "Engine Offline" : (IsEnginePaused ? "Engine Paused" : "Engine Active");
    public string EngineStatusColor => !IsEngineRunning ? "#FF5252" : (IsEnginePaused ? "#FFA500" : "#4CAF50");
    public string EngineStatusIcon => !IsEngineRunning ? "⚡" : (IsEnginePaused ? "⏸" : "⚡");
    
    // Phase 2: Diagnostic Transparency
    public bool IsSoulseekConnected => _downloadManager.SoulseekConnected;
    public bool IsBackingOff => _downloadManager.IsBackingOff;
    public int BackoffSeconds => _downloadManager.CurrentBackoffSeconds;
    public int ActiveWorkerSlots => _downloadManager.ActiveWorkerSlots;
    public int TotalWorkerSlots => _downloadManager.TotalWorkerSlots;
    public string WorkerSlotsDisplay => $"{ActiveWorkerSlots}/{TotalWorkerSlots}";

    public int MaxConcurrentDownloads
    {
        get => _downloadManager.MaxActiveDownloads;
        set 
        {
             // Validate range 1-50
             if (value < 1 || value > 50) return;
             
             if (_downloadManager.MaxActiveDownloads != value)
             {
                 _downloadManager.MaxActiveDownloads = value;
                 this.RaisePropertyChanged();
             }
        }
    }
    
    // Commands
    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand ClearFailedCommand { get; }
    public ICommand RetryAllFailedCommand { get; }
    
    // Phase 12.3: Bulk Commands
    public ICommand VipStartSelectedCommand { get; }
    public ICommand CancelSelectedCommand { get; }
    public ICommand PauseSelectedCommand { get; }
    public ICommand ResumeSelectedCommand { get; }

    // Master Commands
    public ICommand StartEngineCommand { get; }
    public ICommand StopEngineCommand { get; }
    public ICommand ToggleEnginePauseCommand { get; }
    
    private readonly ArtworkCacheService _artworkCache;
    private readonly ILibraryService _libraryService;
    
    public DownloadCenterViewModel(
        DownloadManager downloadManager,
        IEventBus eventBus,
        AppConfig config,
        ArtworkCacheService artworkCache,
        ILibraryService libraryService)
    {
        _downloadManager = downloadManager;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _libraryService = libraryService;
        _config = config;
        _isAutoEnrichEnabled = _config.IsAutoEnrichEnabled;
        
        ToggleLogsCommand = ReactiveCommand.Create(() => ShowEngineLogs = !ShowEngineLogs);
        ClearSecurityQualityLogsCommand = ReactiveCommand.Create(() => { });

        // Phase 6: Security & Quality diagnostics feed (Shield / Gate visibility)
        _eventBus.GetEvent<SecurityAuditEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                // SecurityQualityLogs removed
            })
            .DisposeWith(_subscriptions);
        
        // Initialize commands (ReactiveCommand)
        PauseAllCommand = ReactiveCommand.Create(PauseAll, 
            this.WhenAnyValue(x => x.ActiveCount, count => count > 0));
        
        ResumeAllCommand = ReactiveCommand.CreateFromTask(async () => await _downloadManager.ResumeAllAsync());
        
        StartEngineCommand = ReactiveCommand.CreateFromTask(async () => await _downloadManager.StartAsync(), 
            this.WhenAnyValue(x => x.IsEngineRunning, running => !running));
            
        StopEngineCommand = ReactiveCommand.CreateFromTask(async () => await _downloadManager.StopAsync(),
            this.WhenAnyValue(x => x.IsEngineRunning));

        ToggleEnginePauseCommand = ReactiveCommand.CreateFromTask(async () => await _downloadManager.TogglePauseEngineAsync(),
            this.WhenAnyValue(x => x.IsEngineRunning));

        // Sync manager state
        _downloadManager.PropertyChanged += (s, e) => 
        {
             Avalonia.Threading.Dispatcher.UIThread.Post(() => 
             {
                 if (e.PropertyName == nameof(DownloadManager.IsRunning) || e.PropertyName == nameof(DownloadManager.IsPaused))
                 {
                     this.RaisePropertyChanged(nameof(IsEngineRunning));
                     this.RaisePropertyChanged(nameof(IsEnginePaused));
                     this.RaisePropertyChanged(nameof(EngineStatusText));
                     this.RaisePropertyChanged(nameof(EngineStatusColor));
                     this.RaisePropertyChanged(nameof(EngineStatusIcon));
                 }
                 else if (e.PropertyName == nameof(DownloadManager.ActiveWorkerSlots))
                 {
                     this.RaisePropertyChanged(nameof(ActiveWorkerSlots));
                     this.RaisePropertyChanged(nameof(WorkerSlotsDisplay));
                 }
                 else if (e.PropertyName == nameof(DownloadManager.SoulseekConnected))
                 {
                     this.RaisePropertyChanged(nameof(IsSoulseekConnected));
                 }
                 else if (e.PropertyName == nameof(DownloadManager.IsBackingOff) || e.PropertyName == nameof(DownloadManager.CurrentBackoffSeconds))
                 {
                     this.RaisePropertyChanged(nameof(IsBackingOff));
                     this.RaisePropertyChanged(nameof(BackoffSeconds));
                 }
             });
        };
        
        ClearCompletedCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var completedItems = _downloadsSource.Items
                .Where(x => x.State == PlaylistTrackState.Completed && !x.IsClearedFromDownloadCenter)
                .ToList();
            foreach (var item in completedItems)
            {
                item.IsClearedFromDownloadCenter = true;
                item.Model.IsClearedFromDownloadCenter = true;
                await _libraryService.UpdatePlaylistTrackAsync(item.Model);
            }
        });
        
        ClearFailedCommand = ReactiveCommand.Create(() => 
            _downloadsSource.Remove(_downloadsSource.Items.Where(x => x.State == PlaylistTrackState.Failed).ToList()));

        RetryAllFailedCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var failedItems = FailedDownloads.ToList();
            foreach (var item in failedItems)
            {
                // Fix: Call manager directly to ensure execution and avoid ReactiveCommand subscription issues
                await _downloadManager.HardRetryTrack(item.GlobalId);
            }
            await Task.CompletedTask;
        }, this.WhenAnyValue(x => x.FailedCount, count => count > 0));
        
        // Phase 12.3: Bulk Command Implementation
        VipStartSelectedCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var selectedArgs = SelectedItems.ToList(); // Snapshot
            foreach (var item in selectedArgs)
            {
                if (item.CanForceStart)
                {
                    await _downloadManager.ForceStartTrack(item.GlobalId);
                }
            }
            SelectedItems.Clear(); // Clear selection after action? Maybe keep it. Let's clear for feedback.
            HasSelection = false;
        }, this.WhenAnyValue(x => x.HasSelection)); // Simplified binding, ideally check item states

        CancelSelectedCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var selectedArgs = SelectedItems.ToList();
            foreach (var item in selectedArgs)
            {
                if (item.IsActive || item.IsWaiting)
                {
                    _downloadManager.CancelTrack(item.GlobalId);
                }
            }
            SelectedItems.Clear();
            HasSelection = false;
        }, this.WhenAnyValue(x => x.HasSelection));
        
        PauseSelectedCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var selectedArgs = SelectedItems.ToList();
            foreach (var item in selectedArgs)
            {
                if (item.IsActive)
                {
                    await _downloadManager.PauseTrackAsync(item.GlobalId);
                }
            }
            // Keep selection? Usually pause/resume implies we might want to do more. 
            // matching behavior of Cancel (clear) for consistency.
            SelectedItems.Clear();
            HasSelection = false;
        }, this.WhenAnyValue(x => x.HasSelection));

        ResumeSelectedCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var selectedArgs = SelectedItems.ToList();
            foreach (var item in selectedArgs)
            {
                if (item.IsPaused)
                {
                    await _downloadManager.ResumeTrackAsync(item.GlobalId);
                }
            }
            SelectedItems.Clear();
            HasSelection = false;
        }, this.WhenAnyValue(x => x.HasSelection));
        
        // Monitor Selection Changes
        SelectedItems.CollectionChanged += (s, e) => HasSelection = SelectedItems.Count > 0;
        
        // Initialize DynamicData Pipelines
        
        // Critical: Lifecycle Management - Dispose ViewModels when removed from SourceCache (e.g. Clear Completed)
        _downloadsSource.Connect()
            .DisposeMany()
            .Subscribe()
            .DisposeWith(_subscriptions);

        // 1. Base Pipeline (Active vs Completed vs Failed)
        var sharedSource = _downloadsSource.Connect()
            .AutoRefresh(x => x.State) // Logic re-evaluates when State changes
            .AutoRefresh(x => x.IsActive) // FIX: UI lists bind to IsActive etc.
            .AutoRefresh(x => x.IsCompleted)
            .AutoRefresh(x => x.IsFailed)
            .AutoRefresh(x => x.IsClearedFromDownloadCenter) // Soft Clear
            .Publish(); // Share subscription

        // Active Pipeline
        var activeComparer = SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.State == PlaylistTrackState.Downloading);
        
        sharedSource
            .Filter(x => x.IsActive) // strictly downloading/searching
            .SortAndBind(out _activeDownloads, activeComparer)
            .DisposeMany() // Dispose VMs when removed from Active? No, they might move to Completed.
            // CAREFUL: DisposeMany() here would dispose items when filtered out.
            // Since items move between collections, we should ONLY dispose when removed from Source.
            // DynamicData's DisposeMany() on the SourceCache connects does that.
            .Subscribe()
            .DisposeWith(_subscriptions);

        // 1.1 Ongoing Downloads (Downloading/Searching state)
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Downloading || x.State == PlaylistTrackState.Searching)
            .SortAndBind(out _ongoingDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.State == PlaylistTrackState.Downloading).ThenByDescending(x => x.DownloadSpeed))
            .Subscribe(_ => {
                DownloadingCount = _downloadsSource.Items.Count(x => x.State == PlaylistTrackState.Downloading);
                SearchingCount = _downloadsSource.Items.Count(x => x.State == PlaylistTrackState.Searching);
            })
            .DisposeWith(_subscriptions);

        // 1.2 Queued Downloads (Queued/Pending -> IsWaiting)
        sharedSource
            .Filter(x => x.IsWaiting)
            .SortAndBind(out _queuedDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Ascending(x => x.Model.Priority).ThenByAscending(x => x.Model.AddedAt))
            .Subscribe(_ => QueuedCount = _queuedDownloads.Count)
            .DisposeWith(_subscriptions);

        // Phase 11.1: Global Search Status Logic
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Searching)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => 
            {
                SearchingCount = _downloadsSource.Items.Count(x => x.State == PlaylistTrackState.Searching);
                IsGlobalSearching = SearchingCount > 0;
            })
            .DisposeWith(_subscriptions);


        // Update counts
        _activeDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ActiveCount = _activeDownloads.Count)
            .DisposeWith(_subscriptions);

        _queuedDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => QueuedCount = _queuedDownloads.Count)
            .DisposeWith(_subscriptions);

        // Phase 11: Global Search Status Tracking
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Searching)
            .ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => {
                SearchingCount = _downloadsSource.Items.Count(x => x.State == PlaylistTrackState.Searching);
                IsGlobalSearching = SearchingCount > 0;
            })
            .DisposeWith(_subscriptions);

        // Phase 2: Grouping Pipeline (On-Deck / Waiting / Stalled Only)
        // Group by Source/Origin (e.g. Spotify Playlist ID or Search Session ID)
        sharedSource
            .Filter(x => x.IsWaiting || x.IsStalled) // Match on-deck groups, exclude strictly active (searching/downloading)
            .Group(x => x.Model.SourcePlaylistId ?? x.Model.PlaylistId)
            .Transform((IGroup<UnifiedTrackViewModel, string, Guid> group) => new DownloadGroupViewModel(group))
            .DisposeMany() 
            .SortAndBind(out _activeGroups, SortExpressionComparer<DownloadGroupViewModel>.Descending(x => x.LastActivity))
            .Subscribe()
            .DisposeWith(_subscriptions);

        // Phase 9: Direct Active Tracks (Searching or Downloading - Flat List)
        // Phase 11: Prioritize Downloading over Searching
        var directActiveComparer = SortExpressionComparer<UnifiedTrackViewModel>
            .Descending(x => x.State == PlaylistTrackState.Downloading)
            .ThenByDescending(x => x.Model.AddedAt);
            
        sharedSource
            .Filter(x => x.IsActive && x.State != PlaylistTrackState.Stalled) // estrictly exclude stalled from active
            .SortAndBind(out _activeTracks, directActiveComparer)
            .Subscribe()
            .DisposeWith(_subscriptions);

        // Phase 8: Split Grouping Pipelines for Swimlanes
        // Express Groups (Priority 0 - Only if NOT actively downloading/searching, or always? 
        // User wants active on top, groups below. We keep groups for those waiting or stalled)
        sharedSource
            .Filter(x => (x.IsWaiting || x.IsStalled) && x.Model.Priority == 0)
            .Group(x => x.Model.SourcePlaylistId ?? x.Model.PlaylistId)
            .Transform((IGroup<UnifiedTrackViewModel, string, Guid> group) => new DownloadGroupViewModel(group))
            .DisposeMany()
            .SortAndBind(out _expressGroups, SortExpressionComparer<DownloadGroupViewModel>.Descending(x => x.LastActivity))
            .Subscribe()
            .DisposeWith(_subscriptions);

        // Standard Groups (Priority >= 1)
        sharedSource
            .Filter(x => (x.IsWaiting || x.IsStalled) && x.Model.Priority >= 1)
            .Group(x => x.Model.SourcePlaylistId ?? x.Model.PlaylistId)
            .Transform((IGroup<UnifiedTrackViewModel, string, Guid> group) => new DownloadGroupViewModel(group))
            .DisposeMany()
            .SortAndBind(out _standardGroups, SortExpressionComparer<DownloadGroupViewModel>.Descending(x => x.LastActivity))
            .Subscribe()
            .DisposeWith(_subscriptions);

        // Auto-Enrich Hand-off removed

        // Completed Pipeline
        var completedFilter = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .Select(BuildFilter);

        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Completed && !x.IsClearedFromDownloadCenter)
            .Filter(completedFilter)
            .SortAndBind(out _completedDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.Model.AddedAt))
            .Subscribe();

        _completedDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => CompletedTodayCount = _completedDownloads.Count)
            .DisposeWith(_subscriptions);

        // Failed Pipeline
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Failed || x.State == PlaylistTrackState.Cancelled || x.State == PlaylistTrackState.Stalled)
            .Filter(completedFilter) // Reuse filter for now
            .Bind(out _failedDownloads)
            .Subscribe();

        _failedDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => FailedCount = _failedDownloads.Count)
            .DisposeWith(_subscriptions);


        // 2. Swimlane Pipelines (Derived from sharedSource filtered to Active)
        // Express: Priority 0
        sharedSource
            .Filter(x => (x.IsActive || x.IsWaiting || x.IsStalled) && x.Model.Priority == 0)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _expressItems)
            .Subscribe();

        // Standard: Priority 1-9
        sharedSource
             .Filter(x => (x.IsActive || x.IsWaiting || x.IsStalled) && x.Model.Priority >= 1 && x.Model.Priority < 10)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _standardItems)
            .Subscribe();

        // Background: Priority >= 10
        sharedSource
            .Filter(x => (x.IsActive || x.IsWaiting || x.IsStalled) && x.Model.Priority >= 10)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _backgroundItems)
            .Subscribe();

        sharedSource.Connect(); // Connect the publisher

        // Subscribe to creation events ONLY (State/Progress handled by Smart Component)
        _eventBus.GetEvent<TrackAddedEvent>()
            .Subscribe(OnTrackAdded)
            .DisposeWith(_subscriptions);
            
        // Used to catch removals (e.g. Delete command from within VM)
        _eventBus.GetEvent<TrackRemovedEvent>()
             .Subscribe(OnTrackRemoved)
             .DisposeWith(_subscriptions);

        _eventBus.GetEvent<GlobalStatusEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                GlobalStatusMessage = e.Message;
                IsGlobalStatusVisible = e.IsActive;
                IsGlobalStatusError = e.IsError;
            })
            .DisposeWith(_subscriptions);

        // Phase 3.7 Fix: Respond to background hydration completion
        _eventBus.GetEvent<DownloadManagerHydratedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => 
            {
                Serilog.Log.Information("DownloadCenterViewModel: Received hydration signal, refreshing view...");
                InitialHydration();
            })
            .DisposeWith(_subscriptions);

        // UI Batcher: coalesce high-frequency progress events to 200ms UI pushes.
        _eventBus.GetEvent<TrackProgressChangedEvent>()
            .Buffer(TimeSpan.FromMilliseconds(200))
            .Where(batch => batch.Count > 0)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshGlobalSpeed())
            .DisposeWith(_subscriptions);
        
        // Start global speed calculator
        StartGlobalSpeedTimer();

        // Phase 3.7: Defensive Hydration - Catch up if Manager already finished while we were initializing
        if (_downloadManager.IsHydrated)
        {
            Serilog.Log.Information("DownloadCenterViewModel: Manager already hydrated, performing immediate hydration...");
            InitialHydration();
        }
        
    }

    private Func<UnifiedTrackViewModel, bool> BuildFilter(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return _ => true;

        return vm => vm.TrackTitle.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                     vm.ArtistName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }
    
    private void InitialHydration()
    {
        var existingDownloads = _downloadManager.GetAllDownloads();
        
        foreach (var (model, state) in existingDownloads)
        {
            var fakeEvent = new TrackAddedEvent(model, state);
            OnTrackAdded(fakeEvent);
        }
    }
    
    private void OnTrackAdded(TrackAddedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var track = e.TrackModel;
            
            // Phase 2.5: Create Smart View Model
            var viewModel = new UnifiedTrackViewModel(track, _downloadManager, _eventBus, _artworkCache, _libraryService);
            
            // Phase 12.3: Monitor Selection
            viewModel.WhenAnyValue(x => x.IsSelected)
                .Subscribe(selected =>
                {
                    if (selected) 
                    {
                        if (!SelectedItems.Contains(viewModel)) SelectedItems.Add(viewModel);
                    }
                    else 
                    {
                        SelectedItems.Remove(viewModel);
                    }
                })
                .DisposeWith(_subscriptions); // Note: Should ideally attach to VM lifetime, but Global subscriptions are fine for now.

            // Set initial state override if needed
            if (e.InitialState.HasValue)
            {
                viewModel.State = e.InitialState.Value;
            }
            
            _downloadsSource.AddOrUpdate(viewModel);
        });
    }
    
    // New: Handle global removal
    private void OnTrackRemoved(TrackRemovedEvent e)
    {
         Dispatcher.UIThread.Post(() =>
        {
            _downloadsSource.Remove(e.TrackGlobalId);
        });
    }
    
    private void StartGlobalSpeedTimer()
    {
        var timer = new System.Timers.Timer(200);
        timer.Elapsed += (s, e) =>
        {
            try
            {
                Dispatcher.UIThread.Post(() => RefreshGlobalSpeed());
            }
            catch { }

            // Phase 12.3: Slot Health Check (Piggyback on 1s timer)
            CheckSlotHealth();
        };
        timer.Start();
    }

    private void RefreshGlobalSpeed()
    {
        var totalSpeedBytes = ActiveDownloads
            .Where(d => d.State == PlaylistTrackState.Downloading)
            .Sum(d => d.CurrentSpeedBytes);

        GlobalSpeed = totalSpeedBytes > 1024 * 1024
            ? $"{totalSpeedBytes / 1024 / 1024:F1} MB/s"
            : $"{totalSpeedBytes / 1024:F0} KB/s";
    }
    
    private async Task PauseAll()
    {
        await _downloadManager.PauseAllAsync();
    }
    
    public void Dispose()
    {
        _subscriptions.Dispose();
        _downloadsSource.Dispose();
    }

    // Phase 12.3: Slot Health Logic
    private void CheckSlotHealth()
    {
        try
        {
            var activeItems = ActiveDownloads.Where(d => d.State == PlaylistTrackState.Downloading).ToList();
            foreach (var item in activeItems)
            {
                // Logic: If item says Downloading but has 0 speed for > 30 seconds, mark stalled?
                // Or better: Check if the DownloadManager considers it stalled.
                // Since we don't have direct access to internal tasks, we use heuristics.
                
                // For now, we trust the DownloadManager to set Stalled state via events.
                // But we can detect "Ghosts" - e.g. state is Downloading but speed is 0 for a long time.
                
                if (item.CurrentSpeedBytes < 100 && (DateTime.UtcNow - item.LastActivity).TotalSeconds > 30)
                {
                    // This track thinks it's downloading but hasn't moved in 30s.
                    // We won't force change state here to avoid fighting the Manager,
                    // but we could trigger a "Soft Stall" check in the View.
                    
                    // Actually, let's just log it or maybe update the StalledReason if it's empty
                    if (!item.IsStalled)
                    {
                         // Potential ghost.
                         // System.Diagnostics.Debug.WriteLine($"[Health] Potential Ghost: {item.TrackTitle}");
                    }
                }
            }
        }
        catch {}
    }
}
