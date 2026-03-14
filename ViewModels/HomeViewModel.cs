using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Services.Models;
using SLSKDONET.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using SLSKDONET.Views;
using System.Reactive.Linq;
using System.Collections.Generic;

namespace SLSKDONET.ViewModels;

public class HomeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILogger<HomeViewModel> _logger;
    private readonly DashboardService _dashboardService;
    private readonly INavigationService _navigationService;
    private readonly ConnectionViewModel _connectionViewModel;
    private readonly DatabaseService _databaseService;
    private readonly LibraryViewModel _libraryViewModel;
    private readonly SpotifyAuthService _spotifyAuth;
    private readonly SpotifyEnrichmentService _spotifyEnrichment;
    private readonly DownloadManager _downloadManager;
    private readonly Downloads.DownloadCenterViewModel _downloadCenter; // Inject for stats
    private readonly CrashRecoveryJournal _crashJournal; // Phase 3A: Transparency
    private readonly INotificationService _notificationService;
    private readonly IEventBus _eventBus;
    private IDisposable? _eventSubscription;
    private PropertyChangedEventHandler? _connectionChangedHandler;
    private bool _isDisposed;


    public event PropertyChangedEventHandler? PropertyChanged;

    private LibraryHealthEntity? _libraryHealth;
    public LibraryHealthEntity? LibraryHealth
    {
        get => _libraryHealth;
        set 
        {
            if (SetProperty(ref _libraryHealth, value))
            {
                OnPropertyChanged(nameof(PurityPercent));
                OnPropertyChanged(nameof(PurityStatus));
            }
        }
    }

    public double PurityPercent
    {
        get
        {
            if (LibraryHealth == null || LibraryHealth.TotalTracks == 0) return 0;
            return (double)LibraryHealth.GoldCount / LibraryHealth.TotalTracks * 100;
        }
    }

    public string PurityStatus => PurityPercent switch
    {
        >= 90 => "Audiophile",
        >= 70 => "Excellent",
        >= 50 => "Good",
        _ => "Needs Upgrades"
    };

    public ObservableCollection<PlaylistCardViewModel> RecentPlaylists { get; } = new();
    public ObservableCollection<SpotifyTrackViewModel> SpotifyRecommendations { get; } = new();

    private bool _isLoadingHealth = true;
    public bool IsLoadingHealth
    {
        get => _isLoadingHealth;
        set => SetProperty(ref _isLoadingHealth, value);
    }
    
    // Commands
    public ICommand RefreshDashboardCommand { get; }
    public ICommand NavigateToSearchCommand { get; }
    public ICommand QuickSearchCommand { get; }
    public ICommand ClearDeadLettersCommand { get; }
    public ICommand NavigateLibraryCommand { get; }
    public ICommand ViewPlaylistCommand { get; }


    public ObservableCollection<GenrePlanetViewModel> TopGenres { get; } = new();

    private DashboardSnapshot _currentSnapshot = new();

    public DashboardSnapshot CurrentSnapshot
    {
        get => _currentSnapshot;
        set => SetProperty(ref _currentSnapshot, value);
    }

    public ObservableCollection<string> ResilienceLog { get; } = new();



    private bool _isLoadingRecent;
    public bool IsLoadingRecent
    {
        get => _isLoadingRecent;
        set => SetProperty(ref _isLoadingRecent, value);
    }

    private bool _isLoadingSpotify;
    public bool IsLoadingSpotify
    {
        get => _isLoadingSpotify;
        set => SetProperty(ref _isLoadingSpotify, value);
    }

    public bool IsSpotifyConnected => _spotifyAuth.IsAuthenticated;
    
    public ObservableCollection<MissionOperation> ActiveMissions { get; } = new();

    public HomeViewModel(
        ILogger<HomeViewModel> logger,
        DashboardService dashboardService,
        INavigationService navigationService,
        ConnectionViewModel connectionViewModel,
        DatabaseService databaseService,
        SpotifyAuthService spotifyAuth,
        SpotifyEnrichmentService spotifyEnrichment,
        DownloadManager downloadManager,
        Downloads.DownloadCenterViewModel downloadCenter,
        CrashRecoveryJournal crashJournal,
        INotificationService notificationService,
        IEventBus eventBus,
        LibraryViewModel libraryViewModel)
    {
        _logger = logger;
        _dashboardService = dashboardService;
        _navigationService = navigationService;
        _connectionViewModel = connectionViewModel;
        _databaseService = databaseService;
        _spotifyAuth = spotifyAuth;
        _spotifyEnrichment = spotifyEnrichment;
        _downloadManager = downloadManager;
        _downloadCenter = downloadCenter;
        _crashJournal = crashJournal;
        _notificationService = notificationService;
        _eventBus = eventBus;
        _libraryViewModel = libraryViewModel;

        // Subscribe to Mission Control Updates (Smart Throttled & IEquatable)
        _eventSubscription = _eventBus.GetEvent<DashboardSnapshot>().Subscribe(snapshot =>
        {
            // The constraint: Use DashboardSnapshot.Equals (from IEquatable)
            if (snapshot.Equals(CurrentSnapshot)) return;

            Dispatcher.UIThread.Post(() =>
            {
                CurrentSnapshot = snapshot;
                
                // Update UI Collections
                UpdateResilienceLog(snapshot.ResilienceLog);
                
                // Update Library Health visuals from Snapshot data
                if (snapshot.LibraryHealth != null)
                {
                    LibraryHealth = snapshot.LibraryHealth;
                    UpdateTopGenres(snapshot.LibraryHealth.TopGenresJson);
                }
                
                // Refresh dynamic properties
                OnPropertyChanged(nameof(SessionStatus));
                OnPropertyChanged(nameof(IsSoulseekConnected));
                OnPropertyChanged(nameof(PurityPercent));
                OnPropertyChanged(nameof(PurityStatus));
                OnPropertyChanged(nameof(CurrentCpuLoad));
                OnPropertyChanged(nameof(IsLockdownActive));
                OnPropertyChanged(nameof(LockdownStatusText));
            });
        });

        // Commands
        RefreshDashboardCommand = new AsyncRelayCommand(RefreshDashboardAsync);
        NavigateToSearchCommand = new RelayCommand(() => _navigationService.NavigateTo("Search"));
        NavigateLibraryCommand = new RelayCommand(() => _navigationService.NavigateTo("Library"));
        ViewPlaylistCommand = new RelayCommand<PlaylistCardViewModel>(ExecuteViewPlaylist);
        QuickSearchCommand = new AsyncRelayCommand<SpotifyTrackViewModel>(ExecuteQuickSearchAsync);
        ClearDeadLettersCommand = new AsyncRelayCommand(ClearDeadLettersAsync);


        _connectionChangedHandler = (s, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.StatusText) || 
                e.PropertyName == nameof(ConnectionViewModel.IsConnected))
            {
                OnPropertyChanged(nameof(SessionStatus));
                OnPropertyChanged(nameof(IsSoulseekConnected));
            }
        };
        _connectionViewModel.PropertyChanged += _connectionChangedHandler;

        // Initial load
        _ = RefreshDashboardAsync();
        
        // Listen for Spotify changes
        _spotifyAuth.PropertyChanged += (s, e) => {
            if (e.PropertyName == nameof(SpotifyAuthService.IsAuthenticated))
                OnPropertyChanged(nameof(IsSpotifyConnected));
        };
    }


    public string SessionStatus => _connectionViewModel.StatusText;
    public bool IsSoulseekConnected => _connectionViewModel.IsConnected;
    public string DownloadSpeed => _downloadCenter?.GlobalSpeedDisplay ?? "0 KB/s";

    public string LockdownStatusText => IsLockdownActive ? "🛡️ ACTIVE" : "✅ NOMINAL";
    public bool IsLockdownActive => CurrentSnapshot.IsForensicLockdownActive;
    public double CurrentCpuLoad => CurrentSnapshot.CurrentCpuLoad;

    public string HealthColor => CurrentSnapshot.SystemHealth switch
    {
        SystemHealth.Excellent => "#00FF00",
        SystemHealth.Good => "#4CAF50",
        SystemHealth.Warning => "#FFCA28",
        SystemHealth.Critical => "#FF5252",
        _ => "#808080"
    };

    public int ExpressCount => _downloadCenter?.ExpressItems.Count ?? 0;
    public int StandardCount => _downloadCenter?.StandardItems.Count ?? 0;
    public int BackgroundCount => _downloadCenter?.BackgroundItems.Count ?? 0;
    
    private void UpdateResilienceLog(List<string> newLog)
    {
        if (ResilienceLog.SequenceEqual(newLog)) return;
        
        ResilienceLog.Clear();
        foreach (var l in newLog) ResilienceLog.Add(l);
    }

    public async Task RefreshDashboardAsync()
    {
        try
        {
            // Execute loading tasks in parallel for performance
            var healthTask = LoadLibraryHealthAsync();
            var recentTask = LoadRecentPlaylistsAsync();
            var spotifyTask = LoadSpotifyRecommendationsAsync();

            await Task.WhenAll(healthTask, recentTask, spotifyTask);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh dashboard");
        }
    }

    private async Task LoadLibraryHealthAsync()
    {
        IsLoadingHealth = true;
        try
        {
            LibraryHealth = await _dashboardService.GetLibraryHealthAsync();
            if (LibraryHealth == null)
            {
                // Trigger an initial calculation if cache is empty
                await _dashboardService.RecalculateLibraryHealthAsync();
                LibraryHealth = await _dashboardService.GetLibraryHealthAsync();
            }

            // Phase 3A (Transparency): Inject real Journal Health data (Recovery Status)
            if (LibraryHealth != null)
            {
                var journalStats = await _crashJournal.GetSystemHealthAsync();
                
                if (journalStats.DeadLetterCount > 0)
                {
                    LibraryHealth.HealthScore = 85; // Penalty for dead letters
                    LibraryHealth.HealthStatus = "Requires Attention";
                    LibraryHealth.IssuesCount = journalStats.DeadLetterCount;
                    // We could add a more specific message property if the view supported it,
                    // but for now, 'Issues Count' drives the orange UI state.
                }
                else if (journalStats.ActiveCount > 0)
                {
                    LibraryHealth.HealthStatus = $"Recovering ({journalStats.ActiveCount})";
                    // Active recovery is good, so keep score high
                }
            }
        }
        finally
        {
            IsLoadingHealth = false;
        }
    }

    private async Task ClearDeadLettersAsync()
    {
        try
        {
            int count = await _crashJournal.ResetDeadLettersAsync();
            if (count > 0)
            {
                _notificationService.Show("Recovery Started", $"Queued {count} stalled items for retry via Health Monitor.");
                await RefreshDashboardAsync();
            }
            else
            {
                _notificationService.Show("No Items", "No dead-lettered items found to retry.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear dead letters");
            _notificationService.Show("Error", "Failed to reset dead letters. Check logs.");
        }
    }

    private async Task LoadRecentPlaylistsAsync()
    {
        IsLoadingRecent = true;
        try
        {
            var recent = await _dashboardService.GetRecentPlaylistsAsync(10); // Show more for horizontal scroll
            
            // Map to ViewModels on background thread
            var viewModels = recent.Select(p => new PlaylistCardViewModel(p)).ToList();

            Dispatcher.UIThread.Post(() =>
            {
                RecentPlaylists.Clear();
                foreach (var vm in viewModels) RecentPlaylists.Add(vm);
            });
        }
        finally
        {
            IsLoadingRecent = false;
        }
    }

    private async Task LoadSpotifyRecommendationsAsync()
    {
        if (!_spotifyAuth.IsAuthenticated)
        {
            Dispatcher.UIThread.Post(() => SpotifyRecommendations.Clear());
            IsLoadingSpotify = false;
            return;
        }

        IsLoadingSpotify = true;
        try
        {
            var tracks = await _spotifyEnrichment.GetRecommendationsAsync(8);
            
            // Check library for each track
            foreach (var track in tracks)
            {
                if (!string.IsNullOrEmpty(track.ISRC))
                {
                    track.InLibrary = await _databaseService.FindLibraryEntryAsync(track.ISRC) != null;
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                SpotifyRecommendations.Clear();
                foreach (var t in tracks) SpotifyRecommendations.Add(t);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Spotify recommendations");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsLoadingSpotify = false);
        }
    }

    private void ExecuteViewPlaylist(PlaylistCardViewModel? card)
    {
        if (card == null) return;
        _libraryViewModel.SelectedProject = card.Model;
        _navigationService.NavigateTo("Library");
    }

    private async Task ExecuteQuickSearchAsync(SpotifyTrackViewModel? track)
    {
        if (track == null) return;
        
        // Navigate to search
        _navigationService.NavigateTo("Search");
        
        // Find SearchViewModel and trigger search
        // Since SearchViewModel is likely a singleton or registered in DI
        // we can trigger its property changes or a command if we have access.
        // For now, let's assume we navigate and the user can see the intent.
        // Better: We should have a way to pass parameters to Navigation.
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        _eventSubscription?.Dispose();
        
        if (_connectionChangedHandler != null)
        {
            _connectionViewModel.PropertyChanged -= _connectionChangedHandler;
        }

        _isDisposed = true;
    }



    private void UpdateTopGenres(string? json)
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var genres = System.Text.Json.JsonSerializer.Deserialize<List<GenreData>>(json);
            if (genres == null) return;

            TopGenres.Clear();
            foreach (var g in genres)
            {
                TopGenres.Add(new GenrePlanetViewModel { Name = g.Genre, Count = g.Count });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse top genres JSON");
        }
    }

    private class GenreData
    {
        public string Genre { get; set; } = string.Empty;
        public int Count { get; set; }
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
}

public class GenrePlanetViewModel
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Size => 40 + (Math.Min(Count, 100) * 0.5);
    public string Color => "#00A3FF"; // Could be dynamic based on purity later
}
