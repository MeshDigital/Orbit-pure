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
    private readonly ArtworkCacheService _artworkCacheService;
    private readonly PlaylistMosaicService _mosaicService;
    private readonly AnalysisPageViewModel _analysisPageViewModel;
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
    public ICommand RunMissionCommand { get; }
    public ICommand SelectGenreCommand { get; }
    public ICommand SelectDiscoverTabCommand { get; }

    private string _selectedDiscoverTab = "RecentlyAdded";
    public string SelectedDiscoverTab
    {
        get => _selectedDiscoverTab;
        set
        {
            if (SetProperty(ref _selectedDiscoverTab, value))
            {
                OnPropertyChanged(nameof(IsRecentlyAddedTabActive));
                OnPropertyChanged(nameof(IsDownloadedTabActive));
                OnPropertyChanged(nameof(IsForYouTabActive));
            }
        }
    }

    public bool IsRecentlyAddedTabActive => SelectedDiscoverTab == "RecentlyAdded";
    public bool IsDownloadedTabActive => SelectedDiscoverTab == "Downloaded";
    public bool IsForYouTabActive => SelectedDiscoverTab == "ForYou";


    public ObservableCollection<GenrePlanetViewModel> TopGenres { get; } = new();
    public bool HasTopGenres => TopGenres.Count > 0;

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
        SearchViewModel searchViewModel,
        ArtworkCacheService artworkCacheService,
        PlaylistMosaicService mosaicService,
        AnalysisPageViewModel analysisPageViewModel)
    {
        _logger = logger;
        _dashboardService = dashboardService;
        _navigationService = navigationService;
        _connectionViewModel = connectionViewModel;
        _artworkCacheService = artworkCacheService;
        _mosaicService = mosaicService;
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
        _analysisPageViewModel = analysisPageViewModel;

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
                OnPropertyChanged(nameof(HealthColor));
                OnPropertyChanged(nameof(EngineStatusText));
            });
        });

        // Commands
        RefreshDashboardCommand = new AsyncRelayCommand(RefreshDashboardAsync);
        NavigateToSearchCommand = new RelayCommand(() => _navigationService.NavigateTo("Search"));
        NavigateToAnalysisCommand = new RelayCommand(() => _navigationService.NavigateTo("Analysis"));
        // Accepts an optional "Gold"/"Silver"/"Bronze" CommandParameter — sets the Library's
        // quality-tier filter (and clears any selected playlist, to force the "All Tracks" view)
        // before navigating, so each badge actually lands on a correctly-filtered view instead of
        // the same generic unfiltered Library for all three.
        NavigateLibraryCommand = new RelayCommand<string>(tier =>
        {
            _libraryViewModel.Tracks.QualityTierFilter = string.IsNullOrEmpty(tier) ? null : tier;
            if (!string.IsNullOrEmpty(tier))
            {
                _libraryViewModel.SelectedProject = null;
            }
            _navigationService.NavigateTo("Library");
        });
        ViewPlaylistCommand = new RelayCommand<PlaylistCardViewModel>(ExecuteViewPlaylist);
        QuickSearchCommand = new AsyncRelayCommand<SpotifyTrackViewModel>(ExecuteQuickSearchAsync);
        ClearDeadLettersCommand = new AsyncRelayCommand(ClearDeadLettersAsync);
        RunMissionCommand = new AsyncRelayCommand<MissionOperation>(ExecuteRunMissionAsync);
        SelectGenreCommand = new RelayCommand<GenrePlanetViewModel>(ExecuteSelectGenre);
        SelectDiscoverTabCommand = new RelayCommand<string>(tab =>
        {
            if (!string.IsNullOrEmpty(tab)) SelectedDiscoverTab = tab;
        });


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

    public double CurrentCpuLoad => CurrentSnapshot.CurrentCpuLoad;

    public string HealthColor => CurrentSnapshot.SystemHealth switch
    {
        SystemHealth.Excellent => "#00FF00",
        SystemHealth.Good => "#4CAF50",
        SystemHealth.Warning => "#FFCA28",
        SystemHealth.Critical => "#FF5252",
        _ => "#808080"
    };

    /// <summary>
    /// Real system-status badge, replacing a badge that was previously bound to
    /// "!IsLockdownActive" — a dead-code flag (IsForensicLockdownActive) that was always false,
    /// making the badge permanently read "OPTIMAL" regardless of actual health.
    /// </summary>
    public string EngineStatusText => CurrentSnapshot.SystemHealth switch
    {
        SystemHealth.Excellent or SystemHealth.Good => "OPTIMAL",
        SystemHealth.Warning => "ATTENTION",
        SystemHealth.Critical => "CRITICAL",
        _ => "OPTIMAL"
    };

    private void UpdateResilienceLog(List<string> newLog)
    {
        if (ResilienceLog.SequenceEqual(newLog)) return;
        
        ResilienceLog.Clear();
        foreach (var l in newLog) ResilienceLog.Add(l);
    }

    private DateTime? _lastRefreshedAtUtc;
    /// <summary>
    /// Replaces the previous static "Bridge Operations Active" filler text, which was decorative
    /// and didn't correspond to anything real.
    /// </summary>
    public string LastRefreshedText => _lastRefreshedAtUtc is null
        ? "Not yet refreshed"
        : $"Updated {FormatRelativeTime(DateTime.UtcNow - _lastRefreshedAtUtc.Value)}";

    private static string FormatRelativeTime(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hr ago";
        return $"{(int)elapsed.TotalDays} days ago";
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

            _lastRefreshedAtUtc = DateTime.UtcNow;
            OnPropertyChanged(nameof(LastRefreshedText));
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
                UpdateTopGenres(LibraryHealth.TopGenresJson);

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
            var viewModels = recent.Select(p => new PlaylistCardViewModel(p, _artworkCacheService, _mosaicService)).ToList();

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
            var cards = downloads.Select(track => new RecentDownloadedTrackCardViewModel(track, _artworkCacheService)).ToList();

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

            // Check library for each track in parallel instead of awaiting one DB round-trip
            // at a time — bounded to 8 recommendations, so this stays cheap even run every load.
            var lookupTasks = tracks
                .Where(t => !string.IsNullOrEmpty(t.ISRC))
                .Select(async track => track.InLibrary = await _databaseService.FindLibraryEntryAsync(track.ISRC) != null)
                .ToList();
            await Task.WhenAll(lookupTasks);

            foreach (var track in tracks)
            {
                track.Artwork = new ArtworkProxy(_artworkCacheService, track.ImageUrl);
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

    /// <summary>
    /// Genre Galaxy planet clicked — jumps to the Library so the user can explore that genre's
    /// tracks. Doesn't auto-apply a style filter: the free-text genres shown here (from track
    /// metadata, e.g. "drum and bass") are a different vocabulary from the Library's curated
    /// StyleFilterItem list (e.g. "Neurofunk"), so there's no reliable 1:1 match to pre-select.
    /// </summary>
    private void ExecuteSelectGenre(GenrePlanetViewModel? genre)
    {
        if (genre == null) return;
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

    private async Task ExecuteRunMissionAsync(MissionOperation? mission)
    {
        if (mission == null) return;

        switch (mission.Type)
        {
            case Models.OperationType.Download:
                // "Upgrade Bronze Tracks" / "Re-download Low Bitrate" — deep-link into the real,
                // already-built Upgrade Scout panel where a user reviews/queues candidates,
                // instead of a bare unfiltered Library nav.
                _libraryViewModel.IsUpgradeScoutVisible = true;
                _navigationService.NavigateTo("Library");
                break;

            case Models.OperationType.System:
                // "Repair Dead Letters" is the exact same action as the SELF-HEAL button —
                // just a second entry point into it.
                mission.IsRunning = true;
                try
                {
                    await ClearDeadLettersAsync();
                }
                finally
                {
                    mission.IsRunning = false;
                }
                break;

            case Models.OperationType.Analysis:
                // "Reanalyze Incomplete Tracks" — navigate, then trigger the real batch-reanalyze
                // command Analysis already has, same navigate-then-trigger pattern as quick search.
                _navigationService.NavigateTo("Analysis");
                mission.IsRunning = true;
                try
                {
                    await Task.Delay(50); // allow navigation frame to settle
                    ICommand reanalyzeCommand = _analysisPageViewModel.ReanalyzeAllIncompleteCommand;
                    if (reanalyzeCommand.CanExecute(null))
                        reanalyzeCommand.Execute(null);
                }
                finally
                {
                    mission.IsRunning = false;
                }
                break;

            case Models.OperationType.Enrichment:
                // "Enrich Metadata" — no batch metadata-enrichment trigger exists anywhere in the
                // codebase to deep-link into yet; honest navigation-only until that's built.
                _navigationService.NavigateTo("Library");
                break;

            default:
                _navigationService.NavigateTo("Library");
                break;
        }
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
            for (int i = 0; i < genres.Count; i++)
            {
                TopGenres.Add(new GenrePlanetViewModel { Name = genres[i].Genre, Count = genres[i].Count, Color = GenrePlanetViewModel.PaletteColor(i) });
            }
            OnPropertyChanged(nameof(HasTopGenres));
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
    /// <summary>Vibrant, high-contrast palette cycled by rank so each genre reads as its own
    /// "planet" instead of every orb being an identical shade of blue.</summary>
    private static readonly string[] Palette =
    {
        "#00D9FF", "#A855F7", "#F43F5E", "#22C55E", "#FBBF24",
        "#EC4899", "#3B82F6", "#F97316", "#14B8A6", "#8B5CF6",
    };

    public static string PaletteColor(int index) => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Size => 40 + (Math.Min(Count, 100) * 0.5);
    public string Color { get; set; } = Palette[0];
}

public class RecentDownloadedTrackCardViewModel
{
    private readonly PlaylistTrack _track;

    public RecentDownloadedTrackCardViewModel(PlaylistTrack track, ArtworkCacheService? artworkCacheService = null)
    {
        _track = track;
        Artwork = artworkCacheService != null ? new ArtworkProxy(artworkCacheService, track.AlbumArtUrl) : null;
    }

    /// <summary>Lazily loads the track's art from its (typically remote) URL on first access.</summary>
    public ArtworkProxy? Artwork { get; }

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
