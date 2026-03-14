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
    
    public Library.ProjectListViewModel Projects { get; }
    public Library.TrackListViewModel Tracks { get; }
    public Library.TrackOperationsViewModel Operations { get; }
    public Library.SmartPlaylistViewModel SmartPlaylists { get; }
    public System.Collections.ObjectModel.ObservableCollection<ColumnDefinition> AvailableColumns { get; } = new();
    public LibrarySourcesViewModel LibrarySourcesViewModel { get; }

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

    private bool _isNavigationCollapsed;
    public bool IsNavigationCollapsed
    {
        get => _isNavigationCollapsed;
        set { SetProperty(ref _isNavigationCollapsed, value); }
    }

    private bool _isRemovalHistoryVisible;
    public bool IsRemovalHistoryVisible
    {
        get => _isRemovalHistoryVisible;
        set { SetProperty(ref _isRemovalHistoryVisible, value); }
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
        Services.Library.ColumnConfigurationService columnConfigService)
    {
        _logger = logger;
        _navigationService = navigationService;
        _importHistoryViewModel = importHistoryViewModel;
        _libraryService = libraryService;
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
        LibrarySourcesViewModel = librarySourcesViewModel;

        Projects = projects;
        Tracks = tracks;
        Operations = operations;
        SmartPlaylists = smartPlaylists;

        // Load columns
        _ = InitializeColumnsAsync();

        InitializeCommands();

        // Wire up events
        Projects.ProjectSelected += OnProjectSelected;
        SmartPlaylists.SmartPlaylistSelected += OnSmartPlaylistSelected;
        Tracks.SelectedTracks.CollectionChanged += OnTrackSelectionChanged;
        

        
        _projectAddedSubscription = _eventBus.GetEvent<ProjectAddedEvent>().Subscribe(OnProjectAdded);
        
        // Startup background tasks
        Task.Run(() => _libraryService.SyncLibraryEntriesFromTracksAsync()).ConfigureAwait(false);
    }

    private readonly IDisposable _projectAddedSubscription;

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
                _projectAddedSubscription?.Dispose();
                
                Projects.ProjectSelected -= OnProjectSelected;
                SmartPlaylists.SmartPlaylistSelected -= OnSmartPlaylistSelected;
                Tracks.SelectedTracks.CollectionChanged -= OnTrackSelectionChanged;
            }
            _isDisposed = true;
        }
    }

    public void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void SetMainViewModel(Views.MainViewModel mainViewModel)
    {
        MainViewModel = mainViewModel;
        // FIX: Pass reference to child VM so it knows what project is selected
        if (Tracks != null)
        {
            Tracks.SetMainViewModel(mainViewModel);
        }
    }

    public void AddToPlaylist(PlaylistJob targetPlaylist, PlaylistTrackViewModel track)
    {
        // Simple shim for drag-and-drop
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
            Tracks.SearchText = evt.Query;
            
            // Clear project selection to show "All Tracks"
            Projects.SelectedProject = null;
            
            // Navigate to Library if not already there (MainViewModel handles this if we publish event)
            // But we are usually already in Library.
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
}
