using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;
using System.Reactive.Disposables;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Services.Audio;

using System.Collections.Specialized;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// Manages track lists, filtering, and search functionality.
/// Handles track display state and filtering logic.
public class TrackListViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private bool _isDisposed;

    private readonly ILogger<TrackListViewModel> _logger;
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;
    private MainViewModel? _mainViewModel; // Injected post-construction
    private readonly ArtworkCacheService _artworkCache;
    private readonly IEventBus _eventBus;
    private readonly AppConfig _config;
    private readonly IBulkOperationCoordinator _bulkCoordinator;

    public TrackOperationsViewModel? Operations { get; set; }

    public HierarchicalLibraryViewModel Hierarchical { get; }
    
    private readonly System.Reactive.Subjects.Subject<System.Reactive.Unit> _refreshRequestSubject = new();

    private ObservableCollection<PlaylistTrackViewModel> _currentProjectTracks = new();
    public ObservableCollection<PlaylistTrackViewModel> CurrentProjectTracks
    {
        get => _currentProjectTracks;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentProjectTracks, value);
            RefreshFilteredTracks();
        }
    }

    private IList<PlaylistTrackViewModel> _filteredTracks = new ObservableCollection<PlaylistTrackViewModel>();
    public IList<PlaylistTrackViewModel> FilteredTracks
    {
        get => _filteredTracks;
        private set 
        {
            if (_filteredTracks is INotifyCollectionChanged oldCol)
            {
                oldCol.CollectionChanged -= OnFilteredTracksChanged;
            }

            this.RaiseAndSetIfChanged(ref _filteredTracks, value);
            
            if (_filteredTracks is INotifyCollectionChanged newCol)
            {
                newCol.CollectionChanged += OnFilteredTracksChanged;
            }
            
            UpdateLimitedTracks();
        }
    }

    private void OnFilteredTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Throttled notification for LimitedTracks to avoid UI flooding
        _updateLimitedTracksRequest.OnNext(System.Reactive.Unit.Default);
    }
    
    private readonly System.Reactive.Subjects.Subject<System.Reactive.Unit> _updateLimitedTracksRequest = new();
    private IEnumerable<PlaylistTrackViewModel> _limitedTracks = Enumerable.Empty<PlaylistTrackViewModel>();

    /// <summary>
    /// A safe subset of tracks (max 50) for non-virtualized views like the Card View.
    /// Cached and throttled to prevent UI freezing.
    /// </summary>
    public IEnumerable<PlaylistTrackViewModel> LimitedTracks => _limitedTracks;

    private void UpdateLimitedTracks()
    {
        var result = (FilteredTracks as VirtualizedTrackCollection)?.GetSubset(50) ?? FilteredTracks.Take(50);
        var list = result.ToList();
        
        if (!Enumerable.SequenceEqual(_limitedTracks, list))
        {
            _limitedTracks = list;
            this.RaisePropertyChanged(nameof(LimitedTracks));
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    // Guard flag to prevent infinite recursion in filter properties
    private bool _updatingFilters = false;

    private bool _isFilterAll = true;
    public bool IsFilterAll
    {
        get => _isFilterAll;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterAll, value);
                if (value)
                {
                    _isFilterDownloaded = false;
                    this.RaisePropertyChanged(nameof(IsFilterDownloaded));
                    
                    _isFilterPending = false;
                    this.RaisePropertyChanged(nameof(IsFilterPending));
                }
                else if (!IsFilterDownloaded && !IsFilterPending)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _isFilterDownloaded;
    public bool IsFilterDownloaded
    {
        get => _isFilterDownloaded;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterDownloaded, value);
                if (value)
                {
                    _isFilterAll = false;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                    
                    _isFilterPending = false;
                    this.RaisePropertyChanged(nameof(IsFilterPending));
                }
                else if (!IsFilterPending)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _isFilterPending;
    public bool IsFilterPending
    {
        get => _isFilterPending;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterPending, value);
                if (value)
                {
                    _isFilterAll = false;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                    
                    _isFilterDownloaded = false;
                    this.RaisePropertyChanged(nameof(IsFilterDownloaded));

                    _isFilterNeedsReview = false;
                    this.RaisePropertyChanged(nameof(IsFilterNeedsReview));
                }
                else if (!IsFilterDownloaded && !IsFilterNeedsReview)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _isFilterNeedsReview;
    public bool IsFilterNeedsReview
    {
        get => _isFilterNeedsReview;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterNeedsReview, value);
                if (value)
                {
                    _isFilterAll = false;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                    
                    _isFilterDownloaded = false;
                    this.RaisePropertyChanged(nameof(IsFilterDownloaded));
                    
                    _isFilterPending = false;
                    this.RaisePropertyChanged(nameof(IsFilterPending));
                }
                else if (!IsFilterDownloaded && !IsFilterPending)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _isFilterLiked;
    public bool IsFilterLiked
    {
        get => _isFilterLiked;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterLiked, value);
                if (value)
                {
                    _isFilterAll = false;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
                else if (!IsFilterDownloaded && !IsFilterPending && !IsFilterNeedsReview)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
                
                RefreshFilteredTracks();
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _hasMultiSelection;
    public bool HasMultiSelection
    {
        get => _hasMultiSelection;
        private set => this.RaiseAndSetIfChanged(ref _hasMultiSelection, value);
    }
    
    // Phase 22: Search 2.0 - The Bouncer
    private bool _isBouncerActive;
    public bool IsBouncerActive
    {
        get => _isBouncerActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBouncerActive, value);
            RefreshFilteredTracks();
        }
    }

    // Phase 22: Search 2.0 - Vibe Filter
    private string? _vibeFilter;
    public string? VibeFilter
    {
        get => _vibeFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _vibeFilter, value);
            RefreshFilteredTracks();
        }
    }

    // Format Filters
    private bool _isFilterFlac;
    public bool IsFilterFlac
    {
        get => _isFilterFlac;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterFlac, value);
            RefreshFilteredTracks();
        }
    }

    private bool _isFilterMp3;
    public bool IsFilterMp3
    {
        get => _isFilterMp3;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterMp3, value);
            RefreshFilteredTracks();
        }
    }

    private bool _isFilterWav;
    public bool IsFilterWav
    {
        get => _isFilterWav;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterWav, value);
            RefreshFilteredTracks();
        }
    }

    private bool _isFilterLossless;
    public bool IsFilterLossless
    {
        get => _isFilterLossless;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterLossless, value);
            RefreshFilteredTracks();
        }
    }

    // Quality Tier Filter - individual booleans for each tier (consistent with status filter pattern)
    private bool _isFilterQualityGold;
    public bool IsFilterQualityGold
    {
        get => _isFilterQualityGold;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterQualityGold, value);
            RefreshFilteredTracks();
        }
    }

    private bool _isFilterQualityVerified;
    public bool IsFilterQualityVerified
    {
        get => _isFilterQualityVerified;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterQualityVerified, value);
            RefreshFilteredTracks();
        }
    }

    private bool _isFilterQualityReview;
    public bool IsFilterQualityReview
    {
        get => _isFilterQualityReview;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterQualityReview, value);
            RefreshFilteredTracks();
        }
    }

    private bool _hasSelectedTracks;
    public bool HasSelectedTracks
    {
        get => _hasSelectedTracks;
        private set => this.RaiseAndSetIfChanged(ref _hasSelectedTracks, value);
    }

    private string _selectedCountText = string.Empty;
    public string SelectedCountText
    {
        get => _selectedCountText;
        private set => this.RaiseAndSetIfChanged(ref _selectedCountText, value);
    }
    
    // Phase 22: Search 2.1 - Split Results
    private ObservableCollection<PlaylistTrackViewModel> _otherPlaylistsMatches = new();
    public ObservableCollection<PlaylistTrackViewModel> OtherPlaylistsMatches
    {
        get => _otherPlaylistsMatches;
        set => this.RaiseAndSetIfChanged(ref _otherPlaylistsMatches, value);
    }

    private bool _hasOtherPlaylistsMatches;
    public bool HasOtherPlaylistsMatches
    {
        get => _hasOtherPlaylistsMatches;
        private set => this.RaiseAndSetIfChanged(ref _hasOtherPlaylistsMatches, value);
    }

    private HashSet<string>? _duplicateHashesFilter;
    public HashSet<string>? DuplicateHashesFilter
    {
        get => _duplicateHashesFilter;
        set => this.RaiseAndSetIfChanged(ref _duplicateHashesFilter, value);
    }

    // Task 10.4: Per-column inline filters
    private string _filterArtist = string.Empty;
    public string FilterArtist
    {
        get => _filterArtist;
        set => this.RaiseAndSetIfChanged(ref _filterArtist, value);
    }

    private string _filterTitle = string.Empty;
    public string FilterTitle
    {
        get => _filterTitle;
        set => this.RaiseAndSetIfChanged(ref _filterTitle, value);
    }

    // Task 10.4: Column visibility
    private bool _isColumnFilterStripVisible;
    public bool IsColumnFilterStripVisible
    {
        get => _isColumnFilterStripVisible;
        set => this.RaiseAndSetIfChanged(ref _isColumnFilterStripVisible, value);
    }

    private bool _isFormatColumnVisible = true;
    public bool IsFormatColumnVisible
    {
        get => _isFormatColumnVisible;
        set => this.RaiseAndSetIfChanged(ref _isFormatColumnVisible, value);
    }

    private bool _isForensicsColumnVisible = true;
    public bool IsForensicsColumnVisible
    {
        get => _isForensicsColumnVisible;
        set => this.RaiseAndSetIfChanged(ref _isForensicsColumnVisible, value);
    }

    private bool _isDurationColumnVisible = true;
    public bool IsDurationColumnVisible
    {
        get => _isDurationColumnVisible;
        set => this.RaiseAndSetIfChanged(ref _isDurationColumnVisible, value);
    }
    
    // ListBox Selection Binding
    private ObservableCollection<PlaylistTrackViewModel> _selectedTracks = new();
    public ObservableCollection<PlaylistTrackViewModel> SelectedTracks 
    { 
        get => _selectedTracks;
        private set
        {
            if (value == null || ReferenceEquals(_selectedTracks, value)) return;

            if (_selectedTracks != null)
                _selectedTracks.CollectionChanged -= OnSelectionChanged;
            
            this.RaisePropertyChanging();
            _selectedTracks = value;
            this.RaisePropertyChanged();
            
            if (_selectedTracks != null)
                _selectedTracks.CollectionChanged += OnSelectionChanged;
                
            UpdateSelectionState();
        }
    }

    public void UpdateSelection(System.Collections.Generic.IEnumerable<PlaylistTrackViewModel> selected)
    {
        // Don't trigger recursive updates if we're already changing selection
        _selectedTracks.Clear();
        foreach (var t in selected) _selectedTracks.Add(t);
        UpdateSelectionState();
    }

    public void ClearSelection()
    {
        _selectedTracks.Clear();
        UpdateSelectionState();
    }
    
    // Phase 22: Available Vibes
    public ObservableCollection<string> AvailableVibes { get; } = new ObservableCollection<string>
    {
        "Aggressive", "Chaotic", "Energetic", "Happy", 
        "Party", "Relaxed", "Sad", "Dark"
    };

    public PlaylistTrackViewModel? LeadSelectedTrack => SelectedTracks.FirstOrDefault();

    // Phase 15: Style Filters
    public ObservableCollection<StyleFilterItem> StyleFilters { get; } = new();

    private void OnStyleFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StyleFilterItem.IsSelected))
        {
            RefreshFilteredTracks();
        }
    }

    private bool _isLoadingStyles;
    public async Task LoadStyleFiltersAsync()
    {
        if (_isLoadingStyles) return;
        _isLoadingStyles = true;
        
        try 
        {
            var styles = await _libraryService.GetStyleDefinitionsAsync();
            
            _logger.LogInformation("Loading {Count} style definitions from database", styles.Count);
            
            // Deduplicate by Name to prevent redundant UI chips (User's specific request)
            var uniqueStyles = styles
                .GroupBy(s => s.Name)
                .Select(g => g.First())
                .OrderBy(s => s.Name)
                .ToList();
            
            // Updates on UI Thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Detach event handlers before clearing
                foreach (var item in StyleFilters) 
                    item.PropertyChanged -= OnStyleFilterChanged;
                
                StyleFilters.Clear();

                foreach (var style in uniqueStyles)
                {
                    var item = new StyleFilterItem(style);
                    item.PropertyChanged += OnStyleFilterChanged;
                    StyleFilters.Add(item);
                }
                
                _logger.LogInformation("Loaded {Count} unique style filters into UI", StyleFilters.Count);
            }, DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load style filters");
        }
        finally
        {
            _isLoadingStyles = false;
        }
    }

    public System.Windows.Input.ICommand ToggleColumnFilterStripCommand { get; }
    public System.Windows.Input.ICommand SelectAllTracksCommand { get; }
    public System.Windows.Input.ICommand DeselectAllTracksCommand { get; }
    public System.Windows.Input.ICommand BulkDownloadCommand { get; }
    public System.Windows.Input.ICommand CopyToFolderCommand { get; }
    public System.Windows.Input.ICommand BulkRetryCommand { get; }
    public System.Windows.Input.ICommand BulkCancelCommand { get; }
    public System.Windows.Input.ICommand BulkReEnrichCommand { get; }
    public System.Windows.Input.ICommand BulkExportCsvCommand { get; }
    
    // Phase 18: Sonic Match - Find Similar Vibe
    public System.Windows.Input.ICommand FindSimilarCommand { get; }

    // Phase 22: Search 2.1 - Split Results
    public System.Windows.Input.ICommand AddToCurrentPlaylistCommand { get; }

    public TrackListViewModel(
        ILogger<TrackListViewModel> logger,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        ArtworkCacheService artworkCache,
        IEventBus eventBus,
        AppConfig config,
        IBulkOperationCoordinator bulkCoordinator)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _artworkCache = artworkCache;
        _eventBus = eventBus;
        _config = config;
        _bulkCoordinator = bulkCoordinator;

        Hierarchical = new HierarchicalLibraryViewModel(config, downloadManager, artworkCache);
        
        ToggleColumnFilterStripCommand = ReactiveCommand.Create(() => IsColumnFilterStripVisible = !IsColumnFilterStripVisible);

        SelectAllTracksCommand = ReactiveCommand.Create(() => 
        {
            // Update IsSelected property to reflect selection visually
            // Only select what's currently filtered and visible
            var tracks = FilteredTracks.ToList();
            
            // Batch the collection update
            _selectedTracks.CollectionChanged -= OnSelectionChanged;
            try
            {
                _selectedTracks.Clear();
                foreach (var track in tracks)
                {
                    track.IsSelected = true;
                    _selectedTracks.Add(track);
                }
            }
            finally
            {
                _selectedTracks.CollectionChanged += OnSelectionChanged;
                OnSelectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
            
            UpdateSelectionState();
        });

        DeselectAllTracksCommand = ReactiveCommand.Create(() => 
        {
            _selectedTracks.CollectionChanged -= OnSelectionChanged;
            try
            {
                // CRITICAL: Use ToList() to iterate over a snapshot. 
                // Updating IsSelected = false will trigger two-way bindings in the UI,
                // which might otherwise modify the collection during enumeration.
                foreach (var track in _selectedTracks.ToList())
                {
                    track.IsSelected = false;
                }
                _selectedTracks.Clear();
            }
            finally
            {
                _selectedTracks.CollectionChanged += OnSelectionChanged;
                OnSelectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
            
            UpdateSelectionState();
        });

        BulkDownloadCommand = ReactiveCommand.CreateFromTask(ExecuteBulkDownloadAsync);
        BulkRetryCommand = ReactiveCommand.CreateFromTask(ExecuteBulkRetryAsync);
        CopyToFolderCommand = ReactiveCommand.CreateFromTask(ExecuteCopyToFolderAsync);
        BulkCancelCommand = ReactiveCommand.CreateFromTask(ExecuteBulkCancelAsync);
        BulkReEnrichCommand = ReactiveCommand.CreateFromTask(ExecuteBulkReEnrichAsync);
        BulkExportCsvCommand = ReactiveCommand.CreateFromTask(ExecuteBulkExportCsvAsync);
        
        // Phase 18: Find Similar - triggers sonic match search
        FindSimilarCommand = ReactiveCommand.Create<PlaylistTrackViewModel>(ExecuteFindSimilar);

        // Phase 22: Search 2.1 - Split Results
        AddToCurrentPlaylistCommand = ReactiveCommand.CreateFromTask<PlaylistTrackViewModel>(ExecuteAddToCurrentPlaylistAsync);

        // Selection Change Tracking
        _selectedTracks.CollectionChanged += OnSelectionChanged;

        // Throttled search and filter synchronization (includes per-column filters)
        this.WhenAnyValue(
            x => x.SearchText,
            x => x.IsFilterAll,
            x => x.IsFilterDownloaded,
            x => x.IsFilterPending,
            x => x.IsFilterNeedsReview,
            x => x.FilterArtist,
            x => x.FilterTitle)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshFilteredTracks())
            .DisposeWith(_disposables);

        // Phase 22: Search 2.1 - Split Results
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(query => _ = PerformCrossPlaylistSearchAsync(query))
            .DisposeWith(_disposables);

        // Subscribe to global track updates
        _disposables.Add(eventBus.GetEvent<TrackUpdatedEvent>().Subscribe(evt => OnGlobalTrackUpdated(this, evt.Track)));

        // Phase 6D: Local UI sync for track moves
        _disposables.Add(eventBus.GetEvent<TrackMovedEvent>().Subscribe(evt => OnTrackMoved(evt)));

        // Phase 15: Refresh filters when definitions change
        _disposables.Add(eventBus.GetEvent<StyleDefinitionsUpdatedEvent>().Subscribe(evt => { _ = LoadStyleFiltersAsync(); }));
        
        // Phase 11.6: Refresh UI when track is added (cloned)
        _disposables.Add(eventBus.GetEvent<TrackAddedEvent>().Subscribe(OnTrackAdded));

        
        // Throttled UI Refresh for dynamic changes (add/move/delete)
        _refreshRequestSubject
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshFilteredTracks())
            .DisposeWith(_disposables);

        // Throttled LimitedTracks updates
        _updateLimitedTracksRequest
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateLimitedTracks())
            .DisposeWith(_disposables);

        // Initial Load
        _ = LoadStyleFiltersAsync();
    }
    
    // Explicit handler to support attach/detach
    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSelectionState();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _disposables.Dispose();
            
            // Dispose tracks
            foreach (var track in CurrentProjectTracks)
            {
                if (track is IDisposable d) d.Dispose();
            }
            CurrentProjectTracks.Clear();
            
            foreach (var style in StyleFilters)
            {
                style.PropertyChanged -= OnStyleFilterChanged;
            }
            StyleFilters.Clear();
        }

        _isDisposed = true;
    }


    private void OnTrackAdded(TrackAddedEvent evt)
    {
        // Use the throttled refresh subject instead of immediate post
        // This is critical for bulk imports (Spotify) to prevent UI thread flooding
        _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
    }

    private void OnTrackMoved(TrackMovedEvent evt)
    {
        // Use throttled refresh
        _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
    }

    public void SetMainViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    /// <summary>
    /// Loads tracks for the specified project.
    /// </summary>
    public async Task LoadProjectTracksAsync(PlaylistJob? job)
    {
        if (job == null)
        {
            // Dispose existing tracks
            foreach (var track in CurrentProjectTracks)
            {
               if (track is IDisposable disposable) disposable.Dispose();
            }
            CurrentProjectTracks.Clear();
            return;
        }

        try
        {
            _logger.LogInformation("Loading tracks for project: {Name} (Virtualized)", job.SourceTitle);
            
            // Cleanup existing
            foreach (var track in CurrentProjectTracks)
            {
               if (track is IDisposable disposable) disposable.Dispose();
            }
            CurrentProjectTracks.Clear();

            // Set up virtualization
            var virtualized = new VirtualizedTrackCollection(
                _logger,
                _libraryService, 
                _eventBus, 
                _artworkCache, 
                job.Id, 
                SearchText, 
                IsFilterDownloaded ? true : (IsFilterPending ? false : null));

            // Subscribe to update LimitedTracks when data arrives
            virtualized.CollectionChanged += (s, e) => {
                 if (e.Action == NotifyCollectionChangedAction.Reset)
                 {
                     Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(LimitedTracks)));
                 }
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FilteredTracks = virtualized;
                this.RaisePropertyChanged(nameof(LimitedTracks));
                _logger.LogInformation("Virtualized collection initialized for project {Title}", job.SourceTitle);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize virtualized track loading");
        }
    }

    /// <summary>
    /// Phase 23: Loads tracks for a Smart Crate (Dynamic Playlist).
    /// </summary>
    public async Task LoadSmartCrateAsync(List<string> trackGlobalIds)
    {
        try
        {
             _logger.LogInformation("Loading Smart Crate with {Count} tracks", trackGlobalIds.Count);
             
             // Dispose existing
            foreach (var track in CurrentProjectTracks)
            {
               if (track is IDisposable disposable) disposable.Dispose();
            }
            
            var tracks = new ObservableCollection<PlaylistTrackViewModel>();
            
            // Bulk fetch library entries
            var entries = await _libraryService.GetLibraryEntriesByHashesAsync(trackGlobalIds);
            
            _logger.LogInformation("Resolved {Count} library entries for crate", entries.Count);
            
            foreach (var entry in entries)
            {
                 // Create VM (in-memory only, no PlaylistTrack ID relation yet)
                 var vm = new PlaylistTrackViewModel(
                    new PlaylistTrack
                    {
                        Id = Guid.NewGuid(), // Ephemeral ID
                        PlaylistId = Guid.Empty,
                        TrackUniqueHash = entry.UniqueHash,
                        Artist = entry.Artist,
                        Title = entry.Title,
                        Album = entry.Album,
                        Status = TrackStatus.Downloaded, // Assume downloaded
                        ResolvedFilePath = entry.FilePath,
                        Format = entry.Format
                    },
                    _eventBus,
                    _libraryService,
                    _artworkCache
                );
                
                // Try to sync with Global State if available in MainViewModel (for active status)
                // Accessing MainViewModel requires traversing parents or injection.
                // Current architecture: We don't have MainViewModel injected here directly?
                // We do have OnGlobalTrackUpdated event handling though.
                
                tracks.Add(vm);
            }
            
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentProjectTracks = tracks;
            });
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to load Smart Crate");
        }
    }

    /// <summary>
    /// Refreshes the filtered tracks based on current filter settings.
    /// Optimized with batch updates for virtualization performance.
    /// </summary>

    private async Task SeparateStemsAsync(PlaylistTrack? singleTrack)
    {
        // If single track provided (context menu on item), use it.
        // If null (toolbar button), use selection.
        var tracksToProcess = new System.Collections.Generic.List<PlaylistTrackViewModel>();
        
        if (singleTrack != null)
        {
            // Find the VM for this track
            var vm = FilteredTracks.FirstOrDefault(t => t.Model == singleTrack);
            if (vm != null) tracksToProcess.Add(vm);
        }
        else if (SelectedTracks != null && SelectedTracks.Any())
        {
            tracksToProcess.AddRange(SelectedTracks);
        }

        if (!tracksToProcess.Any()) return;

        // Use Bulk Coordinator if available? 
        // Current SeparateStemsCommand implementation in PlaylistTrackViewModel calls SeparateStems() -> void
        // which triggers a background task/event.
        // So we can just call Execute on them.
        
        foreach (var track in tracksToProcess)
        {
            if (track.SeparateStemsCommand.CanExecute(null))
            {
                track.SeparateStemsCommand.Execute(null);
            }
        }
        
        await Task.CompletedTask;
    }

    private void ExecuteFindSimilar(PlaylistTrackViewModel? track)
    {
        _logger.LogInformation("Find Similar requested for {Artist} - {Title}", track?.Model?.Artist ?? "Unknown", track?.Model?.Title ?? "Unknown");
    }

    public void RefreshFilteredTracks()
    {
        var selectedProjectId = _mainViewModel?.LibraryViewModel?.SelectedProject?.Id ?? Guid.Empty;

        // Phase 23: Logic for In-Memory vs Database Virtualization
        if (CurrentProjectTracks.Any() && selectedProjectId != Guid.Empty && CurrentProjectTracks.First().SourceId != selectedProjectId)
        {
             // ID Mismatch - use virtualization to reload from the correct project
             _logger.LogInformation("RefreshFilteredTracks: ID mismatch or project switch detected. Using virtualization.");
        }
        else if (CurrentProjectTracks.Any() && selectedProjectId == Guid.Empty)
        {
             // Use in-memory tracks (useful for Smart Playlists that aren't DB crates)
             _logger.LogInformation("RefreshFilteredTracks: Using in-memory tracks (Count: {Count})", CurrentProjectTracks.Count);
             var filtered = CurrentProjectTracks.Where(FilterTracks).ToList();
             
             var oldVtcMemory = FilteredTracks as VirtualizedTrackCollection;
             FilteredTracks = new ObservableCollection<PlaylistTrackViewModel>(filtered);
             this.RaisePropertyChanged(nameof(LimitedTracks));
             oldVtcMemory?.Dispose();
             return;
        }

        // Standard Path: Virtualization for DB Projects or "All Tracks"
        // Combine global SearchText with per-column filters into a single query token
        var effectiveFilter = BuildEffectiveFilter();
        var virtualized = new VirtualizedTrackCollection(
            _logger,
            _libraryService, 
            _eventBus, 
            _artworkCache, 
            selectedProjectId, 
            effectiveFilter, 
            IsFilterDownloaded ? true : (IsFilterPending ? false : null),
            DuplicateHashesFilter);

        virtualized.CollectionChanged += (s, e) => {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                _updateLimitedTracksRequest.OnNext(System.Reactive.Unit.Default);
            }
        };

        var oldVtc = FilteredTracks as VirtualizedTrackCollection;
        FilteredTracks = virtualized;
        this.RaisePropertyChanged(nameof(LimitedTracks));
        
        // Dispose old collection AFTER assignment to avoid re-rendering disposed items
        oldVtc?.Dispose();

        _logger.LogInformation("RefreshFilteredTracks (Virtualized): Updated filters for project {Id}. Search='{Search}', DL={DL}, Pend={Pend}", 
            selectedProjectId, effectiveFilter, IsFilterDownloaded, IsFilterPending);
    }

    /// <summary>Merges global search text with per-column filters into one token for VTC/DB queries.</summary>
    private string BuildEffectiveFilter()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(SearchText)) parts.Add(SearchText.Trim());
        if (!string.IsNullOrWhiteSpace(FilterArtist)) parts.Add(FilterArtist.Trim());
        if (!string.IsNullOrWhiteSpace(FilterTitle)) parts.Add(FilterTitle.Trim());
        return string.Join(" ", parts);
    }

    private bool FilterTracks(object obj)
    {
        if (obj is not PlaylistTrackViewModel track) return false;

        // Apply state filter first
        if (!IsFilterAll)
        {
            if (IsFilterNeedsReview && !track.IsReviewNeeded)
                return false;

            if (IsFilterDownloaded && track.State != PlaylistTrackState.Completed)
                return false;

            if (IsFilterPending && track.State == PlaylistTrackState.Completed)
                return false;
        }

        // Phase 15: Style Filtering
        // If NO styles are selected, show ALL (ignore this filter level).
        // If ANY styles are selected, track must match ONE of them.
        var selectedStyles = StyleFilters.Where(s => s.IsSelected).ToList();
        if (selectedStyles.Any())
        {
            var trackStyle = track.Model.DetectedSubGenre;
            if (string.IsNullOrEmpty(trackStyle)) return false; // No style = filtered out if filter active

            bool match = false;
            foreach (var style in selectedStyles)
            {
                 if (string.Equals(trackStyle, style.Style.Name, StringComparison.OrdinalIgnoreCase))
                 {
                     match = true;
                     break;
                 }
            }
            if (!match) return false;
        }
        
        // Phase 22: The Bouncer (Quality Control)
        if (IsBouncerActive)
        {
             // Filter out < 256kbps or unanalyzed tracks
             // Note: FLAC usually has Bitrate 0 or 1000+ in our simpler model, need to check
             // BitrateScore is usually the robust one.
             if (track.Model.BitrateScore.HasValue && track.Model.BitrateScore.Value < 256)
             {
                 return false;
             }
             // Also filter suspicious integrity if we want to be strict
             if (track.Model.Integrity == Data.IntegrityLevel.Suspicious)
             {
                 return false;
             }
        }
        
        // Phase 22: Vibe Filter (Mood)
        if (!string.IsNullOrEmpty(VibeFilter))
        {
             // Match MoodTag (e.g. "Aggressive", "Chill")
             if (!string.Equals(track.Model.MoodTag, VibeFilter, StringComparison.OrdinalIgnoreCase))
             {
                 return false;
             }
        }

        // Format Filters
        bool anyFormatFilterActive = IsFilterFlac || IsFilterMp3 || IsFilterWav || IsFilterLossless;
        if (anyFormatFilterActive)
        {
            var fmt = track.Model.Format?.ToUpperInvariant() ?? string.Empty;
            bool formatMatch = false;
            if (IsFilterFlac && fmt == "FLAC") formatMatch = true;
            if (IsFilterMp3 && fmt == "MP3") formatMatch = true;
            if (IsFilterWav && fmt == "WAV") formatMatch = true;
            if (IsFilterLossless && (fmt == "FLAC" || fmt == "WAV" || fmt == "AIFF" || fmt == "ALAC")) formatMatch = true;
            if (!formatMatch) return false;
        }

        // Quality Tier Filter
        bool anyQualityTierFilterActive = IsFilterQualityGold || IsFilterQualityVerified || IsFilterQualityReview;
        if (anyQualityTierFilterActive)
        {
            bool qualityMatch = false;
            if (IsFilterQualityGold && track.Model.Integrity == Data.IntegrityLevel.Gold) qualityMatch = true;
            if (IsFilterQualityVerified && track.Model.Integrity == Data.IntegrityLevel.Verified) qualityMatch = true;
            if (IsFilterQualityReview && track.Model.Integrity == Data.IntegrityLevel.Suspicious) qualityMatch = true;
            if (!qualityMatch) return false;
        }

        // Per-column inline filters (applied to in-memory path only; VTC path uses BuildEffectiveFilter)
        if (!string.IsNullOrWhiteSpace(FilterArtist) &&
            track.Artist?.Contains(FilterArtist.Trim(), StringComparison.OrdinalIgnoreCase) != true)
            return false;

        if (!string.IsNullOrWhiteSpace(FilterTitle) &&
            track.Title?.Contains(FilterTitle.Trim(), StringComparison.OrdinalIgnoreCase) != true)
            return false;

        // Apply global search filter
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var search = SearchText.Trim();
        return (track.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (track.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (track.MusicalKey?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (track.CamelotDisplay?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void UpdateSelectionState()
    {
        var count = SelectedTracks.Count;
        HasSelectedTracks = count > 0;
        HasMultiSelection = count > 1;
        SelectedCountText = $"{count} tracks selected";
        this.RaisePropertyChanged(nameof(LeadSelectedTrack));
    }

    private async Task ExecuteBulkDownloadAsync()
    {
        var selectedTracks = SelectedTracks.ToList();
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        await _bulkCoordinator.RunOperationAsync(
            selectedTracks,
            async (track, ct) =>
            {
                _downloadManager.QueueTracks(new System.Collections.Generic.List<PlaylistTrack> { track.Model });
                return true;
            },
            "Bulk Download"
        );

        await _downloadManager.StartAsync();

        SelectedTracks.Clear();

    }

    private async Task ExecuteCopyToFolderAsync()
    {
        try
        {
            // Get selected completed tracks only
            var selectedTracks = SelectedTracks
                .Where(t => t.State == PlaylistTrackState.Completed && !string.IsNullOrEmpty(t.Model?.ResolvedFilePath))
                .ToList();
            
            if (!selectedTracks.Any())
            {
                _logger.LogWarning("No completed tracks selected for copy");
                return;
            }

            _logger.LogInformation("Copy to folder: {Count} tracks selected", selectedTracks.Count);

            // Show folder picker dialog
            var folderTask = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select destination folder for tracks",
                    AllowMultiple = false
                };

                var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow == null) return null;

                var result = await mainWindow.StorageProvider.OpenFolderPickerAsync(dialog);
                return result?.FirstOrDefault()?.Path.LocalPath;
            });

            var targetFolder = await folderTask;
            if (string.IsNullOrEmpty(targetFolder))
            {
                _logger.LogInformation("Copy cancelled - no folder selected");
                return;
            }

            _logger.LogInformation("Copying {Count} files to: {Folder}", selectedTracks.Count, targetFolder);

            await _bulkCoordinator.RunOperationAsync(
                selectedTracks,
                async (track, ct) =>
                {
                    try
                    {
                        var sourceFile = track.Model?.ResolvedFilePath;
                        if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
                        {
                            return false;
                        }

                        var fileName = System.IO.Path.GetFileName(sourceFile);
                        var targetFile = System.IO.Path.Combine(targetFolder, fileName);

                        // Handle duplicate filenames
                        int suffix = 1;
                        while (System.IO.File.Exists(targetFile))
                        {
                            var nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(fileName);
                            var ext = System.IO.Path.GetExtension(fileName);
                            targetFile = System.IO.Path.Combine(targetFolder, $"{nameWithoutExt} ({suffix}){ext}");
                            suffix++;
                        }

                        System.IO.File.Copy(sourceFile, targetFile, false);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to copy track: {Title}", track.Title);
                        return false;
                    }
                },
                "Copy to Folder"
            );

            SelectedTracks.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy to folder operation failed");
        }
    }

    private async Task ExecuteBulkRetryAsync()
    {
        var selectedTracks = SelectedTracks
            .Where(t => t.State == PlaylistTrackState.Failed || t.State == PlaylistTrackState.Cancelled)
            .ToList();
        
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        await _bulkCoordinator.RunOperationAsync(
            selectedTracks,
            async (track, ct) =>
            {
                track.Resume();
                return true;
            },
            "Bulk Retry"
        );
        
        // Ensure DownloadManager resumes if paused
        _ = _downloadManager.StartAsync();
        SelectedTracks.Clear();
    }
    
    private async Task ExecuteBulkCancelAsync()
    {
        var selectedTracks = SelectedTracks
            .Where(t => t.IsActive)
            .ToList();
        
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        await _bulkCoordinator.RunOperationAsync(
            selectedTracks,
            async (track, ct) =>
            {
                track.Cancel();
                return true;
            },
            "Bulk Cancel"
        );
        SelectedTracks.Clear();
    }

    private async Task ExecuteBulkReEnrichAsync()
    {
        var selectedTracks = SelectedTracks.ToList();
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        // Enrichment has been removed.

        SelectedTracks.Clear();
    }

    private async Task ExecuteBulkExportCsvAsync()
    {
        try
        {
            var tracksToExport = SelectedTracks.Any()
                ? SelectedTracks.ToList()
                : FilteredTracks.ToList();

            if (!tracksToExport.Any())
            {
                _logger.LogWarning("No tracks available for CSV export");
                return;
            }

            var saveTask = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Export Tracks to CSV",
                    SuggestedFileName = $"orbit_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                    FileTypeChoices = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("CSV Files") { Patterns = new[] { "*.csv" } }
                    }
                };

                var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow == null) return null;

                var result = await mainWindow.StorageProvider.SaveFilePickerAsync(dialog);
                return result?.Path.LocalPath;
            });

            var targetPath = await saveTask;
            if (string.IsNullOrEmpty(targetPath))
            {
                _logger.LogInformation("CSV export cancelled - no file selected");
                return;
            }

            var csvLines = new System.Collections.Generic.List<string>
            {
                "Title,Artist,Album,Format,Bitrate,SampleRate,Duration,FileSize,BPM,Key,Quality,Integrity,SpectralAnalysis,DateAdded,LastPlayed,PlayCount,FilePath"
            };

            // RFC 4180: escape double quotes by doubling them; commas are safe inside quoted fields
            string EscapeCsvField(string? field) =>
                $"\"{field?.Replace("\"", "\"\"") ?? ""}\"";

            foreach (var track in tracksToExport)
            {
                var line = string.Join(",", new[]
                {
                    EscapeCsvField(track.Title),
                    EscapeCsvField(track.Artist),
                    EscapeCsvField(track.Album),
                    EscapeCsvField(track.FormatDisplay),
                    EscapeCsvField(track.Bitrate),
                    EscapeCsvField(track.SampleRateDisplay),
                    EscapeCsvField(track.DurationDisplay),
                    EscapeCsvField(track.FileSizeDisplay),
                    EscapeCsvField(track.BPM.ToString("F2")),
                    EscapeCsvField(track.MusicalKey),
                    EscapeCsvField(track.QualityScoreDisplay),
                    EscapeCsvField(track.IntegrityTooltip),
                    EscapeCsvField(track.SpectralAnalysisDisplay),
                    EscapeCsvField(track.AddedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                    EscapeCsvField(track.LastPlayedDisplay),
                    track.PlayCount.ToString(),
                    EscapeCsvField(track.Model.ResolvedFilePath)
                });
                csvLines.Add(line);
            }

            await System.IO.File.WriteAllLinesAsync(targetPath, csvLines);
            _logger.LogInformation("CSV export completed: {Count} tracks exported to {Path}", tracksToExport.Count, targetPath);

            if (SelectedTracks.Any()) SelectedTracks.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV export failed");
        }
    }

    private void OnGlobalTrackUpdated(object? sender, PlaylistTrackViewModel e)
    {
        // Track updates are handled by the ViewModel itself via binding
    }


    private async Task PerformCrossPlaylistSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OtherPlaylistsMatches.Clear();
                HasOtherPlaylistsMatches = false;
            });
            return;
        }

        var currentProjectId = _mainViewModel?.LibraryViewModel?.SelectedProject?.Id ?? Guid.Empty;
        
        try
        {
            var matches = await _libraryService.SearchAllPlaylists(query, 10);
            
            // Filter out tracks already in the current project to reduce duplication
            var filteredMatches = matches
                .Where(m => m.PlaylistId != currentProjectId)
                .GroupBy(m => m.TrackUniqueHash) // Deduplicate by hash
                .Select(g => g.First())
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OtherPlaylistsMatches.Clear();
                foreach (var m in filteredMatches)
                {
                    var vm = new PlaylistTrackViewModel(m, _eventBus, _libraryService, _artworkCache);
                    OtherPlaylistsMatches.Add(vm);
                }
                HasOtherPlaylistsMatches = OtherPlaylistsMatches.Any();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cross-playlist search failed");
        }
    }

    private async Task ExecuteAddToCurrentPlaylistAsync(PlaylistTrackViewModel trackVm)
    {
        var currentProject = _mainViewModel?.LibraryViewModel?.SelectedProject;
        if (currentProject == null)
        {
             _logger.LogWarning("Cannot add track to current playlist: No project selected");
             return;
        }

        try
        {
            await _libraryService.AddTracksToProjectAsync(new[] { trackVm.Model }, currentProject.Id);
            _logger.LogInformation("Added track {Title} to project {Project}", trackVm.Title, currentProject.SourceTitle);
            
            // Refresh to show it in the main list
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add track to current playlist");
        }
    }
}
