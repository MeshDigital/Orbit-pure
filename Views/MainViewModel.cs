using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection; // Added for GetRequiredService
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Avalonia.Threading;
using System.Collections.Generic; // Added this using directive
using SLSKDONET.Models;
using System.Reactive.Linq;
using ReactiveUI;

namespace SLSKDONET.Views;

/// <summary>
/// Main window ViewModel - coordinates navigation and global app state.
/// Delegates responsibilities to specialized child ViewModels.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();
    private bool _isDisposed;

    private readonly ILogger<MainViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ISoulseekAdapter _soulseek;
    private readonly ISoulseekCredentialService _credentialService;
    private readonly INavigationService _navigationService;
    private readonly IEventBus _eventBus;
    private readonly DownloadManager _downloadManager;
    private readonly ISpotifyMetadataService _spotifyMetadata;
    private readonly SpotifyAuthService _spotifyAuth;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly NativeDependencyHealthService _dependencyHealthService; // Phase 10.5
    private readonly IDialogService _dialogService;
    private readonly ILibraryService _libraryService;
    private readonly GlobalHotkeyService _globalHotkeyService;

    // Child ViewModels
    public PlayerViewModel PlayerViewModel { get; }
    public LibraryViewModel LibraryViewModel { get; }
    public SearchViewModel SearchViewModel { get; }
    public ConnectionViewModel ConnectionViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    public HomeViewModel HomeViewModel { get; }
    public StatusBarViewModel StatusBar { get; }
    // Phase 24: Stem Workspace
    
    // Operation Glass Console: Unified Intelligence Center
    // Operation Glass Console: Unified Intelligence Center


    public event PropertyChangedEventHandler? PropertyChanged;

    // Navigation state
    private object? _currentPage;
    public object? CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }
    
    private PageType _currentPageType;
    public PageType CurrentPageType
    {
        get => _currentPageType;
        set => SetProperty(ref _currentPageType, value);
    }
    
    // ... (StatusText property omitted for brevity, keeping existing) ...

    // ... (UI State properties omitted for brevity, keeping existing) ...

    public MainViewModel(
        ILogger<MainViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        ISoulseekAdapter soulseek,
        ISoulseekCredentialService credentialService,
        INavigationService navigationService,
        PlayerViewModel playerViewModel,
        LibraryViewModel libraryViewModel,
        SearchViewModel searchViewModel,
        ConnectionViewModel connectionViewModel,
        SettingsViewModel settingsViewModel,
        HomeViewModel homeViewModel,
        DownloadManager downloadManager,
        ISpotifyMetadataService spotifyMetadata,
        SpotifyAuthService spotifyAuth,
        IFileInteractionService fileInteractionService,
        IEventBus eventBus,
        NativeDependencyHealthService dependencyHealthService,
        IDialogService dialogService,
        ILibraryService libraryService,
        GlobalHotkeyService globalHotkeyService)

    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _navigationService = navigationService;
        _fileInteractionService = fileInteractionService;
        _dependencyHealthService = dependencyHealthService; // Phase 10.5
        
        // Assign missing fields
        _eventBus = eventBus;
        _downloadManager = downloadManager;
        _spotifyMetadata = spotifyMetadata;
        _spotifyAuth = spotifyAuth;
        _dialogService = dialogService;
        _libraryService = libraryService;
        _globalHotkeyService = globalHotkeyService;

        PlayerViewModel = playerViewModel;
        LibraryViewModel = libraryViewModel;
        SearchViewModel = searchViewModel;
        ConnectionViewModel = connectionViewModel;
        SettingsViewModel = settingsViewModel;
        HomeViewModel = homeViewModel;
        StatusBar = new StatusBarViewModel(eventBus, _dependencyHealthService);
        
        // Initialize commands
        NavigateHomeCommand = new RelayCommand(NavigateToHome); // Phase 6D
        NavigateSearchCommand = new RelayCommand(NavigateToSearch);
        NavigateLibraryCommand = new RelayCommand(NavigateToLibrary);
        NavigateProjectsCommand = new RelayCommand(NavigateToProjects);
        NavigateSettingsCommand = new RelayCommand(NavigateToSettings);
        NavigateImportCommand = new RelayCommand(NavigateToImport); // Phase 6D
        PlayPauseCommand = new RelayCommand(() => PlayerViewModel.TogglePlayPauseCommand.Execute(null));
        FocusSearchCommand = new RelayCommand(FocusSearch);
        ToggleNavigationCommand = new RelayCommand(() => 
        {
            if (!IsNavigationMini && !IsNavigationCollapsed)
            {
                IsNavigationMini = true;
                IsNavigationCollapsed = false;
            }
            else if (IsNavigationMini)
            {
                IsNavigationMini = false;
                IsNavigationCollapsed = true;
            }
            else
            {
                IsNavigationMini = false;
                IsNavigationCollapsed = false;
            }
        });
        TogglePlayerCommand = new RelayCommand(() => IsPlayerSidebarVisible = !IsPlayerSidebarVisible);
        TogglePlayerLocationCommand = new RelayCommand(() => IsPlayerAtBottom = !IsPlayerAtBottom);
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ResetZoomCommand = new RelayCommand(ResetZoom);
        
        // Phase 24: Stem Workspace Toggle
        
        // Operation Glass Console Toggles
        ToggleZenModeCommand = new RelayCommand(ToggleZenMode);
        ToggleTopBarCommand = new RelayCommand(() => IsTopCommandBarVisible = !IsTopCommandBarVisible);


        // Spotify Hub Initialization (TODO: Phase 7 - Implement when needed)
        // Downloads Page Commands
        PauseAllDownloadsCommand = new RelayCommand(PauseAllDownloads);
        ResumeAllDownloadsCommand = new RelayCommand(ResumeAllDownloads);
        RetryAllFailedDownloadsCommand = new RelayCommand(RetryAllFailedDownloads); // NEW
        CancelDownloadsCommand = new RelayCommand(CancelAllowedDownloads);
        // Using generic RelayCommand<PlaylistTrackViewModel> for DeleteTrackCommand
        DeleteTrackCommand = new AsyncRelayCommand<PlaylistTrackViewModel>(DeleteTrackAsync);
        
        // Subscribe to EventBus events
        // Subscribe to EventBus events
        _disposables.Add(_eventBus.GetEvent<TrackUpdatedEvent>().Subscribe(evt => OnTrackUpdated(this, evt.Track)));
        _disposables.Add(_eventBus.GetEvent<ConnectionLifecycleStateChangedEvent>().Subscribe(HandleConnectionLifecycleChanged));
        _disposables.Add(_eventBus.GetEvent<TrackAddedEvent>().Subscribe(evt => OnTrackAdded(evt.TrackModel)));
        _disposables.Add(_eventBus.GetEvent<TrackRemovedEvent>().Subscribe(evt => OnTrackRemoved(evt.TrackGlobalId)));
        
        
        // Phase 12.7: Context Menu Requests
        _disposables.Add(_eventBus.GetEvent<RevealFileRequestEvent>().Subscribe(evt => 
            _fileInteractionService.RevealFileInExplorer(evt.FilePath)));
            
        _disposables.Add(_eventBus.GetEvent<AddToProjectRequestEvent>().Subscribe(evt => 
        {
             _ = OnAddToProjectRequested(evt.Tracks);
        }));
        
        // Glass Box Architecture: Analysis Queue Visibility


        // Phase 14: Forensic Lab Navigation (NOW REDIRECTED TO GLASS CONSOLE)


        // Phase 24: Stem Workspace Navigation



        // Phase 25: Generic Navigation Event
        _disposables.Add(_eventBus.GetEvent<NavigateToPageEvent>().Subscribe(evt =>
        {
            Dispatcher.UIThread.Post(() => _navigationService.NavigateTo(evt.PageName));
        }));
        // Sync initial state in case events fired before subscription
        
        // Local collection monitoring for stats
        AllGlobalTracks.CollectionChanged += (s, e) => 
        {
             OnPropertyChanged(nameof(SuccessfulCount));
             OnPropertyChanged(nameof(FailedCount));
             OnPropertyChanged(nameof(TodoCount));
             OnPropertyChanged(nameof(DownloadProgressPercentage));
        };
        
        // Set application version from assembly
        // Set application version
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var infoVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
            
            // Clean up the version string (e.g. remove commit hash if present)
            if (infoVersion != null && infoVersion.Contains('+'))
            {
                infoVersion = infoVersion.Split('+')[0];
            }

            ApplicationVersion = !string.IsNullOrEmpty(infoVersion) ? infoVersion : "0.1.0-alpha";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get application version");
            ApplicationVersion = "0.1.0-alpha";
        }

        // Set LibraryViewModel's MainViewModel reference
        LibraryViewModel.SetMainViewModel(this);

        _logger.LogInformation("MainViewModel initialized");

        // Sync Spotify auth state
        IsSpotifyAuthenticated = _spotifyAuth.IsAuthenticated;
        _spotifyAuth.AuthenticationChanged += OnSpotifyAuthChanged;

        // Register pages for navigation service
        _navigationService.RegisterPage("Home", typeof(Avalonia.HomePage));
        _navigationService.RegisterPage("Search", typeof(Avalonia.SearchPage));
        _navigationService.RegisterPage("Library", typeof(Avalonia.LibraryPage));
        _navigationService.RegisterPage("Projects", typeof(Avalonia.DownloadsPage));
        _navigationService.RegisterPage("Settings", typeof(Avalonia.SettingsPage));
        _navigationService.RegisterPage("Import", typeof(Avalonia.ImportPage));
        _navigationService.RegisterPage("ImportPreview", typeof(Avalonia.ImportPreviewPage));
        
        // Subscribe to navigation events
        _navigationService.Navigated += OnNavigated;

        // Navigate to Home page by default
        NavigateToHome();

        // Phase 7: Spotify Silent Refresh
        _ = InitializeSpotifyAsync();
    }

    private void OnSpotifyAuthChanged(object? sender, bool authenticated)
    {
        IsSpotifyAuthenticated = authenticated;
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
            _spotifyAuth.AuthenticationChanged -= OnSpotifyAuthChanged;
            _navigationService.Navigated -= OnNavigated;
            
            foreach (var track in AllGlobalTracks.ToList()) // ToList to avoid collection modified exception
            {
                track.Dispose();
            }
            AllGlobalTracks.Clear();

            // Explicitly dispose injected ViewModels that implement IDisposable
            PlayerViewModel?.Dispose();
            LibraryViewModel?.Dispose();
            SearchViewModel?.Dispose();
            SettingsViewModel?.Dispose();
            HomeViewModel?.Dispose();
            _globalHotkeyService?.Dispose();
        }

        _isDisposed = true;
    }

    private async Task InitializeSpotifyAsync()
    {
        try
        {
            if (_config.SpotifyUseApi && await _spotifyAuth.IsAuthenticatedAsync())
            {
                _logger.LogInformation("Spotify silent session refresh successful");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify silent refresh failed");
        }
    }

    private void NavigateToSettings()
    {
        // Safety: ensure Spotify auth UI isn't stuck disabled on arrival
        try { SettingsViewModel.IsAuthenticating = false; } catch {}
        _navigationService.NavigateTo("Settings");
    }


    // Connection logic moved to ConnectionViewModel
    // StatusText is now delegated/coordinated via ConnectionViewModel binding in UI
    // But MainViewModel might still need a status text for other things? 
    // For now we keep StatusText for "Initializing" status but binding in Main Window should point to ConnectionViewModel for connection status.
    // Simplifying MainViewModel:
    
    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    // UI State
    private bool _isNavigationCollapsed;
    public bool IsNavigationCollapsed
    {
        get => _isNavigationCollapsed;
        set => SetProperty(ref _isNavigationCollapsed, value);
    }

    private bool _isNavigationMini;
    public bool IsNavigationMini
    {
        get => _isNavigationMini;
        set => SetProperty(ref _isNavigationMini, value);
    }

    private bool _isZenMode;
    public bool IsZenMode
    {
        get => _isZenMode;
        set
        {
            if (SetProperty(ref _isZenMode, value))
            {
                if (value)
                {
                    IsNavigationCollapsed = true;
                    IsTopCommandBarVisible = false;
                    IsPlayerSidebarVisible = false;
                }
                else
                {
                    IsNavigationCollapsed = false;
                    IsTopCommandBarVisible = true;
                    IsPlayerSidebarVisible = true;
                }
            }
        }
    }

    private bool _isTopCommandBarVisible = true;
    public bool IsTopCommandBarVisible
    {
        get => _isTopCommandBarVisible;
        set => SetProperty(ref _isTopCommandBarVisible, value);
    }

    private bool _isAcquireVisible = true;
    public bool IsAcquireVisible
    {
        get => _isAcquireVisible;
        set => SetProperty(ref _isAcquireVisible, value);
    }

    private bool _isEnrichVisible = true;
    public bool IsEnrichVisible
    {
        get => _isEnrichVisible;
        set => SetProperty(ref _isEnrichVisible, value);
    }

    private bool _isCurateVisible = true;
    public bool IsCurateVisible
    {
        get => _isCurateVisible;
        set => SetProperty(ref _isCurateVisible, value);
    }

    private bool _isDeliverVisible = true;
    public bool IsDeliverVisible
    {
        get => _isDeliverVisible;
        set => SetProperty(ref _isDeliverVisible, value);
    }

    private bool _isSystemVisible = true;
    public bool IsSystemVisible
    {
        get => _isSystemVisible;
        set => SetProperty(ref _isSystemVisible, value);
    }

    private bool _isPlayerSidebarVisible = true;
    public bool IsPlayerSidebarVisible
    {
        get => _isPlayerSidebarVisible;
        set
        {
            if (SetProperty(ref _isPlayerSidebarVisible, value))
            {
                OnPropertyChanged(nameof(IsPlayerInSidebar));
                OnPropertyChanged(nameof(IsPlayerAtBottomVisible));
            }
        }
    }

    private bool _isPlayerAtBottom;
    public bool IsPlayerAtBottom
    {
        get => _isPlayerAtBottom;
        set
        {
            if (SetProperty(ref _isPlayerAtBottom, value))
            {
                OnPropertyChanged(nameof(IsPlayerInSidebar));
                OnPropertyChanged(nameof(IsPlayerAtBottomVisible));
            }
        }
    }




    // === Analysis Queue Status (Glass Box Architecture) ===
    
    private int _analysisQueueCount;
    public int AnalysisQueueCount
    {
        get => _analysisQueueCount;
        set
        {
            if (SetProperty(ref _analysisQueueCount, value))
            {
                OnPropertyChanged(nameof(HasActiveAnalysis));
                OnPropertyChanged(nameof(AnalysisETA));
                OnPropertyChanged(nameof(HasETA));
                OnPropertyChanged(nameof(IsGlobalActivityActive)); // Notify unified activity
            }
        }
    }

    private int _analysisProcessedCount;
    public int AnalysisProcessedCount
    {
        get => _analysisProcessedCount;
        set => SetProperty(ref _analysisProcessedCount, value);
    }

    public bool HasActiveAnalysis => AnalysisQueueCount > 0;

    private bool _isAnalysisPaused;
    public bool IsAnalysisPaused
    {
        get => _isAnalysisPaused;
        set
        {
            if (SetProperty(ref _isAnalysisPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonTooltip));
                OnPropertyChanged(nameof(PauseButtonIcon));
            }
        }
    }

    public string PauseButtonTooltip => IsAnalysisPaused 
        ? "Resume Analysis (CPU saver mode active)" 
        : "Pause Analysis (save CPU for gaming/other tasks)";

    public string PauseButtonIcon => IsAnalysisPaused 
        ? "play_regular" 
        : "pause_regular";

    public string? AnalysisETA
    {
        get
        {
            if (AnalysisQueueCount == 0) return null;
            
            // Estimate: ~2 seconds per track
            int seconds = AnalysisQueueCount * 2;
            
            if (seconds < 60)
                return $"~{seconds}s";
            
            int minutes = seconds / 60;
            if (minutes < 60)
                return $"~{minutes}m";
            
            int hours = minutes / 60;
            int remainingMinutes = minutes % 60;
            return $"~{hours}h {remainingMinutes}m";
        }
    }

    public bool HasETA => !string.IsNullOrEmpty(AnalysisETA);


    public bool IsPlayerInSidebar => !IsPlayerAtBottom && IsPlayerSidebarVisible && CurrentPageType != PageType.TheaterMode;
    public bool IsPlayerAtBottomVisible => IsPlayerAtBottom && CurrentPageType != PageType.TheaterMode;

    private double _baseFontSize = 14.0;
    public double BaseFontSize
    {
        get => _baseFontSize;
        set
        {
            if (SetProperty(ref _baseFontSize, Math.Clamp(value, 8.0, 24.0)))
            {
                UpdateFontSizeResources();
                OnPropertyChanged(nameof(FontSizeSmall));
                OnPropertyChanged(nameof(FontSizeMedium));
                OnPropertyChanged(nameof(FontSizeLarge));
                OnPropertyChanged(nameof(UIScalePercentage));
            }
        }
    }

    public double FontSizeSmall => BaseFontSize * 0.85;
    public double FontSizeMedium => BaseFontSize;
    public double FontSizeLarge => BaseFontSize * 1.2;
    public string UIScalePercentage => $"{(BaseFontSize / 14.0):P0}";

    private string _applicationVersion = "Unknown";
    public string ApplicationVersion
    {
        get => _applicationVersion;
        set => SetProperty(ref _applicationVersion, value);
    }

    private bool _isInitializing = true;
    public bool IsInitializing
    {
        get => _isInitializing;
        set 
        {
            if (SetProperty(ref _isInitializing, value))
            {
                OnPropertyChanged(nameof(IsGlobalActivityActive));
            }
        }
    }
    
    // Computed property to drive the global activity spinner
    public bool IsGlobalActivityActive 
    {
        get => (TodoCount > 0) || (AnalysisQueueCount > 0) || IsInitializing;
    }

    // Phase 7: Spotify Hub Properties
    private bool _isSpotifyAuthenticated;
    public bool IsSpotifyAuthenticated
    {
        get => _isSpotifyAuthenticated;
        set => SetProperty(ref _isSpotifyAuthenticated, value);
    }

    // TODO: Phase 7 - Spotify Hub


    // Event-Driven Collection
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> AllGlobalTracks { get; } = new();
    
    // Filtered Collection for Downloads Page
    private System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> _filteredGlobalTracks = new();
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> FilteredGlobalTracks
    {
        get => _filteredGlobalTracks;
        set => SetProperty(ref _filteredGlobalTracks, value);
    }
    
    private string _downloadsSearchText = "";
    public string DownloadsSearchText
    {
        get => _downloadsSearchText;
        set
        {
            if (SetProperty(ref _downloadsSearchText, value))
            {
                 UpdateDownloadsFilter();
            }
        }
    }

    private int _downloadsFilterIndex = 0; 
    public int DownloadsFilterIndex
    {
        get => _downloadsFilterIndex;
        set
        {
            if (SetProperty(ref _downloadsFilterIndex, value))
            {
                UpdateDownloadsFilter();
            }
        }
    }

    // Navigation Commands

    public ICommand NavigateHomeCommand { get; } // Phase 6D
    public ICommand NavigateSearchCommand { get; }
    public ICommand NavigateLibraryCommand { get; }
    public ICommand NavigateProjectsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateImportCommand { get; } // Phase 6D
    public ICommand PlayPauseCommand { get; }
    public ICommand FocusSearchCommand { get; }
    public ICommand ToggleNavigationCommand { get; }
    public ICommand TogglePlayerCommand { get; }
    public ICommand TogglePlayerLocationCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand RetryAllFailedDownloadsCommand { get; }
    public ICommand ToggleZenModeCommand { get; }
    public ICommand ToggleTopBarCommand { get; }
    
    public bool IsGlobalSidebarOpen => false;

    
    // Downloads Page Commands
    public ICommand PauseAllDownloadsCommand { get; }
    public ICommand ResumeAllDownloadsCommand { get; }
    public ICommand CancelDownloadsCommand { get; }
    public ICommand DeleteTrackCommand { get; }

    // Page instances (lazy-loaded)
    // Lazy-loaded page instances
    // Page instances no longer needed here as they are managed by NavigationService

    private void OnNavigated(object? sender, global::Avalonia.Controls.UserControl page)
    {
        if (page != null)
        {
            CurrentPage = page;
            string pageName = page.GetType().Name;
            
            // Sync CurrentPageType based on the view type to keep UI highlights correct
            CurrentPageType = pageName switch
            {
                "HomePage" => PageType.Home,
                "SearchPage" => PageType.Search,
                "LibraryPage" => PageType.Library,
                "DownloadsPage" => PageType.Projects,
                "SettingsPage" => PageType.Settings,
                "ImportPage" => PageType.Import,
                "ImportPreviewPage" => PageType.Import, // Map preview to Import category
                _ => CurrentPageType
            };

            // Handle Theater Mode Layout (Navigation is special because it affects sidebar size)
            if (CurrentPageType == PageType.TheaterMode)
            {
                IsNavigationCollapsed = true;
            }
            else
            {
                // Restore defaults if we weren't already collapsed
                IsNavigationCollapsed = false; 
            }

            // Player visibility is now computed based on CurrentPageType
            OnPropertyChanged(nameof(IsPlayerInSidebar));
            OnPropertyChanged(nameof(IsPlayerAtBottomVisible));
            OnPropertyChanged(nameof(IsGlobalSidebarOpen));
            
            _logger.LogInformation("Navigation sync: CurrentPage updated to {PageType}", CurrentPageType);

            // Structure Fix B.2: Reset Search State on Navigation
            // If we have navigated away from Search (or just generally navigating), ensure search state is clean
            // unless we are specifically in a search-related flow (like ImportPreview).
            // But user requested "whenever a navigation event occurs".
            // We'll reset if we are NOT on Search page anymore.
            if (CurrentPageType != PageType.Search)
            {
               SearchViewModel.ResetState();
            }
        }
    }

    // Navigation Methods (lazy-loading pattern)

    private void NavigateToHome()
    {
        _navigationService.NavigateTo("Home");
    }

    private void NavigateToSearch()
    {
        _navigationService.NavigateTo("Search");
    }

    private void FocusSearch()
    {
        // Navigate to search page and focus the search box
        NavigateToSearch();
        // For now, just navigate. In a full implementation, we'd use a focus protocol
    }

    private void ToggleZenMode()
    {
        IsZenMode = !IsZenMode;
    }

    private void NavigateToLibrary()
    {
        _navigationService.NavigateTo("Library");
    }

    private void NavigateToProjects()
    {
        _navigationService.NavigateTo("Projects");
    }

    private void NavigateToImport()
    {
        _navigationService.NavigateTo("Import");
    }

    private void UpdateFontSizeResources()
    {
        if (global::Avalonia.Application.Current?.Resources != null)
        {
            global::Avalonia.Application.Current.Resources["FontSizeSmall"] = BaseFontSize * 0.85;
            global::Avalonia.Application.Current.Resources["FontSizeMedium"] = BaseFontSize;
            global::Avalonia.Application.Current.Resources["FontSizeLarge"] = BaseFontSize * 1.2;
            global::Avalonia.Application.Current.Resources["FontSizeXLarge"] = BaseFontSize * 1.4;
        }
    }



    private void ZoomIn() => BaseFontSize += 1;
    private void ZoomOut() => BaseFontSize -= 1;
    private void ResetZoom() => BaseFontSize = 14.0;

    // Download Progress Properties (computed from AllGlobalTracks)
    public int SuccessfulCount => AllGlobalTracks.Count(t => t.State == PlaylistTrackState.Completed);
    public int FailedCount => AllGlobalTracks.Count(t => t.State == PlaylistTrackState.Failed);
    public int TodoCount => AllGlobalTracks.Count(t => t.State == PlaylistTrackState.Pending);
    
    // In OnTrackUpdated, TodoCount changes will now also notify IsGlobalActivityActive
    public double DownloadProgressPercentage
    {
        get
        {
            var total = AllGlobalTracks.Count;
            if (total == 0) return 0;
            var completed = AllGlobalTracks.Count(t => t.State == PlaylistTrackState.Completed);
            return (double)completed / total * 100;
        }
    }

    // Event Handlers for Global Status
    private void OnTrackUpdated(object? sender, PlaylistTrackViewModel track)
    {
        // Trigger UI updates for aggregate stats on UI thread
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
        {
            OnPropertyChanged(nameof(SuccessfulCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(TodoCount));
            OnPropertyChanged(nameof(DownloadProgressPercentage));
            OnPropertyChanged(nameof(IsGlobalActivityActive)); // Notify unified activity
        });
    }

    private void HandleConnectionLifecycleChanged(ConnectionLifecycleStateChangedEvent evt)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
        {
            if (evt.Current == nameof(ConnectionLifecycleState.LoggedIn))
            {
                StatusText = "Ready";
            }
            else if (evt.Current == nameof(ConnectionLifecycleState.Connecting))
            {
                StatusText = "Connecting...";
            }
            else if (evt.Current == nameof(ConnectionLifecycleState.LoggingIn))
            {
                StatusText = "Logging in...";
            }
            else if (evt.Current == nameof(ConnectionLifecycleState.CoolingDown))
            {
                StatusText = "Cooling down before reconnect...";
            }
            else if (evt.Current == nameof(ConnectionLifecycleState.Disconnected)
                  && (evt.Reason.StartsWith("login rejected:", StringComparison.OrdinalIgnoreCase)
                   || evt.Reason.StartsWith("connect failed:", StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = "Connection failed";
            }
        });
    }

    private void OnTrackAdded(PlaylistTrack trackModel)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
        {
            var vm = new PlaylistTrackViewModel(trackModel, _eventBus);
            AllGlobalTracks.Add(vm);
            UpdateDownloadsFilter(); // Refresh filter
        });
    }

    private void OnTrackRemoved(string globalId)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
        {
            var toRemove = System.Linq.Enumerable.FirstOrDefault(AllGlobalTracks, t => t.GlobalId == globalId);
            if (toRemove != null)
            {
                AllGlobalTracks.Remove(toRemove);
                toRemove.Dispose(); // Fix Memory Leak: Dispose the removed track!
                UpdateDownloadsFilter(); // Refresh filter
            }

        });
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }




    private void UpdateDownloadsFilter()
    {
        var search = DownloadsSearchText.Trim();
        var filterIdx = DownloadsFilterIndex;

        IEnumerable<PlaylistTrackViewModel> query = AllGlobalTracks;

        // 1. Apply Search
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t => 
                (t.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // 2. Apply State Filter
        // 0=All, 1=Downloading, 2=Completed, 3=Failed, 4=Pending
        if (filterIdx > 0)
        {
            query = filterIdx switch
            {
                1 => query.Where(t => t.State == PlaylistTrackState.Downloading),
                2 => query.Where(t => t.State == PlaylistTrackState.Completed),
                3 => query.Where(t => t.State == PlaylistTrackState.Failed),
                4 => query.Where(t => t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Searching || t.State == PlaylistTrackState.Queued),
                _ => query
            };
        }

        // Update ObservableCollection
        // Note: For large lists this is inefficient, but for <1000 downloads it's fine for now.
        // Optimization: Use DynamicData or similar if list grows large.
        FilteredGlobalTracks = new System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel>(query.ToList());
    }

    // Command Implementations
    private async void PauseAllDownloads()
    {
        foreach (var track in AllGlobalTracks.Where(t => t.CanPause))
        {
            await _downloadManager.PauseTrackAsync(track.GlobalId);
        }
    }

    private async void ResumeAllDownloads()
    {
        foreach (var track in AllGlobalTracks.Where(t => t.State == PlaylistTrackState.Paused))
        {
            await _downloadManager.ResumeTrackAsync(track.GlobalId);
        }
    }

    private async void RetryAllFailedDownloads()
    {
        var failed = AllGlobalTracks.Where(t => t.State == PlaylistTrackState.Failed).ToList();
        _logger.LogInformation("Retrying {Count} failed downloads", failed.Count);
        foreach (var track in failed)
        {
            await _downloadManager.ResumeTrackAsync(track.GlobalId);
        }
    }

    private void CancelAllowedDownloads()
    {
        foreach (var track in AllGlobalTracks.Where(t => t.CanCancel))
        {
            _downloadManager.CancelTrack(track.GlobalId);
        }
    }

    private async Task DeleteTrackAsync(PlaylistTrackViewModel? track)
    {
        if (track == null) return;
        await _downloadManager.DeleteTrackFromDiskAndHistoryAsync(track.GlobalId);
    }



    private async Task OnAddToProjectRequested(IEnumerable<PlaylistTrack> tracks)
    {
        try
        {
            var trackList = tracks.ToList();
            if (!trackList.Any()) return;

            _logger.LogInformation("Showing project picker for {Count} tracks", trackList.Count);

            // 1. Load all projects
            var projects = await _libraryService.LoadAllPlaylistJobsAsync();
            
            // Filter out "All Tracks" (Guid.Empty) if it's in the list
            projects = projects.Where(p => p.Id != Guid.Empty).ToList();

            if (!projects.Any())
            {
                _logger.LogWarning("No projects found to add tracks to");
                return;
            }

            // 2. Show Picker
            var selectedProject = await _dialogService.ShowProjectPickerAsync(projects);
            if (selectedProject != null)
            {
                _logger.LogInformation("Adding tracks to project: {Title}", selectedProject.SourceTitle);
                
                // 3. Perform addition
                await _libraryService.AddTracksToProjectAsync(trackList, selectedProject.Id);
                
                // 4. Show success notification
                string message = trackList.Count == 1 
                    ? $"Added '{trackList[0].Title}' to '{selectedProject.SourceTitle}'"
                    : $"Added {trackList.Count} tracks to '{selectedProject.SourceTitle}'";
                
                StatusText = message;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle Add to Project request");
        }
    }
}
