using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Platform;
using SLSKDONET.Views; // For AsyncRelayCommand
using SLSKDONET.ViewModels.Settings;
using Avalonia.Threading;

using System.Collections.ObjectModel; // Added
using System.Linq;
using SLSKDONET.Models; // For SearchPolicy and Events
using SLSKDONET.Data.Entities;
using SLSKDONET.Data; // For AppDbContext
using Microsoft.EntityFrameworkCore;
namespace SLSKDONET.ViewModels;


/// <summary>
/// FFmpeg install/check, network activity rates, engine diagnostics, share health, and auth state telemetry (#307).
/// </summary>
public partial class SettingsViewModel
{
    private async Task InstallFfmpegAsync()
    {
        if (_ffmpegInstaller == null) return;
        IsInstallingFfmpeg = true;
        FfmpegStatus = "Installing…";
        try
        {
            var progress = new Progress<double>(p => FfmpegStatus = SLSKDONET.Utils.NumFormat.Ui($"Installing… {p:P0}"));
            var result = await _ffmpegInstaller.InstallAsync(progress);
            if (!result.Success)
            {
                FfmpegStatus = "Install failed";
                _eventBus.Publish(new ToastRequestedEvent("FFmpeg wasn't installed", result.Message, NotificationType.Error, TimeSpan.FromSeconds(8)));
            }
            await CheckFfmpegAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg install from Settings failed");
            FfmpegStatus = "Install failed";
            _eventBus.Publish(new ToastRequestedEvent("FFmpeg wasn't installed", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(8)));
        }
        finally
        {
            IsInstallingFfmpeg = false;
            OnPropertyChanged(nameof(CanInstallFfmpeg));
        }
    }
    public ICommand OpenSpotifyDashboardCommand { get; }
    public ICommand CopySpotifyRedirectUriCommand { get; }
    public ICommand ResetDatabaseCommand { get; }
    public ICommand BackupLibraryNowCommand { get; }
    public ICommand OpenBackupsFolderCommand { get; }
    public ICommand ResetToDefaultsCommand { get; }
    public ICommand ScanLibraryCommand { get; } // [NEW] Manual Scan
    public ICommand ReconcileLibraryCommand { get; }
    public ICommand FullLibrarySyncCommand { get; }
    public ICommand EnrichMissingMetadataCommand { get; }
    public ICommand RefreshRemovalCandidatesCommand { get; }
    public ICommand AddLibraryFolderCommand { get; }
    public ICommand RemoveLibraryFolderCommand { get; }

    public ObservableCollection<RemovalCandidateItemViewModel> RemovalCandidates { get; } = new();

    private string _removalCandidatesStatus = "No removal candidates yet.";
    public string RemovalCandidatesStatus
    {
        get => _removalCandidatesStatus;
        private set => SetProperty(ref _removalCandidatesStatus, value);
    }

    public bool HasRemovalCandidates => RemovalCandidates.Count > 0;

    // Scan State
    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }

    private string _scanStatus = "Idle";
    public string ScanStatus
    {
        get => _scanStatus;
        set => SetProperty(ref _scanStatus, value);
    }

    private string _enrichStatus = string.Empty;
    public string EnrichStatus
    {
        get => _enrichStatus;
        set => SetProperty(ref _enrichStatus, value);
    }

    private readonly EnrichmentViewModel? _enrichment;

    /// <summary>Settings → Connection → Metadata sources (null in design/test construction).</summary>
    public MetadataSourcesSettingsViewModel? MetadataSources { get; }
    public bool HasMetadataSources => MetadataSources != null;

    /// <summary>True while the library enrichment run (MetadataEnrichmentOrchestrator) works.</summary>
    public bool IsEnriching => _enrichment?.IsRunning == true;

    public bool CanEnrich => _enrichment != null && !_enrichment.IsRunning;

    private bool _isReconciling;
    public bool IsReconciling
    {
        get => _isReconciling;
        set => SetProperty(ref _isReconciling, value);
    }

    private string _reconcileStatus = string.Empty;
    public string ReconcileStatus
    {
        get => _reconcileStatus;
        set => SetProperty(ref _reconcileStatus, value);
    }

    private bool _isFullSyncing;
    public bool IsFullSyncing
    {
        get => _isFullSyncing;
        set => SetProperty(ref _isFullSyncing, value);
    }

    private string _fullSyncStatus = string.Empty;
    public string FullSyncStatus
    {
        get => _fullSyncStatus;
        set => SetProperty(ref _fullSyncStatus, value);
    }

    // Phase 8: FFmpeg Dependency State
    private bool _isFfmpegInstalled;
    public bool IsFfmpegInstalled
    {
        get => _isFfmpegInstalled;
        set => SetProperty(ref _isFfmpegInstalled, value);
    }

    /// <summary>Auto-update surface (channel, check, download); may be null before wiring.</summary>
    public UpdateViewModel? Update { get; }

    private string _ffmpegStatus = "Checking...";
    public string FfmpegStatus
    {
        get => _ffmpegStatus;
        set => SetProperty(ref _ffmpegStatus, value);
    }

    private string _ffmpegVersion = "";
    public string FfmpegVersion
    {
        get => _ffmpegVersion;
        set => SetProperty(ref _ffmpegVersion, value);
    }

    // Phase 6: Security Audit Feed
    private const int AuditFeedMaxEntries = 100;
    private const int AdaptiveLaneHistoryMaxEntries = 10;
    public ObservableCollection<SecurityAuditEntryViewModel> SecurityAuditFeed { get; } = new();
    public ObservableCollection<AdaptiveLaneDecisionEntryViewModel> AdaptiveLaneDecisionHistory { get; } = new();
    private IDisposable? _securityAuditSubscription;
    private IDisposable? _adaptiveLaneStatusSubscription;
    private IDisposable? _searchPressureSubscription;

    // Phase E3: snapshot state
    private SearchPressureStatusEvent? _lastSearchPressure;
    private string _diagnosticsSnapshotStatus = "";
    public string DiagnosticsSnapshotStatus
    {
        get => _diagnosticsSnapshotStatus;
        private set
        {
            SetProperty(ref _diagnosticsSnapshotStatus, value);
            OnPropertyChanged(nameof(HasDiagnosticsSnapshotStatus));
        }
    }
    public bool HasDiagnosticsSnapshotStatus => !string.IsNullOrEmpty(_diagnosticsSnapshotStatus);

    // Network Activity Monitor: live feed of every outbound network call
    private const int NetworkActivityFeedMaxEntries = 300;
    public ObservableCollection<NetworkActivityEntryViewModel> NetworkActivityFeed { get; } = new();
    private IDisposable? _networkActivitySubscription;

    private int _soulseekCallsLast4Min;
    public int SoulseekCallsLast4Min
    {
        get => _soulseekCallsLast4Min;
        private set => SetProperty(ref _soulseekCallsLast4Min, value);
    }

    private int _httpCallsLast4Min;
    public int HttpCallsLast4Min
    {
        get => _httpCallsLast4Min;
        private set => SetProperty(ref _httpCallsLast4Min, value);
    }

    private int _socketConnectsLast4Min;
    public int SocketConnectsLast4Min
    {
        get => _socketConnectsLast4Min;
        private set => SetProperty(ref _socketConnectsLast4Min, value);
    }

    private static readonly TimeSpan NetworkActivityRateWindow = TimeSpan.FromMinutes(4);

    private void RefreshNetworkActivityRates()
    {
        if (_networkActivityMonitor == null) return;

        SoulseekCallsLast4Min = _networkActivityMonitor.CountSince(NetworkActivityRateWindow, "Soulseek");
        HttpCallsLast4Min = _networkActivityMonitor.CountSince(NetworkActivityRateWindow, "HTTP");
        SocketConnectsLast4Min = _networkActivityMonitor.CountSince(NetworkActivityRateWindow, "Socket");
    }

    private void OnNetworkActivityEvent(NetworkActivityEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            NetworkActivityFeed.Insert(0, new NetworkActivityEntryViewModel(e));
            while (NetworkActivityFeed.Count > NetworkActivityFeedMaxEntries)
                NetworkActivityFeed.RemoveAt(NetworkActivityFeed.Count - 1);

            RefreshNetworkActivityRates();
        });
    }

    // Engine Diagnostics: import/search audit trail (what a pasted line became, what was
    // searched, what candidates were evaluated, why a search did/didn't match)
    private const int EngineDiagnosticsFeedMaxEntries = 300;
    public ObservableCollection<EngineDiagnosticEntryViewModel> EngineDiagnosticsFeed { get; } = new();
    private IDisposable? _engineDiagnosticsSubscription;

    public string[] EngineDiagnosticsEventTypeFilters { get; } =
    {
        "All",
        EngineDiagnosticEventType.ImportLine,
        EngineDiagnosticEventType.TracksAddedToPlaylist,
        EngineDiagnosticEventType.SearchDispatched,
        EngineDiagnosticEventType.SearchCandidateEvaluated,
        EngineDiagnosticEventType.SearchResolved,
    };

    private string _engineDiagnosticsEventTypeFilter = "All";
    public string EngineDiagnosticsEventTypeFilter
    {
        get => _engineDiagnosticsEventTypeFilter;
        set
        {
            if (SetProperty(ref _engineDiagnosticsEventTypeFilter, value))
            {
                _ = RefreshEngineDiagnosticsFeedAsync();
            }
        }
    }

    private async System.Threading.Tasks.Task RefreshEngineDiagnosticsFeedAsync()
    {
        if (_engineDiagnosticsService == null) return;

        var filter = EngineDiagnosticsEventTypeFilter == "All" ? null : EngineDiagnosticsEventTypeFilter;
        var recent = await _engineDiagnosticsService.GetRecentAsync(filter, take: EngineDiagnosticsFeedMaxEntries);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EngineDiagnosticsFeed.Clear();
            foreach (var entry in recent)
                EngineDiagnosticsFeed.Add(new EngineDiagnosticEntryViewModel(entry));
        });
    }

    private void OnEngineDiagnosticEvent(EngineDiagnosticEvent e)
    {
        if (EngineDiagnosticsEventTypeFilter != "All" && EngineDiagnosticsEventTypeFilter != e.EventType)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            EngineDiagnosticsFeed.Insert(0, new EngineDiagnosticEntryViewModel(e));
            while (EngineDiagnosticsFeed.Count > EngineDiagnosticsFeedMaxEntries)
                EngineDiagnosticsFeed.RemoveAt(EngineDiagnosticsFeed.Count - 1);
        });
    }

    private void OnSecurityAuditEvent(SecurityAuditEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SecurityAuditFeed.Insert(0, new SecurityAuditEntryViewModel(e));
            while (SecurityAuditFeed.Count > AuditFeedMaxEntries)
                SecurityAuditFeed.RemoveAt(SecurityAuditFeed.Count - 1);
        });
    }

    private void OnAdaptiveLaneStatusEvent(AdaptiveLaneStatusEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AdaptiveLaneDecisionHistory.Insert(0, new AdaptiveLaneDecisionEntryViewModel(e));
            while (AdaptiveLaneDecisionHistory.Count > AdaptiveLaneHistoryMaxEntries)
                AdaptiveLaneDecisionHistory.RemoveAt(AdaptiveLaneDecisionHistory.Count - 1);
        });
    }

    private void OnSearchPressureStatusEvent(SearchPressureStatusEvent e)
    {
        _lastSearchPressure = e;
    }

    public ICommand ClearSecurityAuditCommand { get; private set; } = null!;
    public ICommand CopyDiagnosticsSnapshotCommand { get; private set; } = null!;

    // Phase 6: Live Share Status (bound in Settings page)
    private int    _shareFileCount;
    private string _shareReputationLabel = "Unknown";
    private IDisposable? _shareHealthSubscription;

    // Soulseek connection live state
    private IDisposable? _soulseekLifecycleSubscription;
    private bool _soulseekIsConnected;
    public bool SoulseekIsConnected
    {
        get => _soulseekIsConnected;
        private set
        {
            if (SetProperty(ref _soulseekIsConnected, value))
                OnPropertyChanged(nameof(SoulseekIsDisconnected));
        }
    }
    public bool SoulseekIsDisconnected => !_soulseekIsConnected;

    private string _soulseekConnectionStatusText = "";
    public string SoulseekConnectionStatusText
    {
        get => _soulseekConnectionStatusText;
        private set => SetProperty(ref _soulseekConnectionStatusText, value);
    }

    public int ShareFileCount
    {
        get => _shareFileCount;
        private set { _shareFileCount = value; OnPropertyChanged(); RaiseShareStatusChanged(); }
    }

    // Share health drives the status dot on the Settings page (Ellipse.status-dot + Classes.*).
    // Disabled sharing leaves all three false, which renders the neutral grey dot.
    public bool ShareIsEmpty   => EnableLibrarySharing && _shareFileCount == 0;
    public bool ShareIsLow     => EnableLibrarySharing && _shareFileCount is > 0 and < 500;
    public bool ShareIsHealthy => EnableLibrarySharing && _shareFileCount >= 500;

    private void RaiseShareStatusChanged()
    {
        OnPropertyChanged(nameof(ShareStatusSummary));
        OnPropertyChanged(nameof(ShareIsEmpty));
        OnPropertyChanged(nameof(ShareIsLow));
        OnPropertyChanged(nameof(ShareIsHealthy));
    }

    public string ShareStatusSummary => !EnableLibrarySharing
        ? "Sharing is disabled"
        : _shareFileCount == 0
            ? "No shared files detected — check the folder path"
            : $"{SLSKDONET.Utils.CountFormat.N(_shareFileCount)} files shared · {_shareReputationLabel}";

    public ICommand RefreshShareNowCommand { get; private set; } = null!;

    // ── Keyboard Mapping sub-ViewModel (Epic #119) ────────────────────────────
    public KeyboardMappingsViewModel KeyboardMappings { get; private set; } = null!;

    // Soulseek Connection Commands
    public ICommand SoulseekConnectCommand { get; private set; } = null!;
    public ICommand SoulseekDisconnectCommand { get; private set; } = null!;
    public ICommand SoulseekReconnectCommand { get; private set; } = null!;

    private void OnShareHealthUpdated(ShareHealthUpdatedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _shareReputationLabel = e.SharedFileCount == 0 ? "🔴 Critical" :
                                    e.SharedFileCount <  500 ? "🟡 Low" : "🟢 Healthy";
            ShareFileCount = e.SharedFileCount; // notifies all dependents
        });
    }

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        IFileInteractionService fileInteractionService,
        SpotifyAuthService spotifyAuthService,
        ISpotifyMetadataService spotifyMetadataService,
        DatabaseService databaseService,
        LibraryFolderScannerService libraryFolderScannerService,
        IEventBus eventBus,
        ISoulseekAdapter soulseek,
        ISoulseekCredentialService credentialService,
        IConnectionLifecycleService lifecycle,
        KeyboardMappingsViewModel keyboardMappings,
        IDbContextFactory<AppDbContext>? dbFactory = null,
        ILibraryService? libraryService = null,
        IDialogService? dialogService = null,
        NetworkActivityMonitor? networkActivityMonitor = null,
        EngineDiagnosticsService? engineDiagnosticsService = null,
        ILibraryImportLauncher? importLauncher = null,
        EnrichmentViewModel? enrichment = null,
        MetadataSourcesSettingsViewModel? metadataSources = null,
        UpdateViewModel? updateViewModel = null,
        Services.Library.Maintenance.MaintenanceUndoService? maintenanceUndo = null,
        Services.KnowledgeLifecycle.RekordboxHistoryImportService? historyImport = null,
        Settings.PersonalGenreSettingsViewModel? personalGenre = null,
        Services.Platform.FfmpegInstallerService? ffmpegInstaller = null)
    {
        _logger = logger;
        PersonalGenre = personalGenre;
        _ffmpegInstaller = ffmpegInstaller;
        _config = config;
        _enrichment = enrichment;
        MetadataSources = metadataSources;
        Update = updateViewModel;
        if (_enrichment != null)
            _enrichment.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(EnrichmentViewModel.IsRunning) or nameof(EnrichmentViewModel.NoticeText))
                {
                    OnPropertyChanged(nameof(CanEnrich));
                    OnPropertyChanged(nameof(IsEnriching));
                    EnrichStatus = _enrichment.IsRunning ? _enrichment.NoticeText + " · progress on the Analysis page" : _enrichment.NoticeText;
                    (EnrichMissingMetadataCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            };
        _importLauncher = importLauncher;
        _networkActivityMonitor = networkActivityMonitor;
        _engineDiagnosticsService = engineDiagnosticsService;
        _configManager = configManager;
        _fileInteractionService = fileInteractionService;
        _spotifyAuthService = spotifyAuthService;
        _spotifyMetadataService = spotifyMetadataService;
        _databaseService = databaseService;
        _libraryFolderScannerService = libraryFolderScannerService;
        _eventBus = eventBus;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _dbFactory = dbFactory;
        _lifecycle = lifecycle;
        _libraryService = libraryService;
        _maintenanceUndo = maintenanceUndo;
        _historyImport = historyImport;
        KeyboardMappings = keyboardMappings;
        _dialogService = dialogService;

        // Epic #508 / #512: Do NOT silently back-fill DefaultSpotifyClientId when empty.
        // Empty SpotifyClientId means "no Spotify app configured" (public links still work without sign-in).
        OwnSpotifyApp = new Setup.OwnSpotifyAppViewModel(
            readClientId: () => _config.SpotifyClientId ?? string.Empty,
            writeClientId: id => SpotifyClientId = id,
            redirectUri: SpotifyRedirectUri,
            openDashboard: OpenSpotifyDashboard,
            copyRedirectUri: () => CopySpotifyRedirectUriCommand?.Execute(null));

        SaveSettingsCommand = new RelayCommand(SaveSettingsWithConfirmation);
        BrowseDownloadPathCommand = new AsyncRelayCommand(BrowseDownloadPathAsync);
        BrowseSharedFolderCommand = new AsyncRelayCommand(BrowseSharedFolderAsync);
        BrowseFrequentSourcesStagingPathCommand = new AsyncRelayCommand(BrowseFrequentSourcesStagingPathAsync);

        ConnectSpotifyCommand = new AsyncRelayCommand(ConnectSpotifyAsync, () => IsSpotifyDisconnected);
        DisconnectSpotifyCommand = new AsyncRelayCommand(DisconnectSpotifyAsync, () => IsSpotifyConnected);
        TestSpotifyConnectionCommand = new AsyncRelayCommand(TestSpotifyConnectionAsync); // Always allow testing if user expands advanced
        ClearSpotifyCacheCommand = new AsyncRelayCommand(ClearSpotifyCacheAsync);
        RevokeAndReAuthCommand = new AsyncRelayCommand(RevokeAndReAuthAsync);
        CheckFfmpegCommand = new AsyncRelayCommand(CheckFfmpegAsync); // Phase 8
        InstallFfmpegCommand = new AsyncRelayCommand(InstallFfmpegAsync, () => CanInstallFfmpeg && !IsInstallingFfmpeg); // #331
        OpenFfmpegDownloadPageCommand = new RelayCommand(OpenFfmpegDownloadPage);
        OpenSpotifyDashboardCommand = new RelayCommand(OpenSpotifyDashboard);
        CopySpotifyRedirectUriCommand = new AsyncRelayCommand(CopySpotifyRedirectUriAsync);
        RestartSpotifyAuthCommand = new AsyncRelayCommand(RestartSpotifyAuthAsync, () => IsSpotifyConnecting);
        ResetDatabaseCommand = new AsyncRelayCommand(ResetDatabaseAsync);
        BackupLibraryNowCommand = new AsyncRelayCommand(BackupLibraryNowAsync);
        OpenBackupsFolderCommand = new RelayCommand(OpenBackupsFolder);
        ResetToDefaultsCommand = new AsyncRelayCommand(ResetToDefaultsAsync);
        ScanLibraryCommand = new AsyncRelayCommand(ScanLibraryAsync, () => !IsScanning && !IsFullSyncing);
        ReconcileLibraryCommand = new AsyncRelayCommand(ReconcileLibraryAsync, () => !IsReconciling && !IsFullSyncing);
        FullLibrarySyncCommand = new AsyncRelayCommand(FullLibrarySyncAsync, () => !IsFullSyncing && !IsScanning && !IsReconciling);
        EnrichMissingMetadataCommand = new AsyncRelayCommand(EnrichMissingMetadataAsync, () => CanEnrich);
        RefreshRemovalCandidatesCommand = new AsyncRelayCommand(LoadRemovalCandidatesAsync);
        AddLibraryFolderCommand = new AsyncRelayCommand(AddLibraryFolderAsync);
        RemoveLibraryFolderCommand = new AsyncRelayCommand(RemoveLibraryFolderAsync, () => SelectedLibraryFolder != null);
        ClearSecurityAuditCommand = new RelayCommand(() => SecurityAuditFeed.Clear());
        CopyDiagnosticsSnapshotCommand = new AsyncRelayCommand(CopyDiagnosticsSnapshotAsync);
        RefreshShareNowCommand = new AsyncRelayCommand(async () =>
        {
            try
            {
                await _soulseek.RefreshShareStateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Manual share refresh failed. This is non-fatal and can happen while reconnecting.");
            }
        });

        // Soulseek Connection Commands
        SoulseekConnectCommand = new AsyncRelayCommand(SoulseekConnectAsync, () => !_soulseek.IsConnected);
        SoulseekDisconnectCommand = new RelayCommand(SoulseekDisconnect, () => _soulseek.IsConnected);
        SoulseekReconnectCommand = new AsyncRelayCommand(SoulseekReconnectAsync, () => _soulseek.IsConnected);

        // Initialize connected state and subscribe to lifecycle events
        SoulseekIsConnected = _soulseek.IsConnected;
        _soulseekLifecycleSubscription = _eventBus
            .GetEvent<ConnectionLifecycleStateChangedEvent>()
            .Subscribe(OnSoulseekLifecycleChanged);

        // Subscribe to live share health updates
        _shareHealthSubscription = _eventBus.GetEvent<ShareHealthUpdatedEvent>().Subscribe(OnShareHealthUpdated);

        // Explicitly initialize IsAuthenticating to false
        IsAuthenticating = false;

        // Fix: Subscribe to authentication changes from the service
        _spotifyAuthService.AuthenticationChanged += OnSpotifyAuthenticationChanged;

        // Set initial display based on current auth state
        UpdateSpotifyUIState(_spotifyAuthService.IsAuthenticated);

        _libraryFoldersSubscription = _eventBus.GetEvent<LibraryFoldersChangedEvent>().Subscribe(e => { _ = LoadLibraryFoldersAsync(); });

        // Phase 6: Security Audit Feed subscription
        _securityAuditSubscription = _eventBus.GetEvent<SecurityAuditEvent>().Subscribe(OnSecurityAuditEvent);

        // Network Activity Monitor: seed from existing history (Subject<T> doesn't replay), then subscribe for live updates
        if (_networkActivityMonitor != null)
        {
            foreach (var entry in _networkActivityMonitor.GetRecentActivity(NetworkActivityFeedMaxEntries))
                NetworkActivityFeed.Add(new NetworkActivityEntryViewModel(entry));
            RefreshNetworkActivityRates();
        }
        _networkActivitySubscription = _eventBus.GetEvent<NetworkActivityEvent>().Subscribe(OnNetworkActivityEvent);
        _engineDiagnosticsSubscription = _eventBus.GetEvent<EngineDiagnosticEvent>().Subscribe(OnEngineDiagnosticEvent);
        _adaptiveLaneStatusSubscription = _eventBus.GetEvent<AdaptiveLaneStatusEvent>().Subscribe(OnAdaptiveLaneStatusEvent);
        _searchPressureSubscription = _eventBus.GetEvent<SearchPressureStatusEvent>().Subscribe(OnSearchPressureStatusEvent);

        // Force update of derived properties to ensure UI booleans are in sync with SpotifyState
        UpdateDerivedProperties(SpotifyState);
    }

    /// <summary>
    /// Runs the device-enumeration/FFmpeg/library-folder/removal-candidate/AI-engine checks that
    /// used to fire unconditionally from the constructor. SettingsViewModel is a DI singleton
    /// constructed eagerly at app startup (it's a MainViewModel constructor parameter), so doing
    /// this work there blocked the UI thread before the main window ever appeared. Call this once,
    /// from SettingsPage's constructor, so it only runs on first navigation to Settings instead.
    /// </summary>
    public void EnsureInitialized()
    {
        if (_isInitialized) return;
        _isInitialized = true;


        try { RefreshAvailableAudioOutputDevices(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to enumerate audio output devices on Settings load"); }

        _ = CheckFfmpegAsync(); // Phase 8: Check FFmpeg on startup
        _ = LoadLibraryFoldersAsync(); // Phase 0.10
        _ = LoadRemovalCandidatesAsync();
        _ = RefreshEngineDiagnosticsFeedAsync();
        _ = RefreshMaintenanceUndoAsync();
    }

    /// <summary>
    /// Synchronizes the ViewModel state with the SpotifyAuthService authentication state.
    /// Uses the UI thread dispatcher to ensure thread safety during background updates.
    /// </summary>
    private void OnSpotifyAuthenticationChanged(object? sender, bool isAuthenticated)
    {
        // Ensure this always runs on the UI thread
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            // CRITICAL FIX: Ensure we clear the 'IsAuthenticating' lock when we get a definitive state update.
            // This prevents the UI from being stuck in a disabled state if a previous attempt hung.
            if (IsAuthenticating)
            {
                 _logger.LogInformation("Authentication state changed to {State} while IsAuthenticating was true - clearing lock.", isAuthenticated);
                 IsAuthenticating = false;
            }

            UpdateSpotifyUIState(isAuthenticated);
            _logger.LogInformation("Spotify UI state synchronized via event: {State}",
                isAuthenticated ? "Connected" : "Disconnected");
        });
    }


    /// <summary>
    private void UpdateSpotifyUIState(bool isAuthenticated)
    {
        // Source of True Truth
        SpotifyState = isAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;

        SpotifyDisplayName = isAuthenticated ? "Connected" : "Not Connected";

        if (isAuthenticated)
        {
            SpotifyUseApi = true;
        }
    }

    /// <summary>
    /// Phase 8: Enhanced FFmpeg dependency checker with timeout, stderr capture, and fallback paths.
    /// </summary>
    /// <summary>Also used by the setup wizard's Audio &amp; analysis step.</summary>
    public async Task CheckFfmpegAsync()
    {
        try
        {
            FfmpegStatus = "Checking...";

            // Try standard PATH lookup first
            var (success, version) = await TryFfmpegCommandAsync("ffmpeg");

            if (!success)
            {
                // Fallback: Check common install directories (Windows-specific)
                if (OperatingSystem.IsWindows())
                {
                    var commonPaths = new[]
                    {
                        // ORBIT's own managed install (FfmpegInstallerService), checked first.
                        SLSKDONET.Services.Platform.FfmpegInstallerService.ManagedFfmpegPath,
                        @"C:\ffmpeg\bin\ffmpeg.exe",
                        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", "ffmpeg.exe")
                    };

                    foreach (var path in commonPaths)
                    {
                        if (File.Exists(path))
                        {
                            (success, version) = await TryFfmpegCommandAsync(path);
                            if (success)
                            {
                                _logger.LogInformation("FFmpeg found via fallback path: {Path}", path);
                                break;
                            }
                        }
                    }
                }
            }

            if (success)
            {
                IsFfmpegInstalled = true;
                FfmpegVersion = version;
                FfmpegStatus = $"✅ Installed (v{version})";

                // Update global config
                _config.IsFfmpegAvailable = true;
                _config.FfmpegVersion = version;
                _configManager.Save(_config);

                _logger.LogInformation("FFmpeg validation successful: v{Version}", version);
            }
            else
            {
                IsFfmpegInstalled = false;
                FfmpegStatus = "❌ Not Found in PATH";

                // Update global config
                _config.IsFfmpegAvailable = false;
                _config.FfmpegVersion = "";
                _configManager.Save(_config);

                _logger.LogWarning("FFmpeg not found. Sonic Integrity features will be disabled.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg validation failed unexpectedly");
            IsFfmpegInstalled = false;
            FfmpegStatus = "❌ Check Failed";
        }

        OnPropertyChanged(nameof(IsFfmpegInstalled));
        OnPropertyChanged(nameof(CanInstallFfmpeg));
        OnPropertyChanged(nameof(FfmpegStatus));
        OnPropertyChanged(nameof(FfmpegVersion));
    }

    /// <summary>
    /// Attempts to run ffmpeg -version with timeout and captures stderr (where FFmpeg prints version info).
    /// </summary>
    private async Task<(bool success, string version)> TryFfmpegCommandAsync(string ffmpegPath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5-second timeout

        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, // FFmpeg writes to stderr!
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };

            var outputBuilder = new System.Text.StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };

            process.EnableRaisingEvents = true;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode == 0)
            {
                var output = outputBuilder.ToString();

                // Parse version: "ffmpeg version 6.0.1-full_build-www.gyan.dev" or "ffmpeg version N-109688-g5...github.com/BtbN/FFmpeg-Builds"
                var match = System.Text.RegularExpressions.Regex.Match(output, @"ffmpeg version (\d+(\.\d+)+)");
                var version = match.Success ? match.Groups[1].Value : "unknown";

                return (true, version);
            }

            return (false, "");
        }
        catch (System.ComponentModel.Win32Exception) // File not found
        {
            return (false, "");
        }
        catch (OperationCanceledException) // Timeout
        {
            _logger.LogWarning("FFmpeg command timed out after 5 seconds at path: {Path}", ffmpegPath);
            return (false, "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute FFmpeg at path: {Path}", ffmpegPath);
            return (false, "");
        }
    }
}
