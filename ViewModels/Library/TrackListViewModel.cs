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
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// Manages track lists, filtering, and search functionality.
/// Handles track display state and filtering logic.
public partial class TrackListViewModel : ReactiveObject, IDisposable
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
    private readonly System.Reactive.Subjects.Subject<System.Reactive.Unit> _liveRowsRequest = new();

    /// <summary>All Tracks is on screen: show rows an import committed since the last look.</summary>
    internal async Task ShowNewlyImportedRowsAsync()
    {
        try
        {
            var currentProjectId = _mainViewModel?.LibraryViewModel?.SelectedProject?.Id;
            if (currentProjectId != null && currentProjectId != Guid.Empty) return;
            if (FilteredTracks is VirtualizedTrackCollection vtc && await vtc.RefreshForNewRowsAsync())
                _updateLimitedTracksRequest.OnNext(System.Reactive.Unit.Default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Showing newly imported rows failed");
        }
    }

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
            if (_filteredTracks is VirtualizedTrackCollection oldVtc)
            {
                oldVtc.PropertyChanged -= OnFilteredTracksPropertyChanged;
            }

            this.RaiseAndSetIfChanged(ref _filteredTracks, value);
            this.RaisePropertyChanged(nameof(IsLibraryEmpty));

            if (_filteredTracks is INotifyCollectionChanged newCol)
            {
                newCol.CollectionChanged += OnFilteredTracksChanged;
            }
            if (_filteredTracks is VirtualizedTrackCollection newVtc)
            {
                newVtc.PropertyChanged += OnFilteredTracksPropertyChanged;
            }
            RaiseTrackLoadErrorChanged();

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

    /// <summary>
    /// Why the current list's tracks couldn't be loaded from the database, or null. Shown as the
    /// list's error state (with Retry) instead of "0 tracks" or rows stuck on "Loading…".
    /// </summary>
    public string? TrackLoadErrorMessage =>
        (FilteredTracks as VirtualizedTrackCollection)?.LoadErrorMessage is { } error
            ? $"Couldn't load these tracks: {error}"
            : null;

    public bool HasTrackLoadError => TrackLoadErrorMessage != null;

    public System.Windows.Input.ICommand RetryLoadTracksCommand { get; private set; } = null!;

    private void OnFilteredTracksPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VirtualizedTrackCollection.LoadErrorMessage))
            RaiseTrackLoadErrorChanged();
        else if (e.PropertyName == nameof(VirtualizedTrackCollection.Count))
            this.RaisePropertyChanged(nameof(IsLibraryEmpty));
    }

    private void RaiseTrackLoadErrorChanged()
    {
        this.RaisePropertyChanged(nameof(TrackLoadErrorMessage));
        this.RaisePropertyChanged(nameof(HasTrackLoadError));
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
                // Avalonia unchecks the old RadioButton before checking the new one. Never force
                // All back on during that transient write: doing so feeds true back into the group,
                // which writes false again and hangs the UI thread in a binding feedback loop.
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
            this.RaisePropertyChanged(nameof(HasCamelotKeyFilter));
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    /// <summary>A key filter set by clicking the Camelot wheel; shown as a removable chip in the toolbar.</summary>
    public bool HasCamelotKeyFilter => !string.IsNullOrEmpty(_camelotKeyFilter);

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
            this.RaisePropertyChanged(nameof(IsTierLosslessFilter));
            this.RaisePropertyChanged(nameof(IsTierHighFilter));
            this.RaisePropertyChanged(nameof(IsTierLowFilter));
            _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }
    }

    // Toolbar quality chips. They drive QualityTierFilter, which (unlike the old per-format and
    // integrity chips) is applied on both the DB-backed and the in-memory path, and is the same
    // filter the Dashboard's Gold/Silver/Bronze counts set before navigating here, so a
    // click-through from Home now shows up as a checked chip instead of an invisible filter.
    public bool IsTierLosslessFilter { get => _qualityTierFilter == "Gold"; set => SetQualityTierChip("Gold", value); }
    public bool IsTierHighFilter { get => _qualityTierFilter == "Silver"; set => SetQualityTierChip("Silver", value); }
    public bool IsTierLowFilter { get => _qualityTierFilter == "Bronze"; set => SetQualityTierChip("Bronze", value); }

    private void SetQualityTierChip(string tier, bool on)
    {
        if (on) QualityTierFilter = tier;
        else if (_qualityTierFilter == tier) QualityTierFilter = null;
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

    private int _selectedTrackCount;
    /// <summary>Real (loaded, non-placeholder) selected rows; what the selection bar shows.</summary>
    public int SelectedTrackCount
    {
        get => _selectedTrackCount;
        private set => this.RaiseAndSetIfChanged(ref _selectedTrackCount, value);
    }

    /// <summary>
    /// The rows that count as a user selection: never the shared "Loading…" placeholder, never
    /// a null slot. The selection bar is visible only when this is non-empty.
    /// </summary>
    internal static List<PlaylistTrackViewModel> RealSelection(IEnumerable<PlaylistTrackViewModel?> candidates) =>
        candidates.Where(t => t is { IsPlaceholder: false }).Select(t => t!).Distinct().ToList();

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
        set
        {
            this.RaiseAndSetIfChanged(ref _duplicateHashesFilter, value);
            this.RaisePropertyChanged(nameof(HasDuplicateFilter));
        }
    }

    /// <summary>"Find duplicates" narrowed the list; shown as a removable chip in the toolbar.</summary>
    public bool HasDuplicateFilter => _duplicateHashesFilter != null;

    private static readonly HashSet<string> FilterPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(SearchText), nameof(IsFilterAll), nameof(IsFilterDownloaded), nameof(IsFilterPending),
        nameof(IsFilterNeedsReview), nameof(IsFilterLiked), nameof(CamelotKeyFilter), nameof(QualityTierFilter),
        nameof(IsBouncerActive), nameof(VibeFilter), nameof(IsFilterFlac), nameof(IsFilterMp3), nameof(IsFilterWav),
        nameof(IsFilterLossless), nameof(IsFilterQualityGold), nameof(IsFilterQualityVerified),
        nameof(IsFilterQualityReview), nameof(DuplicateHashesFilter), nameof(FilterArtist), nameof(FilterTitle),
    };

    /// <summary>True when anything narrows the list. Drives the toolbar's "Clear" button and the
    /// "no matches" empty state (vs. the "this playlist is empty" one).</summary>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(_searchText) || !_isFilterAll || _isFilterLiked || _isFilterNeedsReview
        || !string.IsNullOrEmpty(_camelotKeyFilter) || !string.IsNullOrEmpty(_qualityTierFilter)
        || _isBouncerActive || !string.IsNullOrEmpty(_vibeFilter)
        || _isFilterFlac || _isFilterMp3 || _isFilterWav || _isFilterLossless
        || _isFilterQualityGold || _isFilterQualityVerified || _isFilterQualityReview
        || _duplicateHashesFilter != null
        || !string.IsNullOrWhiteSpace(_filterArtist) || !string.IsNullOrWhiteSpace(_filterTitle)
        || StyleFilters.Any(f => f.IsSelected);

    public System.Windows.Input.ICommand ClearFiltersCommand { get; private set; } = null!;

    public System.Windows.Input.ICommand ClearCamelotKeyFilterCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand ClearDuplicateFilterCommand { get; private set; } = null!;

    /// <summary>Resets every filter (search, status, quality, key, duplicates, style) in one go.</summary>
    public void ClearFilters()
    {
        _searchText = string.Empty; this.RaisePropertyChanged(nameof(SearchText));
        _filterArtist = string.Empty; this.RaisePropertyChanged(nameof(FilterArtist));
        _filterTitle = string.Empty; this.RaisePropertyChanged(nameof(FilterTitle));
        _isFilterNeedsReview = false; this.RaisePropertyChanged(nameof(IsFilterNeedsReview));
        _isFilterDownloaded = false; this.RaisePropertyChanged(nameof(IsFilterDownloaded));
        _isFilterPending = false; this.RaisePropertyChanged(nameof(IsFilterPending));
        _isFilterLiked = false; this.RaisePropertyChanged(nameof(IsFilterLiked));
        _isFilterAll = true; this.RaisePropertyChanged(nameof(IsFilterAll));
        _isFilterFlac = _isFilterMp3 = _isFilterWav = _isFilterLossless = false;
        _isFilterQualityGold = _isFilterQualityVerified = _isFilterQualityReview = false;
        foreach (var name in new[] { nameof(IsFilterFlac), nameof(IsFilterMp3), nameof(IsFilterWav), nameof(IsFilterLossless),
                                     nameof(IsFilterQualityGold), nameof(IsFilterQualityVerified), nameof(IsFilterQualityReview) })
            this.RaisePropertyChanged(name);
        _isBouncerActive = false; this.RaisePropertyChanged(nameof(IsBouncerActive));
        _vibeFilter = null; this.RaisePropertyChanged(nameof(VibeFilter));
        _camelotKeyFilter = null; this.RaisePropertyChanged(nameof(CamelotKeyFilter)); this.RaisePropertyChanged(nameof(HasCamelotKeyFilter));
        _qualityTierFilter = null; this.RaisePropertyChanged(nameof(QualityTierFilter));
        this.RaisePropertyChanged(nameof(IsTierLosslessFilter));
        this.RaisePropertyChanged(nameof(IsTierHighFilter));
        this.RaisePropertyChanged(nameof(IsTierLowFilter));
        DuplicateHashesFilter = null;
        foreach (var style in StyleFilters.Where(f => f.IsSelected).ToList())
        {
            style.PropertyChanged -= OnStyleFilterChanged;
            style.IsSelected = false;
            style.PropertyChanged += OnStyleFilterChanged;
        }
        _selectedStyleNames.Clear();
        ScheduleViewStateSave();
        this.RaisePropertyChanged(nameof(HasActiveFilters));
        RefreshFilteredTracks();
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
        set { this.RaiseAndSetIfChanged(ref _isFormatColumnVisible, value); RefitColumns(); }
    }

    private bool _isForensicsColumnVisible = true;
    public bool IsForensicsColumnVisible
    {
        get => _isForensicsColumnVisible;
        set { this.RaiseAndSetIfChanged(ref _isForensicsColumnVisible, value); RefitColumns(); }
    }

    private bool _isDurationColumnVisible = true;
    public bool IsDurationColumnVisible
    {
        get => _isDurationColumnVisible;
        set { this.RaiseAndSetIfChanged(ref _isDurationColumnVisible, value); RefitColumns(); }
    }

    // What is actually shown: your column choices, minus the least important columns when the
    // list is too narrow for them (e.g. with the context panel open) — see TrackListColumnLayout.
    private double _listWidth;
    private TrackListColumns _columns = new(true, true, true, true, true);

    /// <summary>Set by the view whenever the track list is resized.</summary>
    public double ListWidth
    {
        get => _listWidth;
        set { if (Math.Abs(_listWidth - value) < 1) return; _listWidth = value; RefitColumns(); RefitToolbar(); }
    }

    // Toolbar responsiveness (TrackListColumnLayout.FitToolbar): what doesn't fit moves into the
    // Filters menu instead of being clipped (the search box and chips used to be cut off mid-word).
    private (bool TierChips, bool StatusSegment) _toolbar = (true, true);
    public bool ShowTierChips => _toolbar.TierChips;
    public bool ShowStatusSegment => _toolbar.StatusSegment;
    public bool ShowFilterMenu => !_toolbar.TierChips;
    public bool IsToolbarNarrow => !_toolbar.StatusSegment;

    private void RefitToolbar()
    {
        var fitted = TrackListColumnLayout.FitToolbar(_listWidth);
        if (fitted == _toolbar) return;
        _toolbar = fitted;
        this.RaisePropertyChanged(nameof(ShowTierChips));
        this.RaisePropertyChanged(nameof(ShowStatusSegment));
        this.RaisePropertyChanged(nameof(ShowFilterMenu));
        this.RaisePropertyChanged(nameof(IsToolbarNarrow));
    }

    private bool _isScrolledDown;
    /// <summary>True once the track list is scrolled down past the first rows — the playlist header
    /// above it then collapses to a slim bar so more tracks fit on screen. Set by the view.</summary>
    public bool IsScrolledDown
    {
        get => _isScrolledDown;
        private set { this.RaiseAndSetIfChanged(ref _isScrolledDown, value); this.RaisePropertyChanged(nameof(IsHeaderExpanded)); }
    }

    public bool IsHeaderExpanded => !_isScrolledDown;

    /// <summary>Scroll position of the track list. Collapses past 60 px and only expands again near
    /// the top (8 px), so the header doesn't flicker around one threshold.</summary>
    public void OnListScrolled(double offsetY)
    {
        UpdateListScrollOffset(offsetY);
        if (!IsScrolledDown && offsetY > 60) IsScrolledDown = true;
        else if (IsScrolledDown && offsetY < 8) IsScrolledDown = false;
    }

    public bool ShowEnergyColumn => _columns.Energy;
    public bool ShowFormatColumn => _columns.Format;
    public bool ShowForensicsColumn => _columns.Forensics;
    public bool ShowDurationColumn => _columns.Duration;
    public bool ShowRatingColumn => _columns.Rating;
    public bool ShowPreparationColumn => _columns.Preparation;
    public bool ShowQueueAction => _columns.QueueAction;
    public bool ShowInsertAction => _columns.InsertAction;
    public bool ShowArtistColumn => _columns.ArtistColumn;
    /// <summary>Narrow list: the artist is shown as a second line under the title instead.</summary>
    public bool ShowArtistUnderTitle => !_columns.ArtistColumn;

    private void RefitColumns()
    {
        var fitted = TrackListColumnLayout.Fit(
            _listWidth,
            _isFormatColumnVisible,
            _isForensicsColumnVisible,
            _isDurationColumnVisible,
            _artistColumnWidth,
            _energyColumnWidth,
            _formatColumnWidth,
            _forensicsColumnWidth,
            _durationColumnWidth,
            _ratingColumnWidth,
            _preparationColumnWidth);
        if (fitted == _columns) return;
        _columns = fitted;
        this.RaisePropertyChanged(nameof(ShowEnergyColumn));
        this.RaisePropertyChanged(nameof(ShowFormatColumn));
        this.RaisePropertyChanged(nameof(ShowForensicsColumn));
        this.RaisePropertyChanged(nameof(ShowDurationColumn));
        this.RaisePropertyChanged(nameof(ShowRatingColumn));
        this.RaisePropertyChanged(nameof(ShowPreparationColumn));
        this.RaisePropertyChanged(nameof(ShowQueueAction));
        this.RaisePropertyChanged(nameof(ShowInsertAction));
        this.RaisePropertyChanged(nameof(ShowArtistColumn));
        this.RaisePropertyChanged(nameof(ShowArtistUnderTitle));
    }
    
    // ListBox Selection Binding
    private ObservableCollection<PlaylistTrackViewModel> _selectedTracks = new();
    private int _selectAllOperation;
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
        var replacement = RealSelection(selected);

        // The grid already reports a complete selection snapshot. Rebuilding that snapshot should
        // not run selection-state, harmonic and transition scheduling once for every added row.
        ReplaceSelectionContents(_selectedTracks, replacement, OnSelectionChanged);
        UpdateSelectionState();
    }

    internal static void ReplaceSelectionContents(
        ObservableCollection<PlaylistTrackViewModel> selectedTracks,
        IEnumerable<PlaylistTrackViewModel> replacement,
        NotifyCollectionChangedEventHandler selectionChangedHandler)
    {
        selectedTracks.CollectionChanged -= selectionChangedHandler;
        try
        {
            selectedTracks.Clear();
            foreach (var track in replacement) selectedTracks.Add(track);
        }
        finally
        {
            selectedTracks.CollectionChanged += selectionChangedHandler;
        }
    }

    private async Task SelectAllTracksAsync()
    {
        ClearSelection();
        var operation = ++_selectAllOperation;
        var source = FilteredTracks;
        var tracks = new List<PlaylistTrackViewModel>();

        if (source is VirtualizedTrackCollection virtualized)
        {
            await foreach (var page in virtualized.LoadRangeForSelectionAsync(0, virtualized.Count - 1))
            {
                if (operation != _selectAllOperation) return;
                tracks.AddRange(page);
            }
        }
        else
        {
            tracks.AddRange(RealSelection(source));
        }

        // Filtering, project navigation or Deselect All while pages were loading wins over this
        // older request; never resurrect a selection in a different track context.
        if (operation != _selectAllOperation || !ReferenceEquals(source, FilteredTracks)) return;

        var selection = RealSelection(tracks);
        ReplaceSelectionContents(_selectedTracks, selection, OnSelectionChanged);
        foreach (var track in selection) track.IsSelected = true;
        UpdateSelectionState();
    }

    public void ClearSelection()
    {
        _selectAllOperation++;
        ClearSelectionContents(_selectedTracks, OnSelectionChanged);
        UpdateSelectionState();
    }

    internal static void ClearSelectionContents(
        ObservableCollection<PlaylistTrackViewModel> selectedTracks,
        NotifyCollectionChangedEventHandler selectionChangedHandler)
    {
        // Clearing only the collection leaves row IsSelected flags and VirtualGrid's own selection
        // alive. Clear the flags as well so bulk actions cannot leave phantom selected rows behind.
        foreach (var track in selectedTracks.ToList()) track.IsSelected = false;
        ReplaceSelectionContents(selectedTracks, Array.Empty<PlaylistTrackViewModel>(), selectionChangedHandler);
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
            _selectedStyleNames = StyleFilters.Where(f => f.IsSelected).Select(f => f.Style.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            this.RaisePropertyChanged(nameof(HasActiveFilters));
            ScheduleViewStateSave();
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

                ApplyStyleSelectionToLoadedFilters();
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
        INotificationService? notificationService = null,
        SLSKDONET.Services.Library.ILibraryTrackListViewStateStore? viewStateStore = null)
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
        _viewStateStore = viewStateStore;

        ToggleMixModeCommand = ReactiveCommand.Create(() => IsMixModeEnabled = !IsMixModeEnabled);
        OpenMixTransitionCommand = ReactiveCommand.Create<PlaylistTrackViewModel?>(OpenMixTransition);
        SuggestReorderForBetterFlowCommand = ReactiveCommand.CreateFromTask(SuggestReorderForBetterFlowAsync);
        DismissFlowWarningCommand = ReactiveCommand.Create(() => { FlowWarningDismissed = true; });

        Hierarchical = new HierarchicalLibraryViewModel(config, downloadManager, artworkCache, eventBus);
        
        ToggleColumnFilterStripCommand = ReactiveCommand.Create(() => IsColumnFilterStripVisible = !IsColumnFilterStripVisible);

        SortByColumnCommand = ReactiveCommand.Create<TrackSortColumn>(ExecuteSortByColumn);

        SelectAllTracksCommand = ReactiveCommand.CreateFromTask(SelectAllTracksAsync);

        DeselectAllTracksCommand = ReactiveCommand.Create(ClearSelection);

        BulkDownloadCommand = ReactiveCommand.CreateFromTask(ExecuteBulkDownloadAsync);
        BulkRetryCommand = ReactiveCommand.CreateFromTask(ExecuteBulkRetryAsync);
        CopyToFolderCommand = ReactiveCommand.CreateFromTask(ExecuteCopyToFolderAsync);
        BulkCancelCommand = ReactiveCommand.CreateFromTask(ExecuteBulkCancelAsync);
        BulkExportCsvCommand = ReactiveCommand.CreateFromTask(ExecuteBulkExportCsvAsync);
        
        // Phase 18: Find Similar - triggers sonic match search
        FindSimilarCommand = ReactiveCommand.Create<PlaylistTrackViewModel>(ExecuteFindSimilar);

        // Phase 22: Search 2.1 - Split Results
        AddToCurrentPlaylistCommand = ReactiveCommand.CreateFromTask<PlaylistTrackViewModel>(ExecuteAddToCurrentPlaylistAsync);

        ClearFiltersCommand = ReactiveCommand.Create(ClearFilters);
        RetryLoadTracksCommand = ReactiveCommand.Create(RefreshFilteredTracks);
        ClearCamelotKeyFilterCommand = ReactiveCommand.Create(() => { CamelotKeyFilter = null; });
        ClearDuplicateFilterCommand = ReactiveCommand.Create(() => { DuplicateHashesFilter = null; RefreshFilteredTracks(); });

        // Any filter change re-evaluates HasActiveFilters (Clear button + empty-state wording).
        this.Changed
            .Where(e => e.PropertyName != null && FilterPropertyNames.Contains(e.PropertyName))
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasActiveFilters)))
            .DisposeWith(_disposables);

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
            // Timestamp before throttling: notifications raised while applying a restored snapshot
            // must not queue a second database rebuild after LoadProjectTracksAsync already queried
            // with that exact state. Genuine edits made afterward always carry a newer timestamp.
            .Select(_ => DateTime.UtcNow)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(changedAt => { if (changedAt > _lastViewStateRestoreAt) RefreshFilteredTracks(); })
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

        // A reorder saved from another surface (Flow Builder's "Save order", Automix, Combine,
        // Auto-Arrange) is a content change for the grid too: without this, going back to a
        // playlist edited elsewhere kept showing the old order until something else reloaded it.
        _disposables.Add(eventBus.GetEvent<PlaylistTrackOrderChangedEvent>().Subscribe(evt =>
        {
            var currentProjectId = _mainViewModel?.LibraryViewModel?.SelectedProject?.Id;
            if (currentProjectId == evt.PlaylistId)
                _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }));

        // A folder import adds LibraryEntries in batches and publishes ONE completion event; All
        // Tracks refreshes once on it (no per-file refresh storm, no "0 tracks" mid-import).
        _disposables.Add(eventBus.GetEvent<LibraryImportCompletedEvent>().Subscribe(evt =>
        {
            if (!evt.Result.ChangedLibrary) return;
            var currentProjectId = _mainViewModel?.LibraryViewModel?.SelectedProject?.Id;
            if (currentProjectId != null && currentProjectId != Guid.Empty) return;
            // Rows were already shown live as batches landed; only an upgrade (a better copy
            // replacing an existing row's file) needs the full reload.
            if (evt.Result.Upgraded == 0 && FilteredTracks is VirtualizedTrackCollection)
                _liveRowsRequest.OnNext(System.Reactive.Unit.Default);
            else
                _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
        }));

        // While a folder import runs, its committed batches appear in All Tracks every ~2 s with
        // their tag (or file-name) titles, in place: no full reload, visible rows keep their view
        // models, newest-first lists keep selection and scroll offset.
        _disposables.Add(eventBus.GetEvent<LibraryEntriesAddedEvent>()
            .Select(_ => System.Reactive.Unit.Default)
            .Merge(_liveRowsRequest)
            .Sample(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(unit => { _ = ShowNewlyImportedRowsAsync(); }));

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

        InitializeViewStatePersistence();

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
            SaveViewStateNow();
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

            // A different playlist: the old selection's rows are about to be disposed and the grid
            // drops its own selection when ItemsSource changes, so keeping them here left a "1
            // selected" bar over a list where nothing is highlighted.
            if (_selectedTracks.Count > 0) ClearSelection();
            
            // Cleanup existing
            foreach (var track in CurrentProjectTracks)
            {
               if (track is IDisposable disposable) disposable.Dispose();
            }
            CurrentProjectTracks.Clear();

            // Set up virtualization with the complete restored view, not just the global search box.
            // Project loading used to bypass column search, duplicates, Mix, and sort until another
            // filter happened to change, which made a restored snapshot look correct but query wrong.
            var virtualized = new VirtualizedTrackCollection(
                _logger,
                _libraryService,
                _eventBus,
                _artworkCache,
                job.Id,
                IsMixModeEnabled ? string.Empty : BuildEffectiveFilter(),
                IsMixModeEnabled ? true : (IsFilterDownloaded ? true : (IsFilterPending ? false : null)),
                IsMixModeEnabled ? null : DuplicateHashesFilter,
                camelotKeyFilter: IsMixModeEnabled ? null : CamelotKeyFilter,
                qualityTier: IsMixModeEnabled ? null : QualityTierFilter,
                sortColumn: SortColumn,
                sortDescending: SortDescending);

            // Subscribe to update LimitedTracks when data arrives
            virtualized.CollectionChanged += (s, e) => {
                 if (e.Action == NotifyCollectionChangedAction.Reset)
                 {
                     Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(LimitedTracks)));
                 }
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Dispose the outgoing collection (after assignment, same as RefreshFilteredTracks):
                // each one holds 7 app-wide event-bus subscriptions plus up to 40 pages of row
                // ViewModels, so skipping this leaked a live collection on every playlist switch —
                // every one kept processing every download-progress/analysis event for the rest of
                // the session, which is why the Library got steadily slower until a restart.
                var oldVtc = FilteredTracks as VirtualizedTrackCollection;
                FilteredTracks = virtualized;
                this.RaisePropertyChanged(nameof(LimitedTracks));
                oldVtc?.Dispose();
                _logger.LogInformation("Virtualized collection initialized for project {Title}", job.SourceTitle);
                RequestScrollRestore();
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
             RequestScrollRestore();
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
        RequestScrollRestore();

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
            bool isLossless = SLSKDONET.Services.Audio.AudioFormats.IsLossless(track.Model.Format);
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
            if (IsFilterLossless && SLSKDONET.Services.Audio.AudioFormats.IsLossless(fmt)) formatMatch = true;
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
        var count = RealSelection(SelectedTracks).Count;
        SelectedTrackCount = count;
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
        // Every row that is already loaded — not just the first 50, which left rows further down
        // the list without badges. Pages that aren't loaded yet are skipped (never force-loaded);
        // each page load raises CollectionChanged, which reschedules this pass.
        var source = FilteredTracks;
        int totalCount = source.Count;
        var virtualized = source as VirtualizedTrackCollection;
        var ordered = new List<PlaylistTrackViewModel?>(totalCount);
        for (int r = 0; r < totalCount; r++)
        {
            if (virtualized != null)
                ordered.Add(virtualized.TryGetLoaded(r, out var loadedRow) ? loadedRow : null);
            else
                ordered.Add(source[r]);
        }
        var firstLoaded = ordered.FirstOrDefault(r => r != null);
        if (firstLoaded == null) return;

        var playlistId = firstLoaded.Model?.PlaylistId ?? Guid.Empty;
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
            if (current == null) continue; // page not loaded yet
            bool isLastOverall = i == totalCount - 1;
            if (isLastOverall)
            {
                current.ShowMixTransitionBadge = false;
                current.NextPlaylistTrackId = null;
                continue;
            }
            if (i >= ordered.Count - 1 || ordered[i + 1] is not { } next)
            {
                // Its "next" row hasn't loaded yet. Leave it as-is; OnFilteredTracksChanged
                // reschedules this once the next page arrives.
                continue;
            }

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
    /// <remarks>
    /// The playlist's rows are read from the database, <b>not</b> from <see cref="FilteredTracks"/>.
    /// The grid is virtualized: only the pages the user scrolled through are loaded, so the old
    /// behaviour reordered just those rows and left every other row on its previous
    /// <c>SortOrder</c>. The playlist then held duplicate positions, <c>ORDER BY SortOrder</c> had
    /// no defined answer for them, and the grid, Cue Forge's browser and the player each showed a
    /// different order (and paged reads could repeat or skip rows). Reading the whole playlist and
    /// arranging all of it keeps the persisted order total and unambiguous.
    /// </remarks>
    private async Task SuggestReorderForBetterFlowAsync()
    {
        var playlistId = _mainViewModel?.LibraryViewModel?.SelectedProject?.Id
                         ?? FilteredTracks.FirstOrDefault()?.Model?.PlaylistId
                         ?? Guid.Empty;
        if (playlistId == Guid.Empty) return;

        // Whole playlist, in its current persisted order.
        var allTracks = await _libraryService.LoadPlaylistTracksAsync(playlistId);
        if (allTracks.Count < 2) return;

        // Only tracks whose audio is actually on disk can be meaningfully mixed/reordered — a
        // Pending/Review/OnHold row has no file to beatmatch or preview, and its stored BPM/key
        // (if any) may be stale metadata rather than analysis of a file we actually have. Model.
        // Status is this codebase's established "is the file really here" signal (see
        // PlaylistTrackViewModel.IsGhost's doc comment) — stronger than AvailabilityState.
        //
        // Rows that can't take part keep their slot in the playlist: SaveTrackOrderAsync treats a
        // partial list as a permutation among the positions its rows already occupy, so the
        // wanted-but-missing tracks don't jump around every time the set is re-arranged.
        var eligible = allTracks
            .Where(t => t.Status == TrackStatus.Downloaded
                        && !string.IsNullOrEmpty(t.ResolvedFilePath)
                        && System.IO.File.Exists(t.ResolvedFilePath))
            .ToList();
        if (eligible.Count < 2) return;

        // Capped at PlaylistOptimizer's own O(n²) safety limit — a playlist larger than that gets
        // its first MaxOptimizeTracks eligible tracks reordered rather than failing outright.
        var toOptimize = eligible.Take(Services.Playlist.PlaylistOptimizer.MaxOptimizeTracks).ToList();

        // The same track can legitimately appear more than once in a set (a replay); the optimizer
        // works on hashes, so keep every occurrence and move them together.
        var rowsByHash = toOptimize
            .Where(t => !string.IsNullOrEmpty(t.TrackUniqueHash))
            .GroupBy(t => t.TrackUniqueHash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (rowsByHash.Count < 2) return;

        try
        {
            var result = await _playlistOptimizer.OptimizeAsync(rowsByHash.Keys);
            if (result.OrderedHashes.Count < 2) return;

            var orderedModels = new List<PlaylistTrack>(toOptimize.Count);
            var seen = new HashSet<Guid>();
            foreach (var hash in result.OrderedHashes)
            {
                if (!rowsByHash.TryGetValue(hash, out var rows)) continue;
                foreach (var row in rows)
                {
                    if (seen.Add(row.Id)) orderedModels.Add(row);
                }
            }

            if (orderedModels.Count < 2) return;

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

        ClearSelection();

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

            ClearSelection();
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
        ClearSelection();
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
        ClearSelection();
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
                    EscapeCsvField(track.BPM.ToString("F2", NumFormat.Culture)),
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

            if (SelectedTracks.Any()) ClearSelection();
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
