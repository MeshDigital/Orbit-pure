using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;
using Avalonia.Controls.Selection;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using SLSKDONET.Events;
using SLSKDONET.Configuration;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Coordinator ViewModel for the Library page.
/// Delegates responsibilities to child ViewModels following Single Responsibility Principle.
/// </summary>
public partial class LibraryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private bool _isDisposed;

    private readonly ILogger<LibraryViewModel> _logger;
    private readonly INavigationService _navigationService;
    private readonly ImportHistoryViewModel _importHistoryViewModel;
    private readonly ILibraryService _libraryService;
    private ILifecycleProjectionService _lifecycleProjectionService;
    private readonly IEventBus _eventBus;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    
    // Core Dependencies
    private readonly SpotifyEnrichmentService _spotifyEnrichmentService;
    private readonly LibraryCacheService _libraryCacheService;
    private readonly IServiceProvider _serviceProvider;
    private readonly DatabaseService _databaseService;
    private readonly SmartCrateService _smartCrateService;
    private readonly DownloadManager _downloadManager;
    private readonly Services.Library.ColumnConfigurationService _columnConfigService;
    private readonly Configuration.AppConfig _appConfig;
    private readonly ConfigManager _configManager;
    private readonly Services.Library.PlaylistExportService _exportService;
    private readonly PlaylistIntelligenceService? _playlistIntelligenceService;
    private readonly ISavedDoublesService? _savedDoublesService;
    private readonly TrackSimilarityService? _trackSimilarityService;
    private readonly SimilarityIndex? _similarityIndex;
    private readonly TransitionStyleClassifier? _transitionStyleClassifier;
    
    public Library.ProjectListViewModel Projects { get; }
    public Library.TrackListViewModel Tracks { get; }
    public Library.TrackOperationsViewModel Operations { get; }
    public Library.SmartPlaylistViewModel SmartPlaylists { get; }
    public System.Collections.ObjectModel.ObservableCollection<ColumnDefinition> AvailableColumns { get; } = new();
    public LibrarySourcesViewModel LibrarySourcesViewModel { get; }
    public LibraryDoubleInspectorViewModel DoubleInspector { get; }
    public LibraryTrackInspectorViewModel TrackInspector { get; }
    public PlaylistIntelligenceViewModel Intelligence { get; }

    private Views.MainViewModel? _mainViewModel;
    public Views.MainViewModel? MainViewModel
    {
        get => _mainViewModel;
        private set { _mainViewModel = value; OnPropertyChanged(); }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private bool _isSourcesOpen = false;
    public bool IsSourcesOpen
    {
        get => _isSourcesOpen;
        set { _isSourcesOpen = value; OnPropertyChanged(); }
    }

    private System.Collections.ObjectModel.ObservableCollection<PlaylistJob> _deletedProjects = new();
    public System.Collections.ObjectModel.ObservableCollection<PlaylistJob> DeletedProjects
    {
        get => _deletedProjects;
        set { _deletedProjects = value; OnPropertyChanged(); }
    }

    private System.Collections.ObjectModel.ObservableCollection<OrphanedTrackViewModel> _orphanedTracks = new();
    public System.Collections.ObjectModel.ObservableCollection<OrphanedTrackViewModel> OrphanedTracks
    {
        get => _orphanedTracks;
        set { _orphanedTracks = value; OnPropertyChanged(); }
    }

    // Expose commonly used child properties for backward compatibility (XAML Bindings)
    public PlaylistJob? SelectedProject 
    { 
        get => Projects.SelectedProject;
        set => Projects.SelectedProject = value;
    }
    
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> CurrentProjectTracks
    {
        get => Tracks.CurrentProjectTracks;
        set => Tracks.CurrentProjectTracks = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set { _isEditMode = value; OnPropertyChanged(); }
    }

    private bool _isActiveDownloadsVisible;
    public bool IsActiveDownloadsVisible
    {
        get => _isActiveDownloadsVisible;
        set { _isActiveDownloadsVisible = value; OnPropertyChanged(); }
    }

    // Default expanded until user manually chooses a collapsed state.
    private bool _isNavigationCollapsed;
    private int _manualNavigationCollapseCount;
    private int _physicalOnDiskCount;
    private int _indexedCatalogCount;
    private int _staleIndexedCount;
    private int _ingestionBacklogCount;
    private int _desiredDownloadCount;
    private string _libraryLifecycleStatusMessage = string.Empty;
    private bool _isSmartPlaylistContext;
    private int _savedDoublesSidebarFocusRequestVersion;
    private readonly System.Collections.Generic.HashSet<string> _savedDoublePartnersForCurrentTrack = new(StringComparer.Ordinal);

    private const string IntelligenceTabUpgrade = "Upgrade";
    private const double SavedDoublePriorBonus = 0.03;

    public bool IsNavigationHoverAutoHideEnabled => _appConfig.LibraryNavigationAutoHideEnabled;

    public bool UseNewPlaylistSurface => _appConfig.UseNewPlaylistSurface;

    public int NavigationHoverAutoHideActivationCount => Math.Max(2, _appConfig.LibraryNavigationAutoHideActivationToggleCount);

    public bool IsNavigationHoverAutoHideArmed =>
        IsNavigationHoverAutoHideEnabled && _manualNavigationCollapseCount >= NavigationHoverAutoHideActivationCount;

    public bool IsNavigationCollapsed
    {
        get => _isNavigationCollapsed;
        set { SetProperty(ref _isNavigationCollapsed, value); }
    }

    public double LibrarySmartInsertMinConfidence
    {
        get => Intelligence.LibrarySmartInsertMinConfidence;
        set => Intelligence.LibrarySmartInsertMinConfidence = value;
    }

    public int LibrarySmartInsertStructureSensitivity
    {
        get => Intelligence.LibrarySmartInsertStructureSensitivity;
        set => Intelligence.LibrarySmartInsertStructureSensitivity = value;
    }

    public string LibrarySmartInsertThresholdPreset => Intelligence.LibrarySmartInsertThresholdPreset;

    public bool IsSmartInsertStrictPresetActive => Intelligence.IsSmartInsertStrictPresetActive;

    public bool IsSmartInsertNormalPresetActive => Intelligence.IsSmartInsertNormalPresetActive;

    public bool IsSmartInsertLoosePresetActive => Intelligence.IsSmartInsertLoosePresetActive;

    public bool IsLibraryIntelligencePanelVisible =>
        !_isSmartPlaylistContext && Projects.SelectedProject is { Id: var id } && id != Guid.Empty;

    public string LibraryIntelligencePlaylistTitle =>
        Projects.SelectedProject?.SourceTitle ?? "No playlist selected";

    public string SelectedLibraryIntelligenceTab
    {
        get => Intelligence.SelectedLibraryIntelligenceTab;
        set
        {
            if (Intelligence.FocusLibraryIntelligenceTab(value))
                RaiseLibraryIntelligenceTabStateChanged();
        }
    }

    public string? TrackExplainabilitySummary => TrackInspector.TrackExplainabilitySummary;

    public System.Collections.Generic.IReadOnlyList<string> TrackExplainabilityReasons => TrackInspector.TrackExplainabilityReasons;

    public bool IsTrackExplainabilityVisible => TrackInspector.IsTrackExplainabilityVisible;

    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> SimilarTracksPreview => TrackInspector.SimilarTracksPreview;

    public bool HasSimilarTracksPreview => TrackInspector.HasSimilarTracksPreview;

    public System.Collections.ObjectModel.ObservableCollection<Library.SavedDoubleViewModel> SavedDoubles { get; } = new();

    public bool HasSavedDoubles => SavedDoubles.Count > 0;

    public System.Collections.ObjectModel.ObservableCollection<Library.SavedDoubleViewModel> SavedDoublesForLeadTrack { get; } = new();

    public bool HasSavedDoublesForLeadTrack => SavedDoublesForLeadTrack.Count > 0;

    public System.Collections.Generic.IEnumerable<Library.SavedDoubleViewModel> SavedDoublesForLeadTrackPreview => SavedDoublesForLeadTrack.Take(4);

    public bool HasMoreSavedDoublesForLeadTrack => SavedDoublesForLeadTrack.Count > 4;

    public System.Collections.ObjectModel.ObservableCollection<Library.SavedDoubleViewModel> SavedDoublesForCurrentPlayerTrack { get; } = new();

    public bool HasSavedDoublesForCurrentPlayerTrack => SavedDoublesForCurrentPlayerTrack.Count > 0;

    public System.Collections.Generic.IEnumerable<Library.SavedDoubleViewModel> SavedDoublesForCurrentPlayerTrackPreview => SavedDoublesForCurrentPlayerTrack.Take(4);

    public bool HasMoreSavedDoublesForCurrentPlayerTrack => SavedDoublesForCurrentPlayerTrack.Count > 4;

    public int SavedDoublesSidebarFocusRequestVersion
    {
        get => _savedDoublesSidebarFocusRequestVersion;
        private set => SetProperty(ref _savedDoublesSidebarFocusRequestVersion, value);
    }

    public System.Collections.ObjectModel.ObservableCollection<Library.SuggestNextCandidateViewModel> SuggestNextCandidates => Intelligence.SuggestNextCandidates;

    public bool HasSuggestNextCandidates => SuggestNextCandidates.Count > 0;

    public bool IsSuggestNextLoading => Intelligence.IsSuggestNextLoading;

    public string SuggestNextInfoText => Intelligence.SuggestNextInfoText;

    public System.Collections.ObjectModel.ObservableCollection<Library.PlaylistUpgradeCandidateViewModel> PlaylistUpgradeCandidates => Intelligence.PlaylistUpgradeCandidates;

    public bool HasPlaylistUpgradeCandidates => PlaylistUpgradeCandidates.Count > 0;

    public bool IsPlaylistUpgradeLoading => Intelligence.IsPlaylistUpgradeLoading;

    public string PlaylistUpgradeInfoText => Intelligence.PlaylistUpgradeInfoText;

    public string SmartInsertFromLabel
    {
        get => Intelligence.SmartInsertFromLabel;
    }

    public string SmartInsertToLabel
    {
        get => Intelligence.SmartInsertToLabel;
    }

    public string SmartInsertContextSummary => Intelligence.SmartInsertContextSummary;

    public string SmartInsertPreparationHint
    {
        get => Intelligence.SmartInsertPreparationHint;
    }

    public bool IsSmartInsertPreparationHintVisible => Intelligence.IsSmartInsertPreparationHintVisible;

    public bool HasPendingSmartInsertContext => Intelligence.HasPendingSmartInsertContext;

    private void RaiseSmartInsertPresetStateChanged()
    {
        OnPropertyChanged(nameof(LibrarySmartInsertThresholdPreset));
        OnPropertyChanged(nameof(IsSmartInsertStrictPresetActive));
        OnPropertyChanged(nameof(IsSmartInsertNormalPresetActive));
        OnPropertyChanged(nameof(IsSmartInsertLoosePresetActive));
    }

    internal (double MinConfidence, int StructureSensitivity) GetSmartInsertSettingsSnapshot()
    {
        if (_appConfig is null)
            return (0.72, 55);

        return (
            Math.Clamp(_appConfig.LibrarySmartInsertMinConfidence, 0.0, 1.0),
            Math.Clamp(_appConfig.LibrarySmartInsertStructureSensitivity, 0, 100));
    }

    internal bool UpdateSmartInsertSettingsFromIntelligence(double minConfidence, int structureSensitivity)
    {
        if (_appConfig is null)
        {
            OnPropertyChanged(nameof(LibrarySmartInsertMinConfidence));
            OnPropertyChanged(nameof(LibrarySmartInsertStructureSensitivity));
            RaiseSmartInsertPresetStateChanged();
            return true;
        }

        var normalizedMin = Math.Clamp(minConfidence, 0.0, 1.0);
        var normalizedStructure = Math.Clamp(structureSensitivity, 0, 100);

        var changed = Math.Abs(_appConfig.LibrarySmartInsertMinConfidence - normalizedMin) >= 0.0001
            || _appConfig.LibrarySmartInsertStructureSensitivity != normalizedStructure;

        _appConfig.LibrarySmartInsertMinConfidence = normalizedMin;
        _appConfig.LibrarySmartInsertStructureSensitivity = normalizedStructure;

        OnPropertyChanged(nameof(LibrarySmartInsertMinConfidence));
        OnPropertyChanged(nameof(LibrarySmartInsertStructureSensitivity));
        RaiseSmartInsertPresetStateChanged();

        return changed;
    }

    private void RaiseLibraryIntelligenceTabStateChanged()
    {
        OnPropertyChanged(nameof(SelectedLibraryIntelligenceTab));
    }

    private void RaiseLibraryIntelligenceContextStateChanged()
    {
        OnPropertyChanged(nameof(IsLibraryIntelligencePanelVisible));
        OnPropertyChanged(nameof(LibraryIntelligencePlaylistTitle));
    }

    internal TrackSimilarityService? TrackSimilarityService => _trackSimilarityService;
    internal ILogger<LibraryViewModel> Logger => _logger;
    internal PlayerViewModel Player => _playerViewModel;

    internal static string BuildCamelotCompatibilityLabel(string? leftCamelot, string? rightCamelot)
    {
        if (!TryParseCamelot(leftCamelot, out var leftNumber, out var leftWheel) ||
            !TryParseCamelot(rightCamelot, out var rightNumber, out var rightWheel))
        {
            return "Key compatibility: Analyze both tracks";
        }

        var wheelDistance = Math.Abs(leftNumber - rightNumber);
        wheelDistance = Math.Min(wheelDistance, 12 - wheelDistance);
        var sameWheel = leftWheel == rightWheel;

        var verdict = (wheelDistance, sameWheel) switch
        {
            (0, true) => "Lock",
            (0, false) => "Relative",
            (1, true) => "Compatible",
            (1, false) => "Creative",
            (2, _) => "Stretch",
            _ => "Risky"
        };

        return $"Key: {leftCamelot} -> {rightCamelot} ({verdict})";
    }

    private static bool TryParseCamelot(string? camelot, out int number, out char wheel)
    {
        number = 0;
        wheel = 'A';

        if (string.IsNullOrWhiteSpace(camelot))
            return false;

        var trimmed = camelot.Trim().ToUpperInvariant();
        if (trimmed.Length < 2)
            return false;

        wheel = trimmed[^1];
        if (wheel is not ('A' or 'B'))
            return false;

        if (!int.TryParse(trimmed[..^1], out number))
            return false;

        return number is >= 1 and <= 12;
    }

    private void SetSmartPlaylistContextMode(bool enabled)
    {
        if (_isSmartPlaylistContext == enabled)
            return;

        _isSmartPlaylistContext = enabled;
        RaiseLibraryIntelligenceContextStateChanged();
    }

    internal void FocusLibraryIntelligenceTab(string tab)
    {
        SelectedLibraryIntelligenceTab = tab;
    }

    public int PhysicalOnDiskCount
    {
        get => _physicalOnDiskCount;
        private set => SetProperty(ref _physicalOnDiskCount, value);
    }

    public int IndexedCatalogCount
    {
        get => _indexedCatalogCount;
        private set => SetProperty(ref _indexedCatalogCount, value);
    }

    public int StaleIndexedCount
    {
        get => _staleIndexedCount;
        private set => SetProperty(ref _staleIndexedCount, value);
    }

    public int IngestionBacklogCount
    {
        get => _ingestionBacklogCount;
        private set => SetProperty(ref _ingestionBacklogCount, value);
    }

    public int DesiredDownloadCount
    {
        get => _desiredDownloadCount;
        private set => SetProperty(ref _desiredDownloadCount, value);
    }

    public string LibraryLifecycleStatusMessage
    {
        get => _libraryLifecycleStatusMessage;
        private set => SetProperty(ref _libraryLifecycleStatusMessage, value);
    }

    public string LibraryCountDifferentiationSummary =>
        $"Wanted downloads: {DesiredDownloadCount} • Ingestion backlog: {IngestionBacklogCount} • Physical on-disk indexed: {PhysicalOnDiskCount} • Stale indexed rows: {StaleIndexedCount}";

    private void RegisterManualNavigationCollapse()
    {
        _manualNavigationCollapseCount++;
        OnPropertyChanged(nameof(IsNavigationHoverAutoHideArmed));
    }

    private bool _isRemovalHistoryVisible;
    public bool IsRemovalHistoryVisible
    {
        get => _isRemovalHistoryVisible;
        set { SetProperty(ref _isRemovalHistoryVisible, value); }
    }

    private bool _isOrphanedTracksVisible;
    public bool IsOrphanedTracksVisible
    {
        get => _isOrphanedTracksVisible;
        set { SetProperty(ref _isOrphanedTracksVisible, value); }
    }

    private double _sidebarWidth = 420;
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set { SetProperty(ref _sidebarWidth, value); }
    }

    private readonly PlayerViewModel _playerViewModel;
    public PlayerViewModel PlayerViewModel => _playerViewModel;
    
    // Track View Customization
    public TrackViewSettings ViewSettings { get; } = new();
    
    // Help Panel
    private bool _isHelpPanelOpen;
    public bool IsHelpPanelOpen
    {
        get => _isHelpPanelOpen;
        set => SetProperty(ref _isHelpPanelOpen, value);
    }

    partial void InitializeCommands();

    public LibraryViewModel(
        ILogger<LibraryViewModel> logger,
        Library.ProjectListViewModel projects,
        Library.TrackListViewModel tracks,
        Library.TrackOperationsViewModel operations,
        Library.SmartPlaylistViewModel smartPlaylists,
        INavigationService navigationService,
        ImportHistoryViewModel importHistoryViewModel,
        ILibraryService libraryService,
        ILifecycleProjectionService lifecycleProjectionService,
        IEventBus eventBus,
        PlayerViewModel playerViewModel,
        IDialogService dialogService,
        INotificationService notificationService,
        SpotifyEnrichmentService spotifyEnrichmentService,
        LibraryCacheService libraryCacheService,
        LibrarySourcesViewModel librarySourcesViewModel,
        IServiceProvider serviceProvider,
        DatabaseService databaseService,
        SearchFilterViewModel searchFilters,
        SmartCrateService smartCrateService,
        DownloadManager downloadManager,
        Services.Library.ColumnConfigurationService columnConfigService,
        Configuration.AppConfig appConfig,
        ConfigManager configManager,
        Services.Library.PlaylistExportService exportService,
        PlaylistIntelligenceService? playlistIntelligenceService = null,
        ISavedDoublesService? savedDoublesService = null,
        TrackSimilarityService? trackSimilarityService = null,
        SimilarityIndex? similarityIndex = null,
        TransitionStyleClassifier? transitionStyleClassifier = null)
    {
        _logger = logger;
        _navigationService = navigationService;
        _importHistoryViewModel = importHistoryViewModel;
        _libraryService = libraryService;
        _lifecycleProjectionService = lifecycleProjectionService;
        _eventBus = eventBus;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _spotifyEnrichmentService = spotifyEnrichmentService;
        _playerViewModel = playerViewModel;
        _libraryCacheService = libraryCacheService;
        _serviceProvider = serviceProvider;
        _databaseService = databaseService;
        _smartCrateService = smartCrateService;
        _downloadManager = downloadManager;
        _columnConfigService = columnConfigService;
        _appConfig = appConfig;
        _configManager = configManager;
        _exportService = exportService;
        _playlistIntelligenceService = playlistIntelligenceService;
        _savedDoublesService = savedDoublesService;
        _trackSimilarityService = trackSimilarityService;
        _similarityIndex = similarityIndex;
        _transitionStyleClassifier = transitionStyleClassifier;
        LibrarySourcesViewModel = librarySourcesViewModel;

        _isNavigationCollapsed = _appConfig.LibraryNavigationCollapsed;

        Projects = projects;
        Tracks = tracks;
        Operations = operations;
        SmartPlaylists = smartPlaylists;
        DoubleInspector = new LibraryDoubleInspectorViewModel(this, _logger, _trackSimilarityService, _transitionStyleClassifier);
        TrackInspector = new LibraryTrackInspectorViewModel(this, _logger, _similarityIndex);
        Intelligence = new PlaylistIntelligenceViewModel(this, _trackSimilarityService);

        // Bridge TrackList and Operations for ContextMenu functionality
        Tracks.Operations = operations;

        // Load columns
        _ = InitializeColumnsAsync();

        InitializeCommands();

        // Wire up events
        Projects.ProjectSelected += OnProjectSelected;
        SmartPlaylists.SmartPlaylistSelected += OnSmartPlaylistSelected;
        Tracks.SelectedTracks.CollectionChanged += OnTrackSelectionChanged;
        _playerViewModel.PropertyChanged += OnPlayerViewModelPropertyChanged;
        _playerViewModel.Queue.CollectionChanged += OnPlayerQueueCollectionChanged;
        SavedDoubles.CollectionChanged += OnSavedDoublesCollectionChanged;
        SavedDoublesForLeadTrack.CollectionChanged += OnSavedDoublesForLeadTrackCollectionChanged;
        SavedDoublesForCurrentPlayerTrack.CollectionChanged += OnSavedDoublesForCurrentPlayerTrackCollectionChanged;
        

        
        _disposables.Add(_eventBus.GetEvent<ProjectAddedEvent>().Subscribe(OnProjectAdded));
        _disposables.Add(_eventBus.GetEvent<SearchRequestedEvent>().Subscribe(OnSearchRequested));
        _disposables.Add(_eventBus.GetEvent<FileIngestionQueuedEvent>().Subscribe(OnFileIngestionQueued));
        _disposables.Add(_eventBus.GetEvent<FileIngestionStartedEvent>().Subscribe(OnFileIngestionStarted));
        _disposables.Add(_eventBus.GetEvent<FileIngestionCompletedEvent>().Subscribe(OnFileIngestionCompleted));
        _disposables.Add(_eventBus.GetEvent<FileMissingDetectedEvent>().Subscribe(OnFileMissingDetected));
        
        // Startup background tasks
        Task.Run(() => _libraryService.SyncLibraryEntriesFromTracksAsync()).ConfigureAwait(false);
        _ = RefreshLifecycleMetricsAsync();
        Intelligence.SeedSuggestNextScaffoldCandidates();
        Intelligence.SeedPlaylistUpgradeScaffoldCandidates();
        _ = Intelligence.RefreshSuggestNextCandidatesAsync();
        _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();

        _ = RefreshSavedDoublesAsync();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                _disposables.Dispose();

                Projects.ProjectSelected -= OnProjectSelected;
                SmartPlaylists.SmartPlaylistSelected -= OnSmartPlaylistSelected;
                Tracks.SelectedTracks.CollectionChanged -= OnTrackSelectionChanged;
                _playerViewModel.PropertyChanged -= OnPlayerViewModelPropertyChanged;
                _playerViewModel.Queue.CollectionChanged -= OnPlayerQueueCollectionChanged;
                SavedDoubles.CollectionChanged -= OnSavedDoublesCollectionChanged;
                SavedDoublesForLeadTrack.CollectionChanged -= OnSavedDoublesForLeadTrackCollectionChanged;
                SavedDoublesForCurrentPlayerTrack.CollectionChanged -= OnSavedDoublesForCurrentPlayerTrackCollectionChanged;
                Intelligence.Dispose();
            }
            _isDisposed = true;
        }
    }

    private void OnSavedDoublesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSavedDoubles));
    }

    private void OnSavedDoublesForLeadTrackCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SavedDoublesForLeadTrackPreview));
        OnPropertyChanged(nameof(HasSavedDoublesForLeadTrack));
        OnPropertyChanged(nameof(HasMoreSavedDoublesForLeadTrack));
    }

    private void OnSavedDoublesForCurrentPlayerTrackCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SavedDoublesForCurrentPlayerTrackPreview));
        OnPropertyChanged(nameof(HasSavedDoublesForCurrentPlayerTrack));
        OnPropertyChanged(nameof(HasMoreSavedDoublesForCurrentPlayerTrack));
    }

    private void OnPlayerViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(PlayerViewModel.CurrentTrack), StringComparison.Ordinal) &&
            !string.Equals(e.PropertyName, nameof(PlayerViewModel.HasCurrentTrack), StringComparison.Ordinal))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshSavedDoublesForCurrentPlayerTrack();
            _ = Intelligence.RefreshSuggestNextCandidatesAsync();
            _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshSavedDoublesForCurrentPlayerTrack();
            _ = Intelligence.RefreshSuggestNextCandidatesAsync();
            _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();
        });
    }

    private void OnPlayerQueueCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateQueueSavedDoublePartnerFlags();
    }

    private void RefreshSavedDoublesForLeadTrack(PlaylistTrackViewModel? leadTrack)
    {
        SavedDoublesForLeadTrack.Clear();

        var leadTrackId = leadTrack?.GlobalId;
        if (string.IsNullOrWhiteSpace(leadTrackId))
        {
            OnPropertyChanged(nameof(SavedDoublesForLeadTrack));
            OnPropertyChanged(nameof(HasSavedDoublesForLeadTrack));
            return;
        }

        var matches = SavedDoubles
            .Where(saved =>
                string.Equals(saved.Model.TrackAId, leadTrackId, StringComparison.Ordinal) ||
                string.Equals(saved.Model.TrackBId, leadTrackId, StringComparison.Ordinal))
            .ToList();

        foreach (var saved in matches)
        {
            var hydrated = Library.SavedDoubleViewModel.TryCreate(saved.Model, ResolveTrackViewModel);
            if (hydrated is null)
                continue;

            hydrated.LeadTrackId = leadTrackId;

            SavedDoublesForLeadTrack.Add(hydrated);
        }

        OnPropertyChanged(nameof(SavedDoublesForLeadTrack));
        OnPropertyChanged(nameof(SavedDoublesForLeadTrackPreview));
        OnPropertyChanged(nameof(HasSavedDoublesForLeadTrack));
        OnPropertyChanged(nameof(HasMoreSavedDoublesForLeadTrack));
    }

    private void RefreshSavedDoublesForCurrentPlayerTrack()
    {
        SavedDoublesForCurrentPlayerTrack.Clear();
        _savedDoublePartnersForCurrentTrack.Clear();

        var currentTrackId = _playerViewModel.CurrentTrack?.GlobalId;
        if (string.IsNullOrWhiteSpace(currentTrackId))
        {
            UpdateQueueSavedDoublePartnerFlags();
            OnPropertyChanged(nameof(SavedDoublesForCurrentPlayerTrack));
            OnPropertyChanged(nameof(SavedDoublesForCurrentPlayerTrackPreview));
            OnPropertyChanged(nameof(HasSavedDoublesForCurrentPlayerTrack));
            OnPropertyChanged(nameof(HasMoreSavedDoublesForCurrentPlayerTrack));
            return;
        }

        var matches = SavedDoubles
            .Where(saved =>
                string.Equals(saved.Model.TrackAId, currentTrackId, StringComparison.Ordinal) ||
                string.Equals(saved.Model.TrackBId, currentTrackId, StringComparison.Ordinal))
            .ToList();

        foreach (var saved in matches)
        {
            var hydrated = Library.SavedDoubleViewModel.TryCreate(saved.Model, ResolveTrackViewModel);
            if (hydrated is null)
                continue;

            hydrated.LeadTrackId = currentTrackId;
            var counterpartId = string.Equals(saved.Model.TrackAId, currentTrackId, StringComparison.Ordinal)
                ? saved.Model.TrackBId
                : saved.Model.TrackAId;
            if (!string.IsNullOrWhiteSpace(counterpartId))
                _savedDoublePartnersForCurrentTrack.Add(counterpartId);
            SavedDoublesForCurrentPlayerTrack.Add(hydrated);
        }

        UpdateQueueSavedDoublePartnerFlags();

        OnPropertyChanged(nameof(SavedDoublesForCurrentPlayerTrack));
        OnPropertyChanged(nameof(SavedDoublesForCurrentPlayerTrackPreview));
        OnPropertyChanged(nameof(HasSavedDoublesForCurrentPlayerTrack));
        OnPropertyChanged(nameof(HasMoreSavedDoublesForCurrentPlayerTrack));
    }

    private void UpdateQueueSavedDoublePartnerFlags()
    {
        foreach (var track in _playerViewModel.Queue)
        {
            var trackId = track.GlobalId;
            track.IsSavedDoublePartner =
                !string.IsNullOrWhiteSpace(trackId) &&
                _savedDoublePartnersForCurrentTrack.Contains(trackId);
        }
    }

    private PlaylistTrackViewModel? ResolveTrackViewModel(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return null;

        return Tracks.FilteredTracks
            .Concat(Tracks.CurrentProjectTracks)
            .FirstOrDefault(track => string.Equals(track.GlobalId, trackId, StringComparison.Ordinal));
    }

    private async Task RefreshSavedDoublesAsync()
    {
        if (_savedDoublesService is null)
            return;

        var savedPairs = await _savedDoublesService.LoadAsync().ConfigureAwait(false);
        var resolved = savedPairs
            .Select(saved => Library.SavedDoubleViewModel.TryCreate(saved, ResolveTrackViewModel))
            .Where(saved => saved is not null)
            .Select(saved => saved!)
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SavedDoubles.Clear();
            foreach (var saved in resolved)
                SavedDoubles.Add(saved);

            var selected = Tracks.SelectedTracks.Count == 1
                ? Tracks.SelectedTracks.First()
                : null;
            RefreshSavedDoublesForLeadTrack(selected);
            RefreshSavedDoublesForCurrentPlayerTrack();
            _ = Intelligence.RefreshSuggestNextCandidatesAsync();
            _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();
        });
    }

    public void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void SetMainViewModel(Views.MainViewModel mainViewModel)
    {
        MainViewModel = mainViewModel;
        // Keep track list context aligned with the active main VM.
        if (Tracks != null)
        {
            Tracks.SetMainViewModel(mainViewModel);
        }
    }

    public void AddToPlaylist(PlaylistJob targetPlaylist, PlaylistTrackViewModel track)
    {
        // Drag/drop invokes this compatibility entry point; playlist add flow is owned elsewhere.
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private LibraryEntry MapEntityToLibraryEntry(PlaylistTrackEntity entity)
    {
        return new LibraryEntry
        {
            UniqueHash = entity.TrackUniqueHash,
            FilePath = entity.ResolvedFilePath,
            Title = entity.Title,
            Artist = entity.Artist,
            Album = entity.Album,
            Genres = entity.Genres,
            BPM = entity.BPM,
            MusicalKey = entity.MusicalKey,
            Bitrate = entity.Bitrate
        };
    }

    private void OnSearchRequested(SearchRequestedEvent evt)
    {
        _logger.LogInformation("🔍 Cross-Component Search Requested: {Query}", evt.Query);
        
        // Use Dispatcher to ensure UI updates on main thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() => 
        {
            // Update search query
            Tracks.SearchText = evt.Query?.Trim() ?? string.Empty;
            
            // Clear project selection to show "All Tracks"
            Projects.SelectedProject = null;

            // Force immediate refresh so the DataGrid reflects ad-hoc query requests reliably.
            Tracks.RefreshFilteredTracks();
            
            // Ensure the user lands on the Library view for ad-hoc search results.
            MainViewModel?.NavigateLibraryCommand?.Execute(null);
        });
    }

    private async Task InitializeColumnsAsync()
    {
        var columns = await _columnConfigService.LoadConfigurationAsync();
        foreach (var col in columns.OrderBy(c => c.DisplayOrder))
        {
            AvailableColumns.Add(col);
        }
    }

    private async Task RefreshLifecycleMetricsAsync()
    {
        try
        {
            var metrics = await _lifecycleProjectionService.ComputeMetricsAsync().ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyLifecycleMetrics(metrics);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh library lifecycle metrics");
        }
    }

    private void OnFileIngestionQueued(FileIngestionQueuedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
            ApplyFileIngestionQueued(evt));
    }

    private void OnFileIngestionStarted(FileIngestionStartedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
            ApplyFileIngestionStarted(evt));
    }

    private void OnFileIngestionCompleted(FileIngestionCompletedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
            ApplyFileIngestionCompleted(evt));
    }

    private void OnFileMissingDetected(FileMissingDetectedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
            ApplyFileMissingDetected(evt));
    }

    private void ApplyFileIngestionQueued(FileIngestionQueuedEvent evt)
    {
        var next = _lifecycleProjectionService.ApplyFileIngestionQueued(GetCurrentLifecycleMetrics());

        ApplyLifecycleMetrics(next);
        LibraryLifecycleStatusMessage = $"Ingestion pending: {System.IO.Path.GetFileName(evt.FilePath)}";
    }

    private void ApplyFileIngestionStarted(FileIngestionStartedEvent evt)
    {
        LibraryLifecycleStatusMessage = $"Ingestion started: {System.IO.Path.GetFileName(evt.FilePath)}";
    }

    private void ApplyFileIngestionCompleted(FileIngestionCompletedEvent evt)
    {
        var next = _lifecycleProjectionService.ApplyFileIngestionCompleted(GetCurrentLifecycleMetrics());

        ApplyLifecycleMetrics(next);
        LibraryLifecycleStatusMessage = $"Indexed: {System.IO.Path.GetFileName(evt.FilePath)}";
    }

    private void ApplyFileMissingDetected(FileMissingDetectedEvent evt)
    {
        var next = _lifecycleProjectionService.ApplyFileMissingDetected(GetCurrentLifecycleMetrics());

        ApplyLifecycleMetrics(next);
        LibraryLifecycleStatusMessage = $"Stale index detected: {System.IO.Path.GetFileName(evt.FilePath)}";
    }

    private LifecycleMetrics GetCurrentLifecycleMetrics()
    {
        return new LifecycleMetrics(
            PhysicalOnDiskCount,
            IndexedCatalogCount,
            StaleIndexedCount,
            IngestionBacklogCount,
            DesiredDownloadCount);
    }

    private void ApplyLifecycleMetrics(LifecycleMetrics metrics)
    {
        PhysicalOnDiskCount = metrics.PhysicalOnDisk;
        IndexedCatalogCount = metrics.IndexedCatalog;
        StaleIndexedCount = metrics.StaleIndexed;
        IngestionBacklogCount = metrics.IngestionBacklog;
        DesiredDownloadCount = metrics.DesiredDownloads;
        OnPropertyChanged(nameof(LibraryCountDifferentiationSummary));
    }
}
