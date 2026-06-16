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
    private readonly SearchViewModel _searchViewModel;
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
    public ObservableCollection<RecentDownloadedTrackCardViewModel> RecentDownloads { get; } = new();
    public ObservableCollection<SpotifyTrackViewModel> SpotifyRecommendations { get; } = new();

    // --- Library Intelligence ---
    private int _intelligenceTotalTracks;
    private int _intelligenceAnalyzedTracks;
    private int _intelligenceFlacCount;
    private int _intelligenceMp3HqCount;
    private int _intelligenceLowQualityCount;

    public double IntelligenceAnalyzedPercent => _intelligenceTotalTracks > 0
        ? Math.Round((double)_intelligenceAnalyzedTracks / _intelligenceTotalTracks * 100, 1) : 0;
    public string IntelligenceAnalyzedText => $"{_intelligenceAnalyzedTracks:N0} of {_intelligenceTotalTracks:N0} tracks";
    public double IntelligenceFlacPercent => _intelligenceTotalTracks > 0
        ? (double)_intelligenceFlacCount / _intelligenceTotalTracks * 100 : 0;
    public double IntelligenceMp3Percent => _intelligenceTotalTracks > 0
        ? (double)_intelligenceMp3HqCount / _intelligenceTotalTracks * 100 : 0;
    public double IntelligenceLowPercent => _intelligenceTotalTracks > 0
        ? (double)_intelligenceLowQualityCount / _intelligenceTotalTracks * 100 : 0;
    public int IntelligenceFlacCount => _intelligenceFlacCount;
    public int IntelligenceMp3HqCount => _intelligenceMp3HqCount;
    public int IntelligenceLowQualityCount => _intelligenceLowQualityCount;

    public ObservableCollection<KeyBarViewModel> KeyDistributionBars { get; } = new();
    public ObservableCollection<EnergyBucketViewModel> EnergyBucketBars { get; } = new();

    private bool _isLoadingHealth = true;
    public bool IsLoadingHealth
    {
        get => _isLoadingHealth;
        set => SetProperty(ref _isLoadingHealth, value);
    }
    
    // Commands
    public ICommand RefreshDashboardCommand { get; }
    public ICommand NavigateToSearchCommand { get; }
    public ICommand NavigateToAnalysisCommand { get; }
    public ICommand QuickSearchCommand { get; }
    public ICommand ClearDeadLettersCommand { get; }
    public ICommand NavigateLibraryCommand { get; }
    public ICommand ViewPlaylistCommand { get; }
    public ICommand UpgradeBronzeCommand { get; }
    public ICommand RunMissionCommand { get; }


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

    private bool _isLoadingRecentDownloads;
    public bool IsLoadingRecentDownloads
    {
        get => _isLoadingRecentDownloads;
        set => SetProperty(ref _isLoadingRecentDownloads, value);
    }

    private bool _isLoadingSpotify;
    public bool IsLoadingSpotify
    {
        get => _isLoadingSpotify;
        set => SetProperty(ref _isLoadingSpotify, value);
    }

    public bool IsSpotifyConnected => _spotifyAuth.IsAuthenticated;

    private int _incompleteAnalysisCount;
    public int IncompleteAnalysisCount
    {
        get => _incompleteAnalysisCount;
        private set
        {
            if (SetProperty(ref _incompleteAnalysisCount, value))
            {
                OnPropertyChanged(nameof(HasIncompleteAnalysisTracks));
                OnPropertyChanged(nameof(IncompleteAnalysisSummary));
            }
        }
    }

    public bool HasIncompleteAnalysisTracks => IncompleteAnalysisCount > 0;
    public string IncompleteAnalysisSummary => HasIncompleteAnalysisTracks
        ? $"{IncompleteAnalysisCount} tracks need reanalysis"
        : "Analysis coverage is healthy";
    
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
        LibraryViewModel libraryViewModel,
        SearchViewModel searchViewModel)
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
        _searchViewModel = searchViewModel;

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
        NavigateToAnalysisCommand = new RelayCommand(() => _navigationService.NavigateTo("Analysis"));
        NavigateLibraryCommand = new RelayCommand(() => _navigationService.NavigateTo("Library"));
        ViewPlaylistCommand = new RelayCommand<PlaylistCardViewModel>(ExecuteViewPlaylist);
        QuickSearchCommand = new AsyncRelayCommand<SpotifyTrackViewModel>(ExecuteQuickSearchAsync);
        ClearDeadLettersCommand = new AsyncRelayCommand(ClearDeadLettersAsync);
        UpgradeBronzeCommand = new RelayCommand(() => _navigationService.NavigateTo("Library"));
        RunMissionCommand = new AsyncRelayCommand<MissionOperation>(ExecuteRunMissionAsync);


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
        _spotifyAuth.AuthenticationChanged += (_, _) => OnPropertyChanged(nameof(IsSpotifyConnected));
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
            var healthTask = LoadLibraryHealthAsync();
            var recentTask = LoadRecentPlaylistsAsync();
            var recentDownloadsTask = LoadRecentDownloadsAsync();
            var spotifyTask = LoadSpotifyRecommendationsAsync();
            var intelligenceTask = LoadIntelligenceStatsAsync();

            await Task.WhenAll(healthTask, recentTask, recentDownloadsTask, spotifyTask, intelligenceTask);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh dashboard");
        }
    }

    private async Task LoadIntelligenceStatsAsync()
    {
        try
        {
            var stats = await _dashboardService.GetLibraryIntelligenceStatsAsync();

            var camelotPositions = new[]
            {
                "1A","2A","3A","4A","5A","6A","7A","8A","9A","10A","11A","12A",
                "1B","2B","3B","4B","5B","6B","7B","8B","9B","10B","11B","12B"
            };
            var camelotColors = new[]
            {
                "#008080","#4682B4","#4169E1","#6A0DAD","#9400D3","#C71585",
                "#DC143C","#FF8C00","#DAA520","#6B8E23","#3CB371","#008B8B",
                "#008080","#4682B4","#4169E1","#6A0DAD","#9400D3","#C71585",
                "#DC143C","#FF8C00","#DAA520","#6B8E23","#3CB371","#008B8B"
            };

            var bucketLabels = new[] { "Low", "Med-", "Med", "Med+", "High" };
            var bucketColors = new[] { "#27AE60", "#2ECC71", "#F39C12", "#E67E22", "#E74C3C" };

            int maxKey = camelotPositions.Select(k => stats.KeyCounts.GetValueOrDefault(k, 0)).DefaultIfEmpty(1).Max();
            if (maxKey == 0) maxKey = 1;
            int maxBucket = stats.EnergyBuckets.DefaultIfEmpty(1).Max();
            if (maxBucket == 0) maxBucket = 1;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _intelligenceTotalTracks = stats.TotalCount;
                _intelligenceAnalyzedTracks = stats.AnalyzedCount;
                _intelligenceFlacCount = stats.FlacCount;
                _intelligenceMp3HqCount = stats.Mp3HqCount;
                _intelligenceLowQualityCount = stats.LowQualityCount;

                OnPropertyChanged(nameof(IntelligenceAnalyzedPercent));
                OnPropertyChanged(nameof(IntelligenceAnalyzedText));
                OnPropertyChanged(nameof(IntelligenceFlacPercent));
                OnPropertyChanged(nameof(IntelligenceMp3Percent));
                OnPropertyChanged(nameof(IntelligenceLowPercent));
                OnPropertyChanged(nameof(IntelligenceFlacCount));
                OnPropertyChanged(nameof(IntelligenceMp3HqCount));
                OnPropertyChanged(nameof(IntelligenceLowQualityCount));

                KeyDistributionBars.Clear();
                for (int i = 0; i < camelotPositions.Length; i++)
                {
                    var key = camelotPositions[i];
                    var count = stats.KeyCounts.GetValueOrDefault(key, 0);
                    KeyDistributionBars.Add(new KeyBarViewModel(key, count, (double)count / maxKey, camelotColors[i]));
                }

                EnergyBucketBars.Clear();
                for (int i = 0; i < 5; i++)
                {
                    EnergyBucketBars.Add(new EnergyBucketViewModel(
                        bucketLabels[i], stats.EnergyBuckets[i],
                        (double)stats.EnergyBuckets[i] / maxBucket,
                        bucketColors[i]));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load intelligence stats");
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

            IncompleteAnalysisCount = await _dashboardService.GetIncompleteAnalysisTrackCountAsync();
        }
        finally
        {
            IsLoadingHealth = false;
            Dispatcher.UIThread.Post(PopulateActiveMissions);
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

    private async Task LoadRecentDownloadsAsync()
    {
        IsLoadingRecentDownloads = true;
        try
        {
            var downloads = await _dashboardService.GetRecentDownloadedTracksAsync(8);
            var cards = downloads.Select(track => new RecentDownloadedTrackCardViewModel(track)).ToList();

            Dispatcher.UIThread.Post(() =>
            {
                RecentDownloads.Clear();
                foreach (var card in cards)
                {
                    RecentDownloads.Add(card);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recent downloads");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsLoadingRecentDownloads = false);
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
        _searchViewModel.SearchQuery = $"{track.Artist} {track.Title}".Trim();
        _navigationService.NavigateTo("Search");
        await Task.Delay(50); // allow navigation frame to settle
        if (_searchViewModel.UnifiedSearchCommand.CanExecute(null))
            _searchViewModel.UnifiedSearchCommand.Execute(null);
    }

    private Task ExecuteRunMissionAsync(MissionOperation? mission)
    {
        if (mission == null) return Task.CompletedTask;
        switch (mission.Type)
        {
            case Models.OperationType.Download:
                _navigationService.NavigateTo("Projects");
                break;
            case Models.OperationType.Analysis:
                _navigationService.NavigateTo("Analysis");
                break;
            default:
                _navigationService.NavigateTo("Library");
                break;
        }
        return Task.CompletedTask;
    }

    private void PopulateActiveMissions()
    {
        ActiveMissions.Clear();
        if (LibraryHealth == null) return;

        if (LibraryHealth.BronzeCount > 0)
            ActiveMissions.Add(new MissionOperation
            {
                Icon = "🥉",
                Name = "Upgrade Bronze Tracks",
                StatusText = $"{LibraryHealth.BronzeCount} tracks below quality threshold",
                Type = Models.OperationType.Download
            });

        if (LibraryHealth.UpgradableCount > 0)
            ActiveMissions.Add(new MissionOperation
            {
                Icon = "⬆️",
                Name = "Re-download Low Bitrate",
                StatusText = $"{LibraryHealth.UpgradableCount} tracks with low bitrate",
                Type = Models.OperationType.Download
            });

        if (LibraryHealth.PendingUpdates > 0)
            ActiveMissions.Add(new MissionOperation
            {
                Icon = "🏷️",
                Name = "Enrich Metadata",
                StatusText = $"{LibraryHealth.PendingUpdates} tracks missing metadata",
                Type = Models.OperationType.Enrichment
            });

        if (LibraryHealth.IssuesCount > 0)
            ActiveMissions.Add(new MissionOperation
            {
                Icon = "🔧",
                Name = "Repair Dead Letters",
                StatusText = $"{LibraryHealth.IssuesCount} items need recovery",
                Type = Models.OperationType.System
            });

        if (IncompleteAnalysisCount > 0)
            ActiveMissions.Add(new MissionOperation
            {
                Icon = "🧪",
                Name = "Reanalyze Incomplete Tracks",
                StatusText = $"{IncompleteAnalysisCount} tracks missing analysis fields",
                Type = Models.OperationType.Analysis
            });

        if (ActiveMissions.Count == 0)
            ActiveMissions.Add(new MissionOperation
            {
                Icon = "✅",
                Name = "Library is Healthy",
                StatusText = "No missions required",
                Type = Models.OperationType.System
            });
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

public class RecentDownloadedTrackCardViewModel
{
    private readonly PlaylistTrack _track;

    public RecentDownloadedTrackCardViewModel(PlaylistTrack track)
    {
        _track = track;
    }

    public string Title => string.IsNullOrWhiteSpace(_track.Title) ? "Unknown Title" : _track.Title;
    public string Artist => string.IsNullOrWhiteSpace(_track.Artist) ? "Unknown Artist" : _track.Artist;
    public string? CoverImageUrl => _track.AlbumArtUrl;
    public string FormatLabel => string.IsNullOrWhiteSpace(_track.Format) ? "FILE" : _track.Format.ToUpperInvariant();
    public string QualityLabel => _track.Bitrate > 0 ? $"{_track.Bitrate} kbps" : FormatLabel;
    public string SourceLabel => string.IsNullOrWhiteSpace(_track.SourcePlaylistName) ? "Library" : _track.SourcePlaylistName!;
    public string CompletedLabel => _track.CompletedAt?.ToLocalTime().ToString("MMM d, HH:mm") ?? "Just now";
}

public record KeyBarViewModel(string Key, int Count, double RelativeHeight, string Color)
{
    public double BarHeight => Math.Max(2, RelativeHeight * 88);
    public string TooltipText => $"{Key}: {Count} tracks";
}

public record EnergyBucketViewModel(string Label, int Count, double RelativeHeight, string Color)
{
    public double BarHeight => Math.Max(2, RelativeHeight * 80);
    public string TooltipText => $"{Label}: {Count} tracks";
}
