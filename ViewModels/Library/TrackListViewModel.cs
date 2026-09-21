using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Events;
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
    private readonly INotificationService? _notificationService;
    private readonly ILibraryPreviewPlayer _previewPlayer;
    private readonly SLSKDONET.Services.Repositories.ITransitionRepository _transitionRepository;
    private readonly SLSKDONET.Services.Similarity.SimilarityIndex _similarityIndex;
    private readonly SLSKDONET.Services.Playlist.PlaylistOptimizer _playlistOptimizer;

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

    private TrackSortColumn _sortColumn = TrackSortColumn.Default;
    /// <summary>Which column the grid is sorted by. Applied both to the DB-backed
    /// (VirtualizedTrackCollection) and in-memory (smart playlist) paths.</summary>
    public TrackSortColumn SortColumn
    {
        get => _sortColumn;
        set
        {
            this.RaiseAndSetIfChanged(ref _sortColumn, value);
            RaiseSortIndicatorProperties();
            RefreshFilteredTracks();
        }
    }

    private bool _sortDescending;
    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            this.RaiseAndSetIfChanged(ref _sortDescending, value);
            RaiseSortIndicatorProperties();
            RefreshFilteredTracks();
        }
    }

    /// <summary>Clicking a column header sorts by it ascending; clicking the same column again
    /// flips direction, matching the usual grid-header sort convention.</summary>
    public System.Windows.Input.ICommand SortByColumnCommand { get; }

    private void ExecuteSortByColumn(TrackSortColumn column)
    {
        if (_sortColumn == column)
        {
            SortDescending = !SortDescending;
            return;
        }

        // Avoid triggering RefreshFilteredTracks() twice (once per property) when switching
        // to a different column — set both fields first, then refresh once.
        _sortColumn = column;
        this.RaisePropertyChanged(nameof(SortColumn));
        _sortDescending = false;
        this.RaisePropertyChanged(nameof(SortDescending));
        RaiseSortIndicatorProperties();
        RefreshFilteredTracks();
    }

    // Header sort-arrow indicators. Plain per-column booleans (rather than an XAML enum-equality
    // converter/multibinding) since this Avalonia version's ObjectConverters has no Equals member.
    private void RaiseSortIndicatorProperties()
    {
        this.RaisePropertyChanged(nameof(IsSortedByArtistAscending));
        this.RaisePropertyChanged(nameof(IsSortedByArtistDescending));
        this.RaisePropertyChanged(nameof(IsSortedByTitleAscending));
        this.RaisePropertyChanged(nameof(IsSortedByTitleDescending));
        this.RaisePropertyChanged(nameof(IsSortedByBpmAscending));
        this.RaisePropertyChanged(nameof(IsSortedByBpmDescending));
        this.RaisePropertyChanged(nameof(IsSortedByDurationAscending));
        this.RaisePropertyChanged(nameof(IsSortedByDurationDescending));
    }

    public bool IsSortedByArtistAscending => SortColumn == TrackSortColumn.Artist && !SortDescending;
    public bool IsSortedByArtistDescending => SortColumn == TrackSortColumn.Artist && SortDescending;
    public bool IsSortedByTitleAscending => SortColumn == TrackSortColumn.Title && !SortDescending;
    public bool IsSortedByTitleDescending => SortColumn == TrackSortColumn.Title && SortDescending;
    public bool IsSortedByBpmAscending => SortColumn == TrackSortColumn.Bpm && !SortDescending;
    public bool IsSortedByBpmDescending => SortColumn == TrackSortColumn.Bpm && SortDescending;
    public bool IsSortedByDurationAscending => SortColumn == TrackSortColumn.Duration && !SortDescending;
    public bool IsSortedByDurationDescending => SortColumn == TrackSortColumn.Duration && SortDescending;

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
            this.RaisePropertyChanged(nameof(IsLibraryEmpty));

            if (_filteredTracks is INotifyCollectionChanged newCol)
            {
                newCol.CollectionChanged += OnFilteredTracksChanged;
            }

            UpdateLimitedTracks();
            // FilteredTracks — not CurrentProjectTracks — is what TrackListView.axaml's ItemsControl
            // actually renders (see its ItemsSource binding). For a real DB-backed project/playlist,
            // RefreshFilteredTracks swaps in a brand-new VirtualizedTrackCollection loaded straight
            // from the database — entirely separate PlaylistTrackViewModel instances from whatever
            // CurrentProjectTracks held. UpdateMixTransitionBadgesAsync used to only ever mutate
            // CurrentProjectTracks' instances, so toggling "+ Mix" on a real playlist (the common
            // case — confirmed live: zero badges rendered) silently did nothing, while the in-memory
            // smart-playlist path (which happens to reuse CurrentProjectTracks' own instances) looked
            // fine. Scheduling here, whenever the bound collection itself changes, covers both paths
            // uniformly. Harmonic highlights had the exact same bug (iterated CurrentProjectTracks
            // instead of the actually-bound collection) — same fix, same reasoning.
            ScheduleUpdateMixTransitionBadges();
            ScheduleUpdateHarmonicHighlights();
        }
    }

    private void OnFilteredTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Throttled notification for LimitedTracks to avoid UI flooding
        _updateLimitedTracksRequest.OnNext(System.Reactive.Unit.Default);
        // A VirtualizedTrackCollection raises this as pages load in asynchronously (Replace/Add),
        // swapping placeholder rows for real ones — badges/highlights need recomputing as that
        // happens, not just once when the collection is first assigned (see FilteredTracks setter
        // above).
        ScheduleUpdateMixTransitionBadges();
        ScheduleUpdateHarmonicHighlights();
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
    
    // These 11 filter setters used to call RefreshFilteredTracks() synchronously and
    // unconditionally — unlike SearchText/IsFilterAll/IsFilterDownloaded/IsFilterPending/
    // IsFilterNeedsReview/FilterArtist/FilterTitle, which are already coalesced through the
    // 250ms-throttled WhenAnyValue chain below. Each rebuild constructs a brand-new
    // VirtualizedTrackCollection (a fresh DB count query + re-subscribing 7 event-bus handlers,
    // disposing the old one) — toggling several of these checkboxes in quick succession (e.g.
    // clicking through format/quality chips) rebuilt the whole collection once per click instead
    // of once for the burst. Routed through _refreshRequestSubject instead — the same
    // already-throttled (500ms) pipeline OnFilteredTracksChanged and the Mix reorder command use.
    private string? _camelotKeyFilter;
    public string? CamelotKeyFilter
    {
        get => _camelotKeyFilter;
        set
        {
            if (_camelotKeyFilter == value) return;
            this.RaiseAndSetIfChanged(ref _camelotKeyFilter, value);
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    private string? _qualityTierFilter;
    /// <summary>
    /// "Gold"/"Silver"/"Bronze"/null — set by the Dashboard's quality badges before navigating
    /// here (see HomeViewModel.NavigateLibraryCommand). Uses the exact same thresholds as
    /// DashboardService's Gold/Silver/Bronze counts (see TrackRepository.ApplyQualityTierFilter)
    /// so the count a user clicks and the tracks they land on always match.
    /// </summary>
    public string? QualityTierFilter
    {
        get => _qualityTierFilter;
        set
        {
            if (_qualityTierFilter == value) return;
            this.RaiseAndSetIfChanged(ref _qualityTierFilter, value);
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    // Phase 22: Search 2.0 - The Bouncer
    private bool _isBouncerActive;
    public bool IsBouncerActive
    {
        get => _isBouncerActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBouncerActive, value);
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
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
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
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
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    private bool _isFilterMp3;
    public bool IsFilterMp3
    {
        get => _isFilterMp3;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterMp3, value);
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    private bool _isFilterWav;
    public bool IsFilterWav
    {
        get => _isFilterWav;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterWav, value);
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    private bool _isFilterLossless;
    public bool IsFilterLossless
    {
        get => _isFilterLossless;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterLossless, value);
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
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
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    private bool _isFilterQualityVerified;
    public bool IsFilterQualityVerified
    {
        get => _isFilterQualityVerified;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterQualityVerified, value);
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    private bool _isFilterQualityReview;
    public bool IsFilterQualityReview
    {
        get => _isFilterQualityReview;
        set
        {
            this.RaiseAndSetIfChanged(ref _isFilterQualityReview, value);
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    public bool IsLibraryEmpty => FilteredTracks.Count == 0;

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
        IBulkOperationCoordinator bulkCoordinator,
        ILibraryPreviewPlayer previewPlayer,
        SLSKDONET.Services.Repositories.ITransitionRepository transitionRepository,
        SLSKDONET.Services.Similarity.SimilarityIndex similarityIndex,
        SLSKDONET.Services.Playlist.PlaylistOptimizer playlistOptimizer,
        INotificationService? notificationService = null)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _artworkCache = artworkCache;
        _eventBus = eventBus;
        _config = config;
        _bulkCoordinator = bulkCoordinator;
        _previewPlayer = previewPlayer;
        _transitionRepository = transitionRepository;
        _similarityIndex = similarityIndex;
        _playlistOptimizer = playlistOptimizer;
        _notificationService = notificationService;

        ToggleMixModeCommand = ReactiveCommand.Create(() => IsMixModeEnabled = !IsMixModeEnabled);
        OpenMixTransitionCommand = ReactiveCommand.Create<PlaylistTrackViewModel?>(OpenMixTransition);
        SuggestReorderForBetterFlowCommand = ReactiveCommand.CreateFromTask(SuggestReorderForBetterFlowAsync);
        DismissFlowWarningCommand = ReactiveCommand.Create(() => { FlowWarningDismissed = true; });

        Hierarchical = new HierarchicalLibraryViewModel(config, downloadManager, artworkCache, eventBus);
        
        ToggleColumnFilterStripCommand = ReactiveCommand.Create(() => IsColumnFilterStripVisible = !IsColumnFilterStripVisible);

        SortByColumnCommand = ReactiveCommand.Create<TrackSortColumn>(ExecuteSortByColumn);

        SelectAllTracksCommand = ReactiveCommand.Create(() => 
        {
            // Update IsSelected property to reflect selection visually
            // Only select what's currently filtered and visible. Excludes placeholders (rows not
            // yet loaded from the DB on a large/virtualized library) — Select All acts on the
            // currently-loaded subset rather than force-loading everything.
            var tracks = FilteredTracks.ToList().Where(t => !t.IsPlaceholder).ToList();
            
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

        // Refresh when the playlist's contents changed elsewhere (e.g. "Remove from playlist",
        // or a bulk add landing in a project we're not actively viewing). Was previously only
        // wired for TrackAddedEvent/TrackMovedEvent, so operations that published
        // ProjectUpdatedEvent (like the remove-from-playlist flow) silently left the visible
        // list stale even though the underlying delete had already succeeded. Scoped to the
        // project actually being viewed so unrelated playlist updates don't reset scroll position.
        _disposables.Add(eventBus.GetEvent<ProjectUpdatedEvent>().Subscribe(evt =>
        {
            var currentProjectId = _mainViewModel?.LibraryViewModel?.SelectedProject?.Id;
            if (currentProjectId == evt.ProjectId)
                _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }));

        // Camelot key filter from wheel click (toggle: click same key again to clear)
        _disposables.Add(eventBus.GetEvent<SetCamelotKeyFilterEvent>().Subscribe(evt =>
            CamelotKeyFilter = CamelotKeyFilter == evt.Key ? null : evt.Key));

        
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

    // ── Library preview (hover-to-listen) ───────────────────────────────────

    /// <summary>
    /// Called when the pointer enters a library row. Debounced 250 ms inside
    /// the service so fast mouse sweeps do not trigger a flurry of file opens.
    /// Only fires for downloaded tracks that have a file on disk.
    /// </summary>
    public void PreviewTrack(PlaylistTrackViewModel track)
    {
        var path = track.Model.ResolvedFilePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        _previewPlayer.RequestPreview(path, track.Model.BPM);
    }

    /// <summary>Called when the pointer leaves the library surface entirely.</summary>
    public void StopPreview() => _previewPlayer.StopPreview();

    // ── Dispose ─────────────────────────────────────────────────────────────

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
                IsFilterDownloaded ? true : (IsFilterPending ? false : null),
                camelotKeyFilter: CamelotKeyFilter,
                qualityTier: QualityTierFilter);

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

    private void ExecuteFindSimilar(PlaylistTrackViewModel? track)
    {
        _logger.LogInformation("Find Similar requested for {Artist} - {Title}", track?.Model?.Artist ?? "Unknown", track?.Model?.Title ?? "Unknown");

        if (track == null || string.IsNullOrWhiteSpace(track.GlobalId))
        {
            _logger.LogWarning("Find Similar requested but the track had no analysis hash to search against");
            return;
        }

        // Opens the Similar Tracks sidebar panel and seeds it with this track — same event
        // mechanism (and the same real harmonic/energy/rhythm/timbre/structure similarity
        // engine) already used by the Bridge Finder, just for a single track instead of two.
        ReactiveUI.MessageBus.Current.SendMessage(
            new FindSimilarTrackRequestEvent(track.GlobalId, $"{track.ArtistName} - {track.TrackTitle}"));
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
             // Computed once here instead of inside FilterTracks (which used to re-run this
             // Where().ToList() for every single track being filtered — visible input lag on
             // large projects with style filters active).
             var selectedStyles = StyleFilters.Where(s => s.IsSelected).ToList();
             var filtered = ApplyInMemorySort(CurrentProjectTracks.Where(t => FilterTracks(t, selectedStyles)).ToList());

             var oldVtcMemory = FilteredTracks as VirtualizedTrackCollection;
             FilteredTracks = new ObservableCollection<PlaylistTrackViewModel>(filtered);
             this.RaisePropertyChanged(nameof(LimitedTracks));
             oldVtcMemory?.Dispose();
             ScheduleUpdateMixTransitionBadges();
             return;
        }

        // Standard Path: Virtualization for DB Projects or "All Tracks"
        // Combine global SearchText with per-column filters into a single query token.
        // In Mix mode, every other filter is bypassed and only downloaded tracks show — same
        // "dedicated view" rule as FilterTracks's early return above, applied to the DB-backed
        // path (real playlists, not just in-memory Smart Playlists).
        var effectiveFilter = IsMixModeEnabled ? string.Empty : BuildEffectiveFilter();
        var virtualized = new VirtualizedTrackCollection(
            _logger,
            _libraryService,
            _eventBus,
            _artworkCache,
            selectedProjectId,
            effectiveFilter,
            IsMixModeEnabled ? true : (IsFilterDownloaded ? true : (IsFilterPending ? false : null)),
            IsMixModeEnabled ? null : DuplicateHashesFilter,
            camelotKeyFilter: IsMixModeEnabled ? null : CamelotKeyFilter,
            qualityTier: IsMixModeEnabled ? null : QualityTierFilter,
            sortColumn: SortColumn,
            sortDescending: SortDescending);

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

    /// <summary>Applies the current sort selection to the in-memory (smart playlist) path. The
    /// DB-backed VirtualizedTrackCollection path applies the equivalent sort in SQL instead.</summary>
    internal List<PlaylistTrackViewModel> ApplyInMemorySort(List<PlaylistTrackViewModel> tracks)
    {
        return SortColumn switch
        {
            TrackSortColumn.Artist => SortDescending
                ? tracks.OrderByDescending(t => t.Artist).ThenBy(t => t.Title).ToList()
                : tracks.OrderBy(t => t.Artist).ThenBy(t => t.Title).ToList(),
            TrackSortColumn.Title => SortDescending
                ? tracks.OrderByDescending(t => t.Title).ToList()
                : tracks.OrderBy(t => t.Title).ToList(),
            TrackSortColumn.Bpm => SortDescending
                ? tracks.OrderByDescending(t => t.BPM).ToList()
                : tracks.OrderBy(t => t.BPM).ToList(),
            TrackSortColumn.Duration => SortDescending
                ? tracks.OrderByDescending(t => t.Model.CanonicalDuration ?? 0).ToList()
                : tracks.OrderBy(t => t.Model.CanonicalDuration ?? 0).ToList(),
            _ => tracks
        };
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

    private bool FilterTracks(object obj, System.Collections.Generic.List<StyleFilterItem> selectedStyles)
    {
        if (obj is not PlaylistTrackViewModel track) return false;

        // "+ Mix" is a dedicated build-transitions view — every other active filter (search,
        // style, quality tier, etc.) is bypassed while it's on, and only downloaded tracks show,
        // since a track that isn't on disk can't be previewed/mixed. Filters resume normally the
        // moment Mix mode turns back off (see IsMixModeEnabled's setter, which re-runs
        // RefreshFilteredTracks on both transitions).
        if (IsMixModeEnabled) return track.State == PlaylistTrackState.Completed;

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
             if (!string.Equals(track.Model.MoodTag, VibeFilter, StringComparison.OrdinalIgnoreCase))
                 return false;
        }

        // Camelot key filter
        if (!string.IsNullOrEmpty(CamelotKeyFilter))
        {
            if (!string.Equals(track.CamelotDisplay, CamelotKeyFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Quality tier filter (Gold/Silver/Bronze) — same thresholds as
        // TrackRepository.ApplyQualityTierFilter, kept in sync deliberately.
        if (!string.IsNullOrEmpty(QualityTierFilter))
        {
            var fmtUpper = track.Model.Format?.ToUpperInvariant() ?? string.Empty;
            bool isLossless = fmtUpper == "FLAC" || fmtUpper == "WAV";
            bool tierMatch = QualityTierFilter switch
            {
                "Gold" => isLossless,
                "Silver" => !isLossless && track.Model.Bitrate >= 320,
                "Bronze" => track.Model.Bitrate < 320 && track.Model.Bitrate > 0,
                _ => true
            };
            if (!tierMatch) return false;
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
        ScheduleUpdateHarmonicHighlights();
        ScheduleUpdateMixTransitionBadges();
    }

    public ReactiveCommand<Unit, bool> ToggleMixModeCommand { get; private set; } = null!;

    /// <summary>Badge-click: opens the "Mix" tab in the CONTEXT sidepanel for this row's
    /// transition into the next track.</summary>
    public ReactiveCommand<PlaylistTrackViewModel?, Unit> OpenMixTransitionCommand { get; private set; } = null!;

    /// <summary>Reorders the currently open playlist in place for smoother BPM/harmonic/genre
    /// flow — offered via the flow-warning banner when Mix mode notices several rough
    /// transitions. See <see cref="SuggestReorderForBetterFlowAsync"/>.</summary>
    public ReactiveCommand<Unit, Unit> SuggestReorderForBetterFlowCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> DismissFlowWarningCommand { get; private set; } = null!;

    private void OpenMixTransition(PlaylistTrackViewModel? outgoing)
    {
        if (outgoing?.NextPlaylistTrackId is not Guid incomingId) return;

        var playlistId = outgoing.Model?.PlaylistId ?? Guid.Empty;
        ReactiveUI.MessageBus.Current.SendMessage(
            new SLSKDONET.Events.OpenMixTransitionEvent(playlistId, outgoing.Id, incomingId));
    }

    private bool _isMixModeEnabled;
    /// <summary>"+ Mix" toggle (Spotify-Mix parity) — when on, each row shows a transition badge
    /// to the next track, and clicking one opens the Mix tab in the CONTEXT sidepanel.</summary>
    public bool IsMixModeEnabled
    {
        get => _isMixModeEnabled;
        set
        {
            var changed = _isMixModeEnabled != value;
            this.RaiseAndSetIfChanged(ref _isMixModeEnabled, value);
            if (changed)
            {
                // Must run on BOTH transitions, not just turning on. UpdateMixTransitionBadgesAsync
                // sets ShowMixTransitionBadge = IsMixModeEnabled for every row — only calling it
                // when value is true meant toggling Mix back OFF never re-ran it, so every badge
                // stayed stuck visible (verified live: turning "+ Mix" off left every row's "Auto"
                // badge showing).
                if (value) FlowWarningDismissed = false;
                this.RaisePropertyChanged(nameof(ShowFlowWarningBanner));
                // Only downloaded tracks show, and every other active filter is bypassed, while
                // Mix mode is on (FilterTracks/RefreshFilteredTracks's IsMixModeEnabled checks) —
                // must re-run on both transitions so turning Mix off restores the user's actual
                // filters exactly as they left them, not just hides the badges.
                RefreshFilteredTracks();
                _ = UpdateMixTransitionBadgesAsync();
            }
        }
    }

    private bool _isFlowSuboptimal;
    /// <summary>True when several adjacent transitions in the materialized window scored poorly
    /// (see <see cref="UpdateMixTransitionBadgesAsync"/>) — drives the "reorder for better flow"
    /// banner. Only meaningful while <see cref="IsMixModeEnabled"/> is on.</summary>
    public bool IsFlowSuboptimal
    {
        get => _isFlowSuboptimal;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isFlowSuboptimal, value);
            this.RaisePropertyChanged(nameof(ShowFlowWarningBanner));
        }
    }

    private bool _flowWarningDismissed;
    public bool FlowWarningDismissed
    {
        get => _flowWarningDismissed;
        set
        {
            this.RaiseAndSetIfChanged(ref _flowWarningDismissed, value);
            this.RaisePropertyChanged(nameof(ShowFlowWarningBanner));
        }
    }

    private string _flowWarningSummary = string.Empty;
    public string FlowWarningSummary
    {
        get => _flowWarningSummary;
        private set => this.RaiseAndSetIfChanged(ref _flowWarningSummary, value);
    }

    public bool ShowFlowWarningBanner => IsMixModeEnabled && IsFlowSuboptimal && !FlowWarningDismissed;

    private bool _mixBadgesScheduled;

    /// <summary>Coalesces bursts of list changes into a single badge recompute, mirroring
    /// ScheduleUpdateHarmonicHighlights below.</summary>
    private void ScheduleUpdateMixTransitionBadges()
    {
        if (!IsMixModeEnabled || _mixBadgesScheduled) return;
        _mixBadgesScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _mixBadgesScheduled = false;
            _ = UpdateMixTransitionBadgesAsync();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Walks the current track list pairwise, resolving each row's transition badge: a saved
    /// PlaylistTrackTransition if one exists for that (outgoing, incoming) pair, else a live
    /// "Auto" suggestion from TrackPairCompatibilityScorer — the same harmonic/energy formulas
    /// the Workstation Flow timeline uses (see TrackPairCompatibilityScorer's doc comment).
    /// </summary>
    /// <summary>
    /// Matches LimitedTracks' materialization cap — computing badges must not force a virtualized,
    /// DB-backed playlist to fully load just to toggle "+ Mix" on.
    /// </summary>
    private const int MixBadgeWindowSize = 50;

    private async Task UpdateMixTransitionBadgesAsync()
    {
        // FilteredTracks — not CurrentProjectTracks — is what TrackListView.axaml's ItemsControl
        // actually renders. See the FilteredTracks setter's comment for why this matters: a real
        // DB-backed playlist's FilteredTracks is a VirtualizedTrackCollection with entirely
        // different PlaylistTrackViewModel instances from CurrentProjectTracks, so this used to
        // silently do nothing for that (the common) case.
        var source = FilteredTracks;
        int totalCount = source.Count;
        var ordered = (source as VirtualizedTrackCollection)?.GetSubset(MixBadgeWindowSize).ToList()
            ?? source.Take(MixBadgeWindowSize).ToList();
        if (ordered.Count == 0) return;

        var playlistId = ordered[0].Model?.PlaylistId ?? Guid.Empty;
        var saved = playlistId != Guid.Empty
            ? (await _transitionRepository.GetTransitionsForPlaylistAsync(playlistId))
                .ToDictionary(t => (t.OutgoingPlaylistTrackId, t.IncomingPlaylistTrackId))
            : new Dictionary<(Guid, Guid), Models.Timeline.PlaylistTrackTransition>();

        // Fetched once per pass rather than once per pair — GetEmbeddingLookupAsync reuses
        // SimilarityIndex's own TTL-cached index, so this is cheap, but still O(1) calls beats
        // O(window size).
        IReadOnlyDictionary<string, float[]>? embeddings = null;
        if (IsMixModeEnabled && _similarityIndex != null)
        {
            try { embeddings = await _similarityIndex.GetEmbeddingLookupAsync(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "[Mix] Embedding lookup unavailable for badge scoring"); }
        }

        int poorCount = 0, scoredCount = 0;

        for (int i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            bool isLastOverall = i == totalCount - 1;
            if (isLastOverall)
            {
                current.ShowMixTransitionBadge = false;
                current.NextPlaylistTrackId = null;
                continue;
            }
            if (i >= ordered.Count - 1)
            {
                // Last item in this materialized window, but not the last track in the playlist —
                // its "next" hasn't loaded yet. Leave it as-is; OnFilteredTracksChanged reschedules
                // this once the next page arrives.
                continue;
            }

            var next = ordered[i + 1];
            current.ShowMixTransitionBadge = IsMixModeEnabled;
            current.NextPlaylistTrackId = next.Id;

            // A Pending/Review/OnHold track has no file on disk yet — there's nothing to
            // beatmatch, preview, or trust the BPM/key of (it may be stale source metadata rather
            // than analysis of a file we actually have). Scoring it anyway produced misleading
            // badges keyed off 0/default values. Model.Status is this codebase's established
            // "is the file really here" signal — see PlaylistTrackViewModel.IsGhost's doc comment.
            bool bothDownloaded = current.Model?.Status == TrackStatus.Downloaded
                && next.Model?.Status == TrackStatus.Downloaded;

            if (!bothDownloaded)
            {
                current.TransitionPresetLabel = "Pending";
                current.TransitionBadgeColor = "#66888888";
                current.TransitionWarningText = "Not downloaded yet";
                continue;
            }

            double? genreSimilarity = null;
            if (embeddings != null
                && embeddings.TryGetValue(current.GlobalId, out var vecA)
                && embeddings.TryGetValue(next.GlobalId, out var vecB))
            {
                genreSimilarity = Services.Similarity.SimilarityIndex.CosineSimilarity(vecA, vecB);
            }

            var score = Services.Playlist.TrackPairCompatibilityScorer.Score(
                current.CamelotDisplay, next.CamelotDisplay, current.Energy, next.Energy,
                outgoingBpm: current.Model?.BPM, incomingBpm: next.Model?.BPM,
                genreSimilarity: genreSimilarity);

            current.TransitionPresetLabel = saved.TryGetValue((current.Id, next.Id), out var savedTransition)
                ? savedTransition.PresetName
                : "Auto";
            current.TransitionBadgeColor = Services.Playlist.TrackPairCompatibilityScorer.CompatibilityColor(score.CombinedScore);

            var warnings = Services.Playlist.TrackPairCompatibilityScorer.BuildWarnings(score, current.Model?.BPM, next.Model?.BPM);
            current.TransitionWarningText = warnings.Count > 0 ? string.Join(" · ", warnings) : string.Empty;

            if (IsMixModeEnabled)
            {
                scoredCount++;
                if (score.CombinedScore < 45) poorCount++;
            }
        }

        if (IsMixModeEnabled)
        {
            // A proportional threshold (e.g. 25% of transitions) sounded reasonable on paper but
            // live-tested near-useless: a real 47-track curated set had just 2 genuinely rough
            // transitions (both <45) out of 46 — nowhere near 25%, yet exactly the kind of thing
            // worth flagging. 2+ rough transitions is a real, noticeable problem regardless of
            // playlist length, so that alone is the bar.
            IsFlowSuboptimal = scoredCount > 0 && poorCount >= 2;
            FlowWarningSummary = IsFlowSuboptimal
                ? $"{poorCount} of {scoredCount} transitions could be smoother"
                : string.Empty;
        }
        else
        {
            IsFlowSuboptimal = false;
        }
    }

    /// <summary>
    /// Reorders the currently open playlist in place using <see cref="PlaylistOptimizer"/> (the
    /// same BPM/harmonic/energy/genre-aware engine the Library's "Automix" feature already uses),
    /// then persists via the existing <see cref="ILibraryService.SaveTrackOrderAsync"/> — mirrors
    /// PlaylistIntelligenceViewModel's CreateAutomixPlaylistAsync + ApplyAutomixAsync, collapsed
    /// into a single in-place action since there's no separate staging step here.
    /// </summary>
    private async Task SuggestReorderForBetterFlowAsync()
    {
        var source = FilteredTracks;
        var playlistId = source.FirstOrDefault()?.Model?.PlaylistId ?? Guid.Empty;
        if (playlistId == Guid.Empty) return;

        // Capped at PlaylistOptimizer's own O(n²) safety limit — a playlist larger than that gets
        // its first MaxOptimizeTracks analyzed tracks reordered rather than failing outright.
        var tracks = (source as VirtualizedTrackCollection)?.GetSubset(Services.Playlist.PlaylistOptimizer.MaxOptimizeTracks).ToList()
            ?? source.Take(Services.Playlist.PlaylistOptimizer.MaxOptimizeTracks).ToList();

        // Only tracks whose audio is actually on disk can be meaningfully mixed/reordered — a
        // Pending/Review/OnHold row has no file to beatmatch or preview, and its stored BPM/key
        // (if any) may be stale metadata rather than analysis of a file we actually have. Model.
        // Status is this codebase's established "is the file really here" signal (see
        // PlaylistTrackViewModel.IsGhost's doc comment) — stronger than AvailabilityState.
        var eligible = tracks
            .Where(t => !t.IsPlaceholder && t.Model?.Status == TrackStatus.Downloaded && (t.HasBpm || t.HasAnalysisData))
            .ToList();
        if (eligible.Count < 2) return;

        // Not-yet-downloaded (but real, already-loaded) tracks are excluded from optimization but
        // must keep a valid, non-colliding SortOrder — appended after the reordered set, in their
        // original relative order, mirroring PlaylistOptimizer's own "unanalyzed tracks appended
        // at the end" convention for tracks it can't score.
        //
        // Placeholders are excluded here too, deliberately never touched: GetSubset's cache-miss
        // path (VirtualizedTrackCollection, still-loading pages) returns the SAME shared
        // PlaylistTrackViewModel.Placeholder singleton for every unloaded slot, not a distinct
        // instance per row. Including it here would mutate that shared instance's SortOrder
        // repeatedly and — far worse — add the same fake "Loading…" Model reference into
        // orderedModels once per unloaded slot, corrupting the SaveTrackOrderAsync write. A
        // playlist with unloaded rows at reorder time simply leaves those specific rows' SortOrder
        // untouched rather than risking that.
        var notEligible = tracks.Where(t => !t.IsPlaceholder && !eligible.Contains(t)).ToList();

        var hashes = eligible.Select(t => t.GlobalId).Where(h => !string.IsNullOrEmpty(h)).ToList();

        try
        {
            var result = await _playlistOptimizer.OptimizeAsync(hashes);
            if (result.OrderedHashes.Count < 2) return;

            var lookup = eligible.ToDictionary(t => t.GlobalId);
            var orderedModels = new List<PlaylistTrack>();
            int i = 1;
            foreach (var hash in result.OrderedHashes)
            {
                if (!lookup.TryGetValue(hash, out var track) || track.Model is null) continue;
                track.Model.SortOrder = i;
                track.Model.TrackNumber = i;
                orderedModels.Add(track.Model);
                i++;
            }

            foreach (var track in notEligible)
            {
                if (track.Model is null) continue;
                track.Model.SortOrder = i;
                track.Model.TrackNumber = i;
                orderedModels.Add(track.Model);
                i++;
            }

            await _libraryService.SaveTrackOrderAsync(playlistId, orderedModels);

            FlowWarningDismissed = true;
            _refreshRequestSubject.OnNext(Unit.Default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mix] Reorder-for-better-flow failed for playlist {PlaylistId}", playlistId);
        }
    }

    private bool _harmonicHighlightsScheduled;

    /// <summary>
    /// Coalesces bursts of selection changes (e.g. holding Shift+Down to extend a range
    /// selection, which fires one UpdateSelectionState per key-repeat) into a single full-collection
    /// harmonic-highlight recompute per burst instead of one per selection change.
    /// </summary>
    private void ScheduleUpdateHarmonicHighlights()
    {
        if (_harmonicHighlightsScheduled) return;
        _harmonicHighlightsScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _harmonicHighlightsScheduled = false;
            UpdateHarmonicHighlights();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// MixedInKey-style harmonic mixing highlight: marks tracks in the current library
    /// view as key-compatible with the lead selected track (Camelot wheel adjacent,
    /// relative major/minor, or exact key).
    /// </summary>
    private void UpdateHarmonicHighlights()
    {
        var lead = LeadSelectedTrack;
        var referenceKey = lead?.CamelotDisplay;
        var hasReference = !string.IsNullOrEmpty(referenceKey) && referenceKey != "—";

        // LimitedTracks (not CurrentProjectTracks) is the bounded, already-maintained view of
        // whatever FilteredTracks actually is — for a real DB-backed playlist (the common case),
        // FilteredTracks is a VirtualizedTrackCollection with entirely different
        // PlaylistTrackViewModel instances than CurrentProjectTracks (which only Smart Crates/
        // Smart Playlists populate), so this used to silently highlight nothing during normal
        // library/playlist browsing. Same root cause already fixed for the Mix badges — see
        // UpdateMixTransitionBadgesAsync's identical comment.
        foreach (var track in LimitedTracks)
        {
            if (!hasReference || track == lead)
            {
                track.IsHarmonicMatch = false;
                track.IsExactKeyMatch = false;
                continue;
            }

            var relation = Utils.KeyConverter.GetHarmonicRelation(referenceKey, track.CamelotDisplay);
            track.IsHarmonicMatch = relation == Utils.HarmonicRelation.Compatible;
            track.IsExactKeyMatch = relation == Utils.HarmonicRelation.Exact;
        }
    }

    private async Task ExecuteBulkDownloadAsync()
    {
        var selectedTracks = SelectedTracks.ToList();
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning)
        {
            _notificationService?.Show("Bulk Operation In Progress", "Another bulk operation is already running — wait for it to finish first.", NotificationType.Warning);
            return;
        }

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

        if (_bulkCoordinator.IsRunning)
        {
            _notificationService?.Show("Bulk Operation In Progress", "Another bulk operation is already running — wait for it to finish first.", NotificationType.Warning);
            return;
        }

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

        if (_bulkCoordinator.IsRunning)
        {
            _notificationService?.Show("Bulk Operation In Progress", "Another bulk operation is already running — wait for it to finish first.", NotificationType.Warning);
            return;
        }

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

    private async Task ExecuteBulkExportCsvAsync()
    {
        try
        {
            var tracksToExport = SelectedTracks.Any()
                ? SelectedTracks.ToList()
                : FilteredTracks.ToList().Where(t => !t.IsPlaceholder).ToList();

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
