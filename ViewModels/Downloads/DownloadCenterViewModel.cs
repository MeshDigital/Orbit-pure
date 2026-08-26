using System;
using System.Collections.Generic;
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
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Configuration;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2.5: Global Download Center - Singleton observer that tracks all downloads.
/// Manages Active, Completed, and Failed collections with real-time event subscriptions.
/// </summary>
public class DownloadCenterViewModel : ReactiveObject, IDisposable
{
    private const string NonStrictPreferredFormats = "flac,wav,aiff,aif,mp3";
    private const int NonStrictMinBitrate = 320;
    private const int NonStrictMaxBitrate = 0;
    private const int NonStrictSearchResponseLimit = 300;
    private const int NonStrictSearchFileLimit = 300;
    private const int NonStrictMaxPeerQueueLength = 200;

    private const string StrictPreferredFormats = "flac,wav,aiff,aif";
    private const int StrictMinBitrate = 320;
    private const int StrictMaxBitrate = 0;
    private const int StrictSearchResponseLimit = 200;
    private const int StrictSearchFileLimit = 200;
    private const int StrictMaxPeerQueueLength = 120;

    private const string StricterPreferredFormats = "flac";
    private const int StricterMinBitrate = 701;
    private const int StricterMaxBitrate = 0;
    private const int StricterSearchResponseLimit = 100;
    private const int StricterSearchFileLimit = 100;
    private const int StricterMaxPeerQueueLength = 50;

    private readonly DownloadManager _downloadManager;
    private readonly IEventBus _eventBus;
    private readonly AppConfig _config;
    private readonly DatabaseService _databaseService;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly object _logsLock = new();
    public ObservableCollection<EngineLogEntry> EngineLogs { get; } = new();
    public int EngineLogCount => EngineLogs.Count;
    private DispatcherTimer? _uiBatchTimer;
    private DispatcherTimer? _statusBannerTimer;
    private bool _hasPendingUiRefresh;
    private bool _isApplyingDownloadProfile;
    private string? _globalStatusContext;
    
    // Collections (DynamicData Source)
    private readonly SourceCache<UnifiedTrackViewModel, string> _downloadsSource = new(x => x.GlobalId);

    // Shared, throttled text-only search filter (SearchText, no status component) — both the Hub
    // row projection and the "group by playlist" pipeline subscribe to this same hot observable
    // instead of each debouncing SearchText independently.
    private IObservable<Func<UnifiedTrackViewModel, bool>> _textOnlyFilter = null!;

    // These three drive ActiveCount/CompletedTodayCount/FailedCount only — kept private. The public
    // per-row surface for the UI is the Hub projection below (HubActiveRows/AttentionRows/HubCompletedRows).
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _activeDownloads;
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _completedDownloads;
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _failedDownloads;

    // Download Center v2 (Slice 1): stable projection rows for future card-based layout.
    private readonly ReadOnlyObservableCollection<DownloadRowViewModel> _hubRows;
    public ReadOnlyObservableCollection<DownloadRowViewModel> HubRows => _hubRows;

    private readonly ReadOnlyObservableCollection<DownloadRowViewModel> _hubActiveRows;
    public ReadOnlyObservableCollection<DownloadRowViewModel> HubActiveRows => _hubActiveRows;

    private readonly ReadOnlyObservableCollection<DownloadRowViewModel> _hubAttentionRows;
    public ReadOnlyObservableCollection<DownloadRowViewModel> HubAttentionRows => _hubAttentionRows;

    private readonly ReadOnlyObservableCollection<DownloadRowViewModel> _hubCompletedRows;
    public ReadOnlyObservableCollection<DownloadRowViewModel> HubCompletedRows => _hubCompletedRows;

    public ObservableCollection<DownloadRowViewModel> HubCompletedRecentRows { get; } = new();

    /// <summary>Playlists that still have Missing tracks — start their downloads from here.</summary>
    public ObservableCollection<MissingPlaylistSummaryViewModel> MissingPlaylists { get; } = new();

    /// <summary>
    /// Tracks GhostAcquisitionOrchestrator gave up searching for (TrackStatus.OnHold, after 3
    /// failed background attempts), with their source playlist — otherwise these are invisible
    /// except lumped into the "All" row filter.
    /// </summary>
    public ObservableCollection<UnfindableTrackViewModel> UnfindableTracks { get; } = new();

    /// <summary>
    /// The Attention tab's row source — merges HubAttentionRows (runtime Failed/Stalled/Cancelled,
    /// session-scoped) with UnfindableTracks (persisted OnHold, can outlive the session) into one
    /// filtered, sorted list. These used to be two unrelated UI sections with unrelated filtering
    /// (SearchText never touched UnfindableTracks at all) even though both answer the same user
    /// question: "why can't ORBIT find this track." See RebuildAttentionRows.
    /// </summary>
    public ObservableCollection<IHubRowDisplay> AttentionRows { get; } = new();

    private bool _showAllCompleted;
    /// <summary>False = recent completed only; true = the full completed history inline —
    /// replaces the separate "Completed History" tab so the Hub is the single source of truth.</summary>
    public bool ShowAllCompleted
    {
        get => _showAllCompleted;
        set
        {
            this.RaiseAndSetIfChanged(ref _showAllCompleted, value);
            this.RaisePropertyChanged(nameof(VisibleCompletedRows));
        }
    }

    /// <summary>Recent-20 or the full completed set, depending on <see cref="ShowAllCompleted"/>.</summary>
    public IEnumerable<DownloadRowViewModel> VisibleCompletedRows
        => ShowAllCompleted ? _hubCompletedRows : HubCompletedRecentRows;

    // Phase 2: Active Groups (Album-Centric)
    private readonly ReadOnlyObservableCollection<DownloadGroupViewModel> _activeGroups;
    public ReadOnlyObservableCollection<DownloadGroupViewModel> ActiveGroups => _activeGroups;

    // Beta 2026: Peer Lane Dashboard — group active tracks by source peer
    private readonly ReadOnlyObservableCollection<PeerLaneViewModel> _byPeerGroups;
    public ReadOnlyObservableCollection<PeerLaneViewModel> ByPeerGroups => _byPeerGroups;

    // Ongoing vs Queued Split — kept private; only used to drive DownloadingCount/QueuedCount below,
    // never exposed as a public bound collection (that role belongs to the Hub rows).
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _ongoingDownloads;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _queuedDownloads;

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

    private int _remoteQueuedCount;
    public int RemoteQueuedCount
    {
        get => _remoteQueuedCount;
        set => this.RaiseAndSetIfChanged(ref _remoteQueuedCount, value);
    }

    // Unfiltered totals — not affected by search text, used by the metric cards
    private int _totalCompletedCount;
    public int TotalCompletedCount
    {
        get => _totalCompletedCount;
        set => this.RaiseAndSetIfChanged(ref _totalCompletedCount, value);
    }

    private int _totalFailedCount;
    public int TotalFailedCount
    {
        get => _totalFailedCount;
        set => this.RaiseAndSetIfChanged(ref _totalFailedCount, value);
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

    // Session-scoped only — no persistence needed. Toggles the Active tab between the flat
    // HubActiveRows/HubDownloadRowTemplate list and the ActiveGroups/DownloadGroupTemplate
    // per-playlist view.
    private bool _groupActiveByPlaylist;
    public bool GroupActiveByPlaylist
    {
        get => _groupActiveByPlaylist;
        set => this.RaiseAndSetIfChanged(ref _groupActiveByPlaylist, value);
    }

    // Active / Attention / Completed tabs — replaces the old 7-stacked-section shared-scroll
    // layout. Each tab owns its own scoped ScrollViewer for real virtualization.
    private int _selectedDownloadTab;
    public int SelectedDownloadTab
    {
        get => _selectedDownloadTab;
        set => this.RaiseAndSetIfChanged(ref _selectedDownloadTab, value);
    }

    // Quick status filter — narrows the Active tab only (Searching/Downloading/All). Failed/
    // Completed used to be chip values here too, but now that Attention and Completed are their
    // own tabs, a chip that could also silently empty those tabs was redundant and confusing —
    // removed. Never affects Attention/Completed or the "group by playlist" view.
    private string _rowStatusFilter = "All";
    public string RowStatusFilter
    {
        get => _rowStatusFilter;
        private set
        {
            if (_rowStatusFilter != value)
            {
                this.RaiseAndSetIfChanged(ref _rowStatusFilter, value);
                this.RaisePropertyChanged(nameof(RowStatusFilterAll));
                this.RaisePropertyChanged(nameof(RowStatusFilterSearching));
                this.RaisePropertyChanged(nameof(RowStatusFilterDownloading));
                ApplyRowStatusFilterBanner();
            }
        }
    }

    public bool RowStatusFilterAll
    {
        get => RowStatusFilter == "All";
        set { if (value) RowStatusFilter = "All"; }
    }

    public bool RowStatusFilterSearching
    {
        get => RowStatusFilter == "Searching";
        set => RowStatusFilter = value ? "Searching" : "All";
    }

    public bool RowStatusFilterDownloading
    {
        get => RowStatusFilter == "Downloading";
        set => RowStatusFilter = value ? "Downloading" : "All";
    }

    public static bool MatchesRowStatusFilter(UnifiedTrackViewModel track, string filter) => filter switch
    {
        "Searching" => track.State == PlaylistTrackState.Searching,
        // Mirrors IsActive (minus Searching, which has its own chip above) so a track waiting
        // for a download slot or a peer connection doesn't vanish from every chip except "All".
        "Downloading" => track.State == PlaylistTrackState.Downloading
            || track.State == PlaylistTrackState.Queued
            || track.State == PlaylistTrackState.WaitingForConnection,
        "Failed" => track.State == PlaylistTrackState.Failed || track.State == PlaylistTrackState.Stalled || track.State == PlaylistTrackState.Cancelled,
        "Completed" => track.State == PlaylistTrackState.Completed,
        _ => true,
    };

    // SessionFilterMode and the whole parallel session-ledger filter system it drove were deleted
    // here — confirmed zero XAML bindings anywhere (superseded entirely by RowStatusFilter/the Hub
    // row projection below), but the live subscriptions kept running every frame regardless.

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
        set
        {
            if (this.RaiseAndSetIfChanged(ref _isGlobalStatusVisible, value))
            {
                this.RaisePropertyChanged(nameof(IsGlobalStatusInfoVisible));
                this.RaisePropertyChanged(nameof(IsGlobalStatusErrorVisible));
            }
        }
    }

    private bool _isGlobalStatusError;
    public bool IsGlobalStatusError
    {
        get => _isGlobalStatusError;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _isGlobalStatusError, value))
            {
                this.RaisePropertyChanged(nameof(IsGlobalStatusInfoVisible));
                this.RaisePropertyChanged(nameof(IsGlobalStatusErrorVisible));
            }
        }
    }

    public bool IsGlobalStatusInfoVisible => IsGlobalStatusVisible && !IsGlobalStatusError;
    public bool IsGlobalStatusErrorVisible => IsGlobalStatusVisible && IsGlobalStatusError;

    private bool _showEngineLogs;
    public bool ShowEngineLogs
    {
        get => _showEngineLogs;
        set => this.RaiseAndSetIfChanged(ref _showEngineLogs, value);
    }

    public ICommand ToggleLogsCommand { get; }
    public ICommand ClearSecurityQualityLogsCommand { get; }
    public ICommand DismissGlobalStatusCommand { get; }
    public ICommand ClearHubSelectionCommand { get; }
    
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

    // Hub row selection — drives the Downloads Hub side panel
    private DownloadRowViewModel? _selectedHubRow;
    public DownloadRowViewModel? SelectedHubRow
    {
        get => _selectedHubRow;
        set => this.RaiseAndSetIfChanged(ref _selectedHubRow, value);
    }

    // Phase 12.8: Track Inspector / Slide-out Panel
    private UnifiedTrackViewModel? _selectedTrack;
    public UnifiedTrackViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectedTrack, value);
            if (value != null)
            {
                ReactiveUI.MessageBus.Current.SendMessage(SLSKDONET.Events.OpenInspectorEvent.Create(value, "Downloads.Selection.Single"));
            }
        }
    }

    public bool IsMp3FallbackEnabled
    {
        get => _config.EnableMp3Fallback;
        set
        {
            if (_config.EnableMp3Fallback != value)
            {
                _config.EnableMp3Fallback = value;
                this.RaisePropertyChanged(nameof(IsMp3FallbackEnabled));
                
                // Persist the change
                if (App.Current is App app && app.Services != null)
                {
                    var configManager = app.Services.GetService(typeof(ConfigManager)) as ConfigManager;
                    configManager?.Save(_config);
                }
            }
        }
    }


    public bool HasAnyActiveOrQueued => ActiveCount > 0 || QueuedCount > 0;

    // Beta 2026: Network Health Indicator (fed by Parent Health Monitor)
    private string? _networkHealthMessage;
    public string? NetworkHealthMessage
    {
        get => _networkHealthMessage;
        set => this.RaiseAndSetIfChanged(ref _networkHealthMessage, value);
    }

    private bool _showNetworkHealthWarning;
    public bool ShowNetworkHealthWarning
    {
        get => _showNetworkHealthWarning;
        set => this.RaiseAndSetIfChanged(ref _showNetworkHealthWarning, value);
    }

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

    public bool EnableMp3Fallback
    {
        get => _downloadManager.EnableMp3Fallback;
        set 
        {
             if (_downloadManager.EnableMp3Fallback != value)
             {
                 _downloadManager.EnableMp3Fallback = value;
                 this.RaisePropertyChanged();
             }
        }
    }
    
    public bool DownloadProfileNonStrict

    {
        get => IsNonStrictActive();
        set
        {
            if (!value || _isApplyingDownloadProfile)
                return;

            ApplyDownloadProfile("NonStrict");
        }
    }

    public bool DownloadProfileStrict
    {
        get => IsStrictActive();
        set
        {
            if (!value || _isApplyingDownloadProfile)
                return;

            ApplyDownloadProfile("Strict");
        }
    }

    public bool DownloadProfileStricter
    {
        get => IsStricterActive();
        set
        {
            if (!value || _isApplyingDownloadProfile)
                return;

            ApplyDownloadProfile("Stricter");
        }
    }

    public string DownloadProfileModeText => DownloadProfileStricter
        ? "STRICTER overwrite: FLAC-only + 701kbps floor"
        : DownloadProfileStrict
            ? "STRICT overwrite: FLAC/WAV/AIFF/AIF + 320kbps floor"
            : "NON-STRICT overwrite: expanded formats + 320kbps floor";

    // ── #140 Batch Profile ────────────────────────────────────────────────────
    private BatchProfile _selectedBatchProfile = BatchProfile.DJSetPrep;

    /// <summary>
    /// Switches concurrency + quality floor as a single preset.
    /// DJSetPrep   = 3 concurrent, strict quality filter.
    /// Archival    = 1 concurrent, strictest quality filter (lossless only).
    /// QuickPreview= 8 concurrent, non-strict quality filter.
    /// </summary>
    public BatchProfile SelectedBatchProfile
    {
        get => _selectedBatchProfile;
        set
        {
            if (_selectedBatchProfile == value) return;
            this.RaiseAndSetIfChanged(ref _selectedBatchProfile, value);
            ApplyBatchProfile(value);
        }
    }

    private void ApplyBatchProfile(BatchProfile profile)
    {
        switch (profile)
        {
            case BatchProfile.DJSetPrep:
                MaxConcurrentDownloads = 3;
                if (!DownloadProfileStrict) ApplyDownloadProfile("Strict");
                break;
            case BatchProfile.Archival:
                MaxConcurrentDownloads = 1;
                if (!DownloadProfileStricter) ApplyDownloadProfile("Stricter");
                break;
            case BatchProfile.QuickPreview:
                MaxConcurrentDownloads = 8;
                if (!DownloadProfileNonStrict) ApplyDownloadProfile("NonStrict");
                break;
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
    public ICommand BulkRevealCommand { get; }
    public ICommand CancelSelectedCommand { get; }
    public ICommand PauseSelectedCommand { get; }
    public ICommand ResumeSelectedCommand { get; }

    // Master Commands
    public ICommand StartEngineCommand { get; }
    public ICommand StopEngineCommand { get; }
    public ICommand ToggleEnginePauseCommand { get; }
    public ICommand ResetDownloadCenterCommand { get; }
    public ICommand CancelAllActiveCommand { get; }
    
    private readonly ArtworkCacheService _artworkCache;
    private readonly ILibraryService _libraryService;

    public DownloadCenterViewModel(
        DownloadManager downloadManager,
        IEventBus eventBus,
        AppConfig config,
        ArtworkCacheService artworkCache,
        ILibraryService libraryService,
        DatabaseService databaseService,
        IDbContextFactory<AppDbContext> dbFactory,
        IDialogService dialogService,
        INotificationService notificationService)
    {
        _downloadManager = downloadManager;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _libraryService = libraryService;
        _databaseService = databaseService;
        _config = config;
        _dbFactory = dbFactory;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _isAutoEnrichEnabled = _config.IsAutoEnrichEnabled;
        
        ToggleLogsCommand = ReactiveCommand.Create(() => ShowEngineLogs = !ShowEngineLogs);
        ClearSecurityQualityLogsCommand = ReactiveCommand.Create(() => { });
        DismissGlobalStatusCommand = ReactiveCommand.Create(() => ClearGlobalStatus());
        ClearHubSelectionCommand = ReactiveCommand.Create(() => SelectedHubRow = null);
        
        ResetDownloadCenterCommand = ReactiveCommand.CreateFromTask(ExecuteResetDownloadCenterAsync);
        CancelAllActiveCommand = ReactiveCommand.Create(ExecuteCancelAllActive);

        // Phase 6: Security & Quality diagnostics feed (Shield / Gate visibility)
        _eventBus.GetEvent<SecurityAuditEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                // SecurityQualityLogs removed
            })
            .DisposeWith(_subscriptions);

        // Propagate playlist renames to DownloadGroupViewModel titles so the Download
        // Center reflects the new name without requiring a session restart.
        _eventBus.GetEvent<ProjectUpdatedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
            {
                var newName = _downloadManager.GetPlaylistSourceName(evt.ProjectId);
                if (string.IsNullOrEmpty(newName)) return;
                foreach (var grp in _activeGroups)
                {
                    if (grp.GroupKey == evt.ProjectId)
                        grp.Title = newName;
                }
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

        // Sync manager state — use Observable so the subscription is tracked and disposed with the VM
        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => _downloadManager.PropertyChanged += h,
                h => _downloadManager.PropertyChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pattern =>
            {
                var e = pattern.EventArgs;
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
            })
            .DisposeWith(_subscriptions);
        
        ClearCompletedCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var completedItems = _downloadsSource.Items
                .Where(x => x.IsCompleted && !x.IsClearedFromDownloadCenter)
                .ToList();

            // Soft-clear: flip the VM flag — DynamicData AutoRefresh + !IsClearedFromDownloadCenter
            // filter removes the row from the session ledger without destroying the VM.
            foreach (var item in completedItems)
            {
                item.IsClearedFromDownloadCenter = true;
                item.Model.IsClearedFromDownloadCenter = true;
                await _libraryService.UpdatePlaylistTrackAsync(item.Model);
            }
        });
        
        ClearFailedCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var failedItems = _downloadsSource.Items
            .Where(x => (x.IsFailed || x.IsStalled || x.State == PlaylistTrackState.Cancelled) && !x.IsClearedFromDownloadCenter)
                .ToList();

            // Soft-clear: flip the VM flag — DynamicData AutoRefresh + !IsClearedFromDownloadCenter
            // filter removes the row from the session ledger without destroying the VM.
            foreach (var item in failedItems)
            {
                item.IsClearedFromDownloadCenter = true;
                item.Model.IsClearedFromDownloadCenter = true;
                await _libraryService.UpdatePlaylistTrackAsync(item.Model);
            }
        });

        RetryAllFailedCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var failedItems = _failedDownloads.ToList();
            for (var i = 0; i < failedItems.Count; i++)
            {
                // Stagger retries to avoid a search storm that pushes active search count
                // past the Critical threshold (5+) and locks variationCap to 1.
                // 500ms between each retry → at MaxConcurrentSearches=5, we naturally
                // spread load without hammering the Soulseek search network all at once.
                if (i > 0)
                    await Task.Delay(500);

                await _downloadManager.HardRetryTrack(failedItems[i].GlobalId);
            }
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
                if (!item.IsCompleted && !item.IsFailed)
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
        
        BulkRevealCommand = ReactiveCommand.Create(() => 
        {
            var selectedArgs = SelectedItems.Where(x => x.IsCompleted).ToList();
            foreach (var item in selectedArgs)
            {
                var path = item.Model.ResolvedFilePath;
                if (!string.IsNullOrEmpty(path))
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
                }
            }
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
            .AutoRefresh(x => x.PeerName)
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

        // Download Center v2 (Slice 1): unified row projection for future card-based hub.
        var hubRowComparer = SortExpressionComparer<DownloadRowViewModel>
            .Ascending(x => x.Priority)
            .ThenByDescending(x => x.LastUpdatedUtc);

        // Text-only search filter — deliberately has NO RowStatusFilter component, so SearchText
        // applies uniformly to every tab (Active/Attention/Completed) instead of only the section
        // RowStatusFilter happened to be scoped to. This is the fix for search feeling inconsistent
        // across the page: it used to gate _hubRows upstream of the 3-way Active/Attention/Completed
        // split combined with the status chips, so e.g. clicking "Failed" silently emptied Completed
        // too. RowStatusFilter is now applied only within the Active tab's own filter chain below.
        _textOnlyFilter = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Select<string?, Func<UnifiedTrackViewModel, bool>>(text =>
            {
                // Accent-insensitive, same rationale as BuildFilter: typing "beyonce" must still
                // find "Beyoncé" — computed once per filter change, not per track, for efficiency.
                var normalizedSearch = string.IsNullOrWhiteSpace(text) ? null : StripDiacritics(text);
                return track =>
                    normalizedSearch == null
                    || StripDiacritics(track.TrackTitle).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                    || StripDiacritics(track.ArtistName).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase);
            })
            .Replay(1)
            .RefCount();

        sharedSource
            .Filter(x => !x.IsClearedFromDownloadCenter)
            .Filter(_textOnlyFilter)
            .Transform(x => new DownloadRowViewModel(x, row => SelectedHubRow = row))
            .DisposeMany() // rows subscribe to their track's PropertyChanged — unhook on removal
            .AutoRefresh(x => x.Priority) // re-sort when a row's status flips its section
            .SortAndBind(out _hubRows, hubRowComparer)
            .Subscribe()
            .DisposeWith(_subscriptions);

        // RowStatusFilter (Searching/Downloading/All — Failed/Completed removed, tabs do that job
        // now) narrows the Active tab only, so it never silently empties Attention/Completed.
        var activeRowStatusFilter = this.WhenAnyValue(x => x.RowStatusFilter)
            .Select<string, Func<DownloadRowViewModel, bool>>(statusFilter =>
                row => MatchesRowStatusFilter(row.Track, statusFilter));

        // AutoRefresh(Priority) on each section: without it a row that transitions
        // (e.g. Downloading → Completed) stays stuck in its original section forever.
        // AutoRefresh(Status) additionally: Searching→Pending (a failed search attempt
        // going back to the retry queue) doesn't change Priority at all — both map to
        // DownloadRowPriority.Active — so without watching Status directly, a row that
        // finished searching (no results) and moved to Pending stayed stuck showing the
        // "Searching" badge and its last stale status message under the Searching chip
        // filter forever, even though DownloadManager had already moved it to Pending.
        _hubRows.ToObservableChangeSet()
            .AutoRefresh(x => x.Priority)
            .AutoRefresh(x => x.Status)
            .Filter(x => x.Priority == DownloadRowPriority.Active)
            .Filter(activeRowStatusFilter)
            .Bind(out _hubActiveRows)
            .Subscribe()
            .DisposeWith(_subscriptions);

        _hubRows.ToObservableChangeSet()
            .AutoRefresh(x => x.Priority)
            .Filter(x => x.Priority == DownloadRowPriority.Attention)
            .Bind(out _hubAttentionRows)
            .Subscribe()
            .DisposeWith(_subscriptions);

        _hubAttentionRows.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RebuildAttentionRows())
            .DisposeWith(_subscriptions);

        // SearchText changes UnfindableTracks' own filtered contribution to AttentionRows (the
        // HubAttentionRows side is already covered by the subscription above, since _textOnlyFilter
        // changing re-fires _hubRows itself).
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RebuildAttentionRows())
            .DisposeWith(_subscriptions);

        _hubRows.ToObservableChangeSet()
            .AutoRefresh(x => x.Priority)
            .Filter(x => x.Priority == DownloadRowPriority.Completed)
            .Bind(out _hubCompletedRows)
            .Subscribe()
            .DisposeWith(_subscriptions);

        _hubCompletedRows.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshHubCompletedRecentRows())
            .DisposeWith(_subscriptions);

        // 1.1 Ongoing Downloads (Downloading/Searching state)
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Downloading || x.State == PlaylistTrackState.Searching || x.State == PlaylistTrackState.Queued)
            .SortAndBind(out _ongoingDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.State == PlaylistTrackState.Downloading).ThenByDescending(x => x.DownloadSpeed))
            .Subscribe(_ => {
                DownloadingCount = _downloadsSource.Items.Count(x => x.State == PlaylistTrackState.Downloading);
                SearchingCount = _downloadsSource.Items.Count(x => x.State == PlaylistTrackState.Searching);
                RemoteQueuedCount = _downloadsSource.Items.Count(x => x.State == PlaylistTrackState.Queued);
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

        // "Group by playlist" pipeline for the Active tab — widened to include IsActive (a playlist
        // with a track actively downloading right now used to be excluded from its own group card)
        // and now shares _textOnlyFilter so SearchText actually narrows grouped view too (previously
        // this pipeline had no SearchText awareness at all — the same class of bug as the Hub rows
        // fix above, just in the grouped-view sibling). Also applies RowStatusFilter (All/Searching/
        // Downloading) directly to UnifiedTrackViewModel — the flat view's chips filter DownloadRowViewModel
        // via activeRowStatusFilter above, but that filter never touched this pipeline at all, so
        // clicking "Searching" while "Group by playlist" was on silently did nothing.
        var activeGroupRowStatusFilter = this.WhenAnyValue(x => x.RowStatusFilter)
            .Select<string, Func<UnifiedTrackViewModel, bool>>(statusFilter =>
                track => MatchesRowStatusFilter(track, statusFilter));

        sharedSource
            .Filter(x => x.IsActive || x.IsWaiting || x.IsStalled)
            .Filter(_textOnlyFilter)
            .Filter(activeGroupRowStatusFilter)
            .Group(x => x.Model.SourcePlaylistId ?? x.Model.PlaylistId)
            .Transform((IGroup<UnifiedTrackViewModel, string, Guid> group) => new DownloadGroupViewModel(group, _downloadManager, _libraryService, _notificationService, row => SelectedHubRow = row))
            .DisposeMany()
            .SortAndBind(out _activeGroups, SortExpressionComparer<DownloadGroupViewModel>.Descending(x => x.LastActivity))
            .Subscribe()
            .DisposeWith(_subscriptions);

        // Completed Pipeline
        var completedFilter = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .Select(BuildFilter);

        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Completed && !x.IsClearedFromDownloadCenter)
            .Filter(completedFilter)
            .ObserveOn(RxApp.MainThreadScheduler)
            .SortAndBind(out _completedDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.Model.CompletedAt ?? x.Model.AddedAt))
            .Subscribe()
            .DisposeWith(_subscriptions);

        _completedDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => CompletedTodayCount = _completedDownloads.Count)
            .DisposeWith(_subscriptions);

        // Failed Pipeline
        sharedSource
            .Filter(x => (x.State == PlaylistTrackState.Failed || x.State == PlaylistTrackState.Cancelled || x.State == PlaylistTrackState.Stalled) && !x.IsClearedFromDownloadCenter)
            .Filter(completedFilter)
            .ObserveOn(RxApp.MainThreadScheduler)
            .SortAndBind(out _failedDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.Model.CompletedAt ?? x.Model.AddedAt))
            .Subscribe()
            .DisposeWith(_subscriptions);

        _failedDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => FailedCount = _failedDownloads.Count)
            .DisposeWith(_subscriptions);

        // Unfiltered metric card totals — independent of SearchText so the cards never drop counts
        // when the user types in the search box. These mirror the filtered pipelines above but skip
        // the completedFilter so they always reflect the true session totals.
        _downloadsSource.Connect()
            .AutoRefresh(x => x.State)
            .AutoRefresh(x => x.IsClearedFromDownloadCenter)
            .Filter(x => x.State == PlaylistTrackState.Completed && !x.IsClearedFromDownloadCenter)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => TotalCompletedCount = _downloadsSource.Items.Count(x =>
                x.State == PlaylistTrackState.Completed && !x.IsClearedFromDownloadCenter))
            .DisposeWith(_subscriptions);

        _downloadsSource.Connect()
            .AutoRefresh(x => x.State)
            .AutoRefresh(x => x.IsClearedFromDownloadCenter)
            .Filter(x => (x.State == PlaylistTrackState.Failed ||
                          x.State == PlaylistTrackState.Cancelled ||
                          x.State == PlaylistTrackState.Stalled) && !x.IsClearedFromDownloadCenter)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => TotalFailedCount = _downloadsSource.Items.Count(x =>
                (x.State == PlaylistTrackState.Failed ||
                 x.State == PlaylistTrackState.Cancelled ||
                 x.State == PlaylistTrackState.Stalled) && !x.IsClearedFromDownloadCenter))
            .DisposeWith(_subscriptions);

        sharedSource.Connect(); // Connect the publisher

        // Beta 2026: Peer Lane Dashboard — group downloading/searching tracks by their peer name
        sharedSource
            .Filter(x => x.IsActive && !string.IsNullOrWhiteSpace(x.PeerName))
            .Group(GetPeerLaneGroupKey)
            .Transform((IGroup<UnifiedTrackViewModel, string, string> group) => new PeerLaneViewModel(group))
            .DisposeMany()
            .SortAndBind(out _byPeerGroups,
                SortExpressionComparer<PeerLaneViewModel>.Descending(x => x.TotalSpeed))
            .Subscribe()
            .DisposeWith(_subscriptions);

        // Beta 2026: Network health warning from Parent Health Monitor
        _eventBus.GetEvent<NetworkHealthWarningEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                NetworkHealthMessage = e.Message;
                ShowNetworkHealthWarning = true;
            })
            .DisposeWith(_subscriptions);

        // Subscribe to creation events ONLY (State/Progress handled by Smart Component)
        _eventBus.GetEvent<TrackAddedEvent>()
            .Subscribe(OnTrackAdded)
            .DisposeWith(_subscriptions);

        // Live Log Engine Subscription
        _eventBus.GetEvent<TrackStateChangedEvent>()
            .Subscribe(OnTrackStateChanged)
            .DisposeWith(_subscriptions);
        
        // Issue #4: Subscribe to batch track additions from imports
        _eventBus.GetEvent<BatchTracksAddedEvent>()
            .Subscribe(OnBatchTracksAdded)
            .DisposeWith(_subscriptions);
            
        // Used to catch removals (e.g. Delete command from within VM)
        _eventBus.GetEvent<TrackRemovedEvent>()
             .Subscribe(OnTrackRemoved)
             .DisposeWith(_subscriptions);

        _eventBus.GetEvent<GlobalStatusEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ApplyGlobalStatusEvent)
            .DisposeWith(_subscriptions);

        _eventBus.GetEvent<FileIngestionQueuedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
                ShowGlobalStatus(
                    $"Ingestion pending: {BuildIngestionDisplayName(evt.FilePath, evt.TrackUniqueHash)}",
                    isError: false,
                    autoHide: true,
                    context: "ingestion-lifecycle"))
            .DisposeWith(_subscriptions);

        _eventBus.GetEvent<FileIngestionStartedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
                ShowGlobalStatus(
                    $"Indexing: {BuildIngestionDisplayName(evt.FilePath, evt.TrackUniqueHash)}",
                    isError: false,
                    autoHide: true,
                    context: "ingestion-lifecycle"))
            .DisposeWith(_subscriptions);

        _eventBus.GetEvent<FileIngestionCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
                ShowGlobalStatus(
                    $"Ready: {BuildIngestionDisplayName(evt.FilePath, evt.TrackUniqueHash)}",
                    isError: false,
                    autoHide: true,
                    context: "ingestion-lifecycle"))
            .DisposeWith(_subscriptions);

        _eventBus.GetEvent<FileMissingDetectedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
                ShowGlobalStatus(
                    $"Missing file detected: {BuildIngestionDisplayName(evt.FilePath, evt.TrackUniqueHash)}",
                    isError: true,
                    autoHide: false,
                    context: "ingestion-lifecycle"))
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
            .Subscribe(_ => _hasPendingUiRefresh = true)
            .DisposeWith(_subscriptions);
        
        // Start global speed calculator
        StartGlobalSpeedTimer();

        RefreshHubCompletedRecentRows();

        // Playlists-with-missing panel: initial load, then refresh (throttled) whenever
        // completions land so counts stay honest.
        _ = RefreshMissingPlaylistsAsync();
        _ = RefreshUnfindableTracksAsync();
        _hubCompletedRows.ToObservableChangeSet()
            .Throttle(TimeSpan.FromSeconds(3))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(changes =>
            {
                var fireAndForget = RefreshMissingPlaylistsAsync();
                var fireAndForgetUnfindable = RefreshUnfindableTracksAsync();
            })
            .DisposeWith(_subscriptions);

        // Phase 3.7: Defensive Hydration - Catch up if Manager already finished while we were initializing
        if (_downloadManager.IsHydrated)
        {
            Serilog.Log.Information("DownloadCenterViewModel: Manager already hydrated, performing immediate hydration...");
            InitialHydration();
        }
        
    }

    private void RefreshHubCompletedRecentRows()
    {
        HubCompletedRecentRows.Clear();
        foreach (var row in _hubCompletedRows.Take(20))
        {
            HubCompletedRecentRows.Add(row);
        }
    }

    /// <summary>
    /// Rebuilds AttentionRows from HubAttentionRows + UnfindableTracks. HubAttentionRows is
    /// already text-filtered upstream (via _textOnlyFilter on _hubRows), so only UnfindableTracks
    /// needs its own SearchText check here — this is the fix for "the search box doesn't affect
    /// Unfindable Tracks," which previously had zero SearchText awareness at all.
    /// </summary>
    private void RebuildAttentionRows()
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(SearchText) ? null : StripDiacritics(SearchText);
        var unfindableFiltered = normalizedSearch == null
            ? UnfindableTracks.AsEnumerable()
            : UnfindableTracks.Where(t => StripDiacritics(t.DisplayName).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase));

        AttentionRows.Clear();
        // HubAttentionRows first (already sorted newest-activity-first); Unfindable rows after
        // (already sorted by playlist title, then artist, from RefreshUnfindableTracksAsync).
        foreach (var row in _hubAttentionRows) AttentionRows.Add(row);
        foreach (var row in unfindableFiltered) AttentionRows.Add(row);
    }

    /// <summary>
    /// Rebuilds the "Playlists with missing tracks" panel from live DB counts
    /// (persisted PlaylistJob.MissingCount can drift, so count for real).
    /// </summary>
    public async System.Threading.Tasks.Task RefreshMissingPlaylistsAsync()
    {
        try
        {
            List<(Guid PlaylistId, int Missing, int Total)> counts;
            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                counts = (await db.PlaylistTracks
                        .AsNoTracking()
                        .GroupBy(t => t.PlaylistId)
                        .Select(g => new
                        {
                            PlaylistId = g.Key,
                            Missing = g.Count(t => t.Status == TrackStatus.Missing),
                            Total = g.Count(),
                        })
                        .Where(x => x.Missing > 0)
                        .ToListAsync())
                    .Select(x => (x.PlaylistId, x.Missing, x.Total))
                    .ToList();
            }

            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();
            var titleById = jobs.ToDictionary(j => j.Id, j => j.SourceTitle);

            var queuedPlaylistIds = _downloadsSource.Items
                .Where(x => x.IsActive || x.IsWaiting)
                .Select(x => x.Model.SourcePlaylistId ?? x.Model.PlaylistId)
                .Where(id => id != Guid.Empty)
                .ToHashSet();

            var summaries = counts
                .Where(c => titleById.ContainsKey(c.PlaylistId))
                .OrderByDescending(c => c.Missing)
                .Select(c =>
                {
                    var vm = new MissingPlaylistSummaryViewModel(
                        c.PlaylistId,
                        titleById[c.PlaylistId],
                        c.Missing,
                        c.Total,
                        StartMissingPlaylistAsync);
                    vm.IsQueued = queuedPlaylistIds.Contains(c.PlaylistId);
                    return vm;
                })
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MissingPlaylists.Clear();
                foreach (var summary in summaries)
                {
                    MissingPlaylists.Add(summary);
                }
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to refresh missing-playlists panel");
        }
    }

    /// <summary>
    /// Queues every Missing track of the given playlist — same path as the Library page's
    /// "Download Missing" context-menu action, now reachable directly from the Download Center.
    /// </summary>
    private async System.Threading.Tasks.Task StartMissingPlaylistAsync(MissingPlaylistSummaryViewModel playlist, PlaylistPriority priority)
    {
        // Set before either await below: SetJobPriorityAsync and LoadPlaylistTracksAsync are both
        // full DB round-trips, so without this the row gives zero visual feedback for the whole
        // duration and a click reads as not having registered at all.
        playlist.IsStarting = true;
        ShowGlobalStatus($"Looking up missing tracks in \"{playlist.Title}\"...", isError: false, autoHide: true, context: "missing-playlists");

        try
        {
            await _downloadManager.SetJobPriorityAsync(playlist.PlaylistId, priority);

            var tracks = await _libraryService.LoadPlaylistTracksAsync(playlist.PlaylistId);
            var missing = tracks
                .Where(t => t.Status != TrackStatus.Downloaded && t.Status != TrackStatus.OnHold)
                .ToList();

            if (missing.Count == 0)
            {
                ShowGlobalStatus($"All tracks in \"{playlist.Title}\" are already downloaded or on hold.", isError: false, autoHide: true, context: "missing-playlists");
                return;
            }

            foreach (var t in missing)
            {
                // Track-level Priority 0 = explicit user action — bypasses lazy-buffer size gate.
                t.Priority = 0;
                if (string.IsNullOrEmpty(t.SourcePlaylistName))
                {
                    t.SourcePlaylistName = playlist.Title;
                    t.SourcePlaylistId = playlist.PlaylistId;
                }
            }

            _downloadManager.QueueTracks(missing);

            var priorityLabel = priority == PlaylistPriority.Normal ? string.Empty : $" [{priority}]";
            ShowGlobalStatus($"Queued {missing.Count} missing track(s) from \"{playlist.Title}\"{priorityLabel}.", isError: false, autoHide: true, context: "missing-playlists");

            playlist.IsQueued = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to start missing downloads for playlist {Playlist}", playlist.Title);
            ShowGlobalStatus($"Failed to queue \"{playlist.Title}\": {ex.Message}", isError: true, autoHide: false, context: "missing-playlists");
        }
        finally
        {
            playlist.IsStarting = false;
        }
    }

    /// <summary>
    /// Rebuilds the "Unfindable Tracks" panel — every track GhostAcquisitionOrchestrator gave up
    /// on (TrackStatus.OnHold) plus its source playlist name, same live-DB-count approach as
    /// <see cref="RefreshMissingPlaylistsAsync"/>.
    /// </summary>
    public async System.Threading.Tasks.Task RefreshUnfindableTracksAsync()
    {
        try
        {
            List<(Guid Id, Guid PlaylistId, string Artist, string Title, int SearchRetryCount)> rows;
            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                rows = (await db.PlaylistTracks
                        .AsNoTracking()
                        .Where(t => t.Status == TrackStatus.OnHold)
                        .Select(t => new { t.Id, t.PlaylistId, t.Artist, t.Title, t.SearchRetryCount })
                        .ToListAsync())
                    .Select(x => (x.Id, x.PlaylistId, x.Artist, x.Title, x.SearchRetryCount))
                    .ToList();
            }

            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();
            var titleById = jobs.ToDictionary(j => j.Id, j => j.SourceTitle);

            var items = rows
                .Where(r => titleById.ContainsKey(r.PlaylistId))
                .OrderBy(r => titleById[r.PlaylistId])
                .ThenBy(r => r.Artist)
                .Select(r => new UnfindableTrackViewModel(
                    r.Id,
                    r.PlaylistId,
                    r.Artist,
                    r.Title,
                    titleById[r.PlaylistId],
                    r.SearchRetryCount,
                    RetryUnfindableTrackAsync))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UnfindableTracks.Clear();
                foreach (var item in items)
                {
                    UnfindableTracks.Add(item);
                }
                RebuildAttentionRows();
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to refresh unfindable-tracks panel");
        }
    }

    /// <summary>
    /// Resets an OnHold track back to Missing with a clean retry count so the next idle sweep (or
    /// a manual "Download Missing") reconsiders it — otherwise OnHold is a one-way trip.
    /// </summary>
    private async System.Threading.Tasks.Task RetryUnfindableTrackAsync(UnfindableTrackViewModel track)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var entity = await db.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == track.PlaylistTrackId);
            if (entity == null)
            {
                ShowGlobalStatus($"\"{track.DisplayName}\" no longer exists.", isError: true, autoHide: true, context: "unfindable-tracks");
                return;
            }

            entity.Status = TrackStatus.Missing;
            entity.SearchRetryCount = 0;
            await db.SaveChangesAsync();

            // The DB flip alone doesn't touch this track's live in-memory DownloadContext — if it
            // was already hydrated this session (OnHold hydrates into Paused), it would otherwise
            // sit inert until the next app restart despite the UI saying "will be retried."
            if (!string.IsNullOrEmpty(entity.TrackUniqueHash))
                await _downloadManager.ResumeTrackAsync(entity.TrackUniqueHash);

            UnfindableTracks.Remove(track);
            ShowGlobalStatus($"\"{track.DisplayName}\" will be retried.", isError: false, autoHide: true, context: "unfindable-tracks");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to retry unfindable track {Track}", track.DisplayName);
            ShowGlobalStatus($"Failed to retry \"{track.DisplayName}\": {ex.Message}", isError: true, autoHide: false, context: "unfindable-tracks");
        }
    }

    private void ApplyDownloadProfile(string mode)
    {
        _isApplyingDownloadProfile = true;
        try
        {
            if (string.Equals(mode, "Stricter", StringComparison.OrdinalIgnoreCase))
            {
                _config.PreferredFormats = StricterPreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                _config.PreferredMinBitrate = StricterMinBitrate;
                _config.PreferredMaxBitrate = StricterMaxBitrate;
                _config.SearchResponseLimit = StricterSearchResponseLimit;
                _config.SearchFileLimit = StricterSearchFileLimit;
                _config.MaxPeerQueueLength = StricterMaxPeerQueueLength;
            }
            else if (string.Equals(mode, "Strict", StringComparison.OrdinalIgnoreCase))
            {
                _config.PreferredFormats = StrictPreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                _config.PreferredMinBitrate = StrictMinBitrate;
                _config.PreferredMaxBitrate = StrictMaxBitrate;
                _config.SearchResponseLimit = StrictSearchResponseLimit;
                _config.SearchFileLimit = StrictSearchFileLimit;
                _config.MaxPeerQueueLength = StrictMaxPeerQueueLength;
            }
            else
            {
                _config.PreferredFormats = NonStrictPreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                _config.PreferredMinBitrate = NonStrictMinBitrate;
                _config.PreferredMaxBitrate = NonStrictMaxBitrate;
                _config.SearchResponseLimit = NonStrictSearchResponseLimit;
                _config.SearchFileLimit = NonStrictSearchFileLimit;
                _config.MaxPeerQueueLength = NonStrictMaxPeerQueueLength;
            }

            this.RaisePropertyChanged(nameof(DownloadProfileNonStrict));
            this.RaisePropertyChanged(nameof(DownloadProfileStrict));
            this.RaisePropertyChanged(nameof(DownloadProfileStricter));
            this.RaisePropertyChanged(nameof(DownloadProfileModeText));

            _eventBus.Publish(new GlobalStatusEvent($"Download profile overwrite applied: {DownloadProfileModeText}", IsActive: true, IsError: false));
        }
        finally
        {
            _isApplyingDownloadProfile = false;
        }
    }

    private bool IsNonStrictActive()
    {
        var formats = NormalizeFormats(_config.PreferredFormats);
        return formats.SetEquals(new HashSet<string>(new[] { "flac", "wav", "aiff", "aif", "mp3" }))
               && _config.PreferredMinBitrate <= NonStrictMinBitrate
               && _config.SearchResponseLimit >= NonStrictSearchResponseLimit
               && _config.SearchFileLimit >= NonStrictSearchFileLimit;
    }

    private bool IsStrictActive()
    {
        var formats = NormalizeFormats(_config.PreferredFormats);
        return formats.SetEquals(new HashSet<string>(new[] { "flac", "wav", "aiff", "aif" }))
               && _config.PreferredMinBitrate >= StrictMinBitrate
               && _config.PreferredMinBitrate < StricterMinBitrate;
    }

    private bool IsStricterActive()
    {
        var formats = NormalizeFormats(_config.PreferredFormats);
        return formats.Count == 1
               && formats.Contains("flac")
               && _config.PreferredMinBitrate >= StricterMinBitrate
               && _config.SearchResponseLimit <= StricterSearchResponseLimit
               && _config.SearchFileLimit <= StricterSearchFileLimit;
    }

    private static HashSet<string> NormalizeFormats(List<string>? formats)
    {
        return (formats ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .ToHashSet();
    }

    private static string GetPeerLaneGroupKey(UnifiedTrackViewModel track)
    {
        return string.IsNullOrWhiteSpace(track.PeerName)
            ? string.Empty
            : track.PeerName.Trim();
    }

    public static string? BuildSessionFilterBanner(string mode, int visibleCount)
    {
        if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{mode} filter active • {visibleCount} visible in the current session.";
    }

    public static bool ShouldAutoDismissGlobalStatus(GlobalStatusEvent evt)
    {
        return evt.IsActive
            && !evt.IsError
            && !string.IsNullOrWhiteSpace(evt.Message)
            && evt.Message.Contains("profile", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyRowStatusFilterBanner()
    {
        var message = BuildSessionFilterBanner(RowStatusFilter, HubActiveRows.Count);
        if (string.IsNullOrWhiteSpace(message))
        {
            ClearGlobalStatus("session-filter");
            return;
        }

        ShowGlobalStatus(message, isError: false, autoHide: false, context: "session-filter");
    }

    private void ApplyGlobalStatusEvent(GlobalStatusEvent evt)
    {
        if (!evt.IsActive || string.IsNullOrWhiteSpace(evt.Message))
        {
            ClearGlobalStatus();
            return;
        }

        ShowGlobalStatus(evt.Message, evt.IsError, ShouldAutoDismissGlobalStatus(evt), evt.IsError ? "global-error" : "global-info");
    }

    private void ShowGlobalStatus(string message, bool isError, bool autoHide, string context)
    {
        _statusBannerTimer?.Stop();
        _globalStatusContext = context;
        GlobalStatusMessage = message;
        IsGlobalStatusError = isError;
        IsGlobalStatusVisible = true;

        if (!autoHide)
        {
            return;
        }

        _statusBannerTimer ??= new DispatcherTimer();
        _statusBannerTimer.Interval = TimeSpan.FromSeconds(3);
        _statusBannerTimer.Tick -= OnStatusBannerTimerTick;
        _statusBannerTimer.Tick += OnStatusBannerTimerTick;
        _statusBannerTimer.Start();
    }

    private void OnStatusBannerTimerTick(object? sender, EventArgs e)
    {
        ClearGlobalStatus();
    }

    private void ClearGlobalStatus(string? context = null)
    {
        if (!string.IsNullOrWhiteSpace(context)
            && !string.Equals(_globalStatusContext, context, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _statusBannerTimer?.Stop();
        GlobalStatusMessage = null;
        IsGlobalStatusVisible = false;
        IsGlobalStatusError = false;
        _globalStatusContext = null;
    }

    private Func<UnifiedTrackViewModel, bool> BuildFilter(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return _ => true;

        // Accent-insensitive: typing "beyonce" must still find "Beyoncé" — the track/artist
        // names routinely carry diacritics that a user's search text won't reproduce.
        var normalizedSearch = StripDiacritics(searchText);

        return vm => StripDiacritics(vm.TrackTitle).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                     StripDiacritics(vm.ArtistName).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase);
    }

    internal static string StripDiacritics(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    internal static string BuildIngestionDisplayName(string? filePath, string? fallbackHash)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return System.IO.Path.GetFileName(filePath);
        }

        return string.IsNullOrWhiteSpace(fallbackHash) ? "unknown track" : fallbackHash;
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
            var viewModel = new UnifiedTrackViewModel(track, _downloadManager, _eventBus, _artworkCache, _libraryService, _databaseService, _config, _dbFactory);

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
    
    /// <summary>
    /// Issue #4: Batch handler for bulk track additions from imports or hydration.
    /// Processes all tracks in one UI update cycle to prevent freeze during large imports.
    /// </summary>
    private void OnBatchTracksAdded(BatchTracksAddedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var (track, initialState) in e.Tracks)
            {
                // Phase 2.5: Create Smart View Model
                var viewModel = new UnifiedTrackViewModel(track, _downloadManager, _eventBus, _artworkCache, _libraryService, _databaseService, _config, _dbFactory);

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
                    .DisposeWith(_subscriptions);

                // Set initial state override if needed
                if (initialState.HasValue)
                {
                    viewModel.State = initialState.Value;
                }
                
                _downloadsSource.AddOrUpdate(viewModel);
            }
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
        _uiBatchTimer?.Stop();
        _uiBatchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };

        _uiBatchTimer.Tick += (_, _) =>
        {
            if (_hasPendingUiRefresh)
            {
                _hasPendingUiRefresh = false;
                RefreshGlobalSpeed();
            }

            // Phase 12.3: Slot Health Check (Piggyback on 1s timer)
            CheckSlotHealth();
        };

        _uiBatchTimer.Start();
    }

    private void RefreshGlobalSpeed()
    {
        var totalSpeedBytes = _activeDownloads
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

    private void OnTrackStateChanged(TrackStateChangedEvent e)
    {
        string artist = "Unknown Artist";
        string title = "Unknown Title";
        
        var trackVm = _downloadsSource.Items.FirstOrDefault(x => x.GlobalId == e.TrackGlobalId);
        if (trackVm != null)
        {
            artist = trackVm.ArtistName;
            title = trackVm.TrackTitle;
        }
        
        string level = "INFO";
        string stage = "ENGINE";
        string message = "";
        
        switch (e.State)
        {
            case PlaylistTrackState.Searching:
                level = "INFO";
                stage = "SEARCH";
                message = $"Started searching for '{artist} - {title}'";
                break;
            case PlaylistTrackState.Downloading:
                level = "INFO";
                stage = "DOWNLOAD";
                message = $"Downloading '{artist} - {title}'" + (!string.IsNullOrEmpty(e.PeerName) ? $" from peer '{e.PeerName}'" : "");
                break;
            case PlaylistTrackState.Completed:
                level = "SUCCESS";
                stage = "ENGINE";
                message = $"Successfully downloaded '{artist} - {title}'";
                break;
            case PlaylistTrackState.Failed:
                level = "ERROR";
                stage = "ENGINE";
                message = $"Failed to download '{artist} - {title}'" + (!string.IsNullOrEmpty(e.Error) ? $": {e.Error}" : "");
                break;
            case PlaylistTrackState.Cancelled:
                level = "WARN";
                stage = "ENGINE";
                message = $"Cancelled download for '{artist} - {title}'";
                break;
            default:
                return;
        }
        
        Dispatcher.UIThread.Post(() =>
        {
            lock (_logsLock)
            {
                if (EngineLogs.LastOrDefault()?.Message == message) return;
                
                EngineLogs.Add(new EngineLogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = level,
                    Stage = stage,
                    Message = message
                });
                
                if (EngineLogs.Count > 1000)
                    EngineLogs.RemoveAt(0);
                
                this.RaisePropertyChanged(nameof(EngineLogCount));
            }
        });
    }

    private void ExecuteCancelAllActive()
    {
        foreach (var track in _downloadsSource.Items
            .Where(x => x.State == PlaylistTrackState.Searching || x.State == PlaylistTrackState.Downloading)
            .ToList())
        {
            _downloadManager.CancelTrack(track.GlobalId);
        }
    }

    private async Task ExecuteResetDownloadCenterAsync()
    {
        bool confirm = await _dialogService.ConfirmAsync(
            "Reset Download Center", 
            "Are you sure you want to cancel all active downloads and clear all jobs/history from the Download Center? The library itself will remain untouched.", 
            "Yes", "No");
            
        if (!confirm) return;

        var activeTracks = _downloadsSource.Items
            .Where(x => x.State == PlaylistTrackState.Searching || x.State == PlaylistTrackState.Downloading || x.State == PlaylistTrackState.Pending || x.State == PlaylistTrackState.Queued)
            .ToList();

        foreach (var track in activeTracks)
        {
            _downloadManager.CancelTrack(track.GlobalId);
        }

        await Task.Delay(250);

        var allTracks = _downloadsSource.Items.ToList();
        foreach (var track in allTracks)
        {
            track.IsClearedFromDownloadCenter = true;
            track.Model.IsClearedFromDownloadCenter = true;
            await _libraryService.UpdatePlaylistTrackAsync(track.Model);
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_logsLock)
            {
                EngineLogs.Clear();
                this.RaisePropertyChanged(nameof(EngineLogCount));
            }
        });

        ShowGlobalStatus("Download Center has been fully reset.", isError: false, autoHide: true, context: "reset");
    }
    
    public void Dispose()
    {
        _uiBatchTimer?.Stop();
        _statusBannerTimer?.Stop();
        _subscriptions.Dispose();
        _downloadsSource.Dispose();
    }

    // Phase 12.3: Slot Health Logic
    private void CheckSlotHealth()
    {
        try
        {
            var activeItems = _activeDownloads.Where(d => d.State == PlaylistTrackState.Downloading).ToList();
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
        catch (Exception ex)
        {
            // Was a bare catch — one exception here permanently and silently disabled stall
            // detection for the rest of the session, with no trace of why.
            Serilog.Log.Warning(ex, "CheckSlotHealth failed — stall detection may be degraded this session");
        }
    }
}
