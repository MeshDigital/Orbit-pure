using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Entertainment;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using SLSKDONET.Services.Input;
using SLSKDONET.Views;
using SLSKDONET.Views.Avalonia;
using SLSKDONET.ViewModels.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace SLSKDONET;

/// <summary>
/// Avalonia application class for cross-platform UI
/// </summary>
public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    /// <summary>
    /// True only once the user has explicitly chosen "Exit" from the tray menu (or another real
    /// shutdown path sets it). Closing the main window (X button, Alt+F4, taskbar close) does NOT
    /// set this — MainWindow.OnWindowClosing checks it to decide whether to actually exit or just
    /// hide to the tray, so the download engine (and the app's whole background queue processing)
    /// keeps running unattended instead of the process exiting the moment the window closes.
    /// </summary>
    public bool IsExitRequested { get; set; }
    private Views.Avalonia.ErrorStreamWindow? _errorStreamWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        // Phase 12: Global Exception Handling - Setup before anything else
        SetupGlobalExceptionHandling();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Configure services
            Services = ConfigureServices();

            // Eagerly activate background queue listeners that subscribe to the event bus.
            // Without resolving this singleton, manual Analyse actions appear to do nothing.
            _ = Services.GetRequiredService<Services.AnalysisQueueService>();

            // Eagerly activate the network activity monitor so it's observing from startup, not
            // only once something (e.g. the Settings page) happens to resolve it first.
            _ = Services.GetRequiredService<Services.NetworkActivityMonitor>();

            // Register shutdown handler to prevent orphaned processes
            desktop.Exit += async (_, __) =>
            {
                Serilog.Log.Information("Application shutdown initiated - cleaning up services...");
                
                try
                {
                    // Disconnect Soulseek client
                    try
                    {
                        var soulseekAdapter = Services?.GetService<ISoulseekAdapter>();
                        if (soulseekAdapter != null)
                        {
                            Serilog.Log.Information("Disconnecting Soulseek client...");
                            await soulseekAdapter.DisconnectAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Failed to disconnect Soulseek client");
                    }

                    // Clear Spotify credentials if configured
                    try
                    {
                        var config = Services?.GetService<ConfigManager>()?.GetCurrent();
                        if (config?.ClearSpotifyOnExit ?? false)
                        {
                            var spotifyAuthService = Services?.GetService<SpotifyAuthService>();
                            if (spotifyAuthService != null)
                            {
                                Serilog.Log.Information("Clearing Spotify credentials...");
                                await spotifyAuthService.ClearCachedCredentialsAsync();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Failed to clear Spotify credentials");
                    }

                    // Close database connections
                    try
                    {
                        var databaseService = Services?.GetService<DatabaseService>();
                        if (databaseService != null)
                        {
                            Serilog.Log.Information("Closing database connections...");
                            await databaseService.CloseConnectionsAsync();
                        }

                        // Phase 2A: Close Crash Recovery Journal (prevents locked WAL files)
                        var crashJournal = Services?.GetService<CrashRecoveryJournal>();
                        if (crashJournal != null)
                        {
                            Serilog.Log.Information("Closing crash recovery journal...");
                            await crashJournal.DisposeAsync();
                        }

                        // Plain AddSingleton services are never auto-disposed (the container itself
                        // is never disposed), so the EventListener-based network monitor needs an
                        // explicit unsubscribe here to avoid leaking its runtime event subscriptions.
                        Services?.GetService<Services.NetworkActivityMonitor>()?.Dispose();

                        // Stop IHostedService background workers
                        try
                        {
                            if (Services != null)
                            {
                                var hostedServices = Services.GetServices<IHostedService>();
                                foreach (var hostedService in hostedServices)
                                {
                                    Serilog.Log.Information("Stopping hosted service: {Service}", hostedService.GetType().Name);
                                    await hostedService.StopAsync(CancellationToken.None);
                                }
                            }
                        }
                        catch (Exception hostedEx)
                        {
                            Serilog.Log.Warning(hostedEx, "Failed to cleanly stop hosted background services");
                        }

                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Failed to close database connections or stop services");
                    }

                    Serilog.Log.Information("Application shutdown completed");
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Error during application shutdown");
                }
                finally
                {
                    // Ensure Serilog is flushed before process terminates
                    Serilog.Log.CloseAndFlush();
                }
            };

            try
            {
                // Phase 10: Biggers App Refactoring - Config Migration
                // Detect legacy weights and migrate to SearchPolicy
                try {
                     var configManager = Services.GetRequiredService<ConfigManager>();
                     var migrationConfig = configManager.Load(); // Reload to be sure
                     var migrationService = Services.GetRequiredService<ConfigMigrationService>();
                     
                     if (migrationService.Migrate(migrationConfig))
                     {
                         configManager.Save(migrationConfig);
                         Serilog.Log.Information("✅ Configuration migrated to 'Biggers App' Search Policy");
                     }
                }
                catch (Exception profEx)
                {
                    Serilog.Log.Warning(profEx, "Config migration failed (non-critical)");
                }

                // Phase 7: Load ranking strategy and weights from config
                var configDispatcher = Services.GetRequiredService<ConfigManager>();
                var config = configDispatcher.GetCurrent() ?? new AppConfig();
                
                string profile = config.RankingProfile ?? "Balanced";

                // ResultSorter.SetWeights(config.CustomWeights ?? ScoringWeights.Balanced); // Removed: Obsolete API
                ResultSorter.SetConfig(config);
                
                Serilog.Log.Information("Loaded ranking strategy: {Profile}", profile);

                // Phase 8: Validate FFmpeg availability - Moved to background task

                // Show Splash Screen first
                var splashScreen = new SLSKDONET.Views.Avalonia.SplashScreen();
                
                // Set as main window temporarily so it shows up as the app window
                desktop.MainWindow = splashScreen;
                splashScreen.Show();
                splashScreen.UpdateStatus("Initializing Database...");
                
                // Yield to let the UI thread render the splash screen
                await Task.Delay(50);
                
                // CRITICAL FIX: Initialize Database BEFORE creating the UI to prevent SQLite locks
                _ = Task.Run(async () =>
                {
                    MainViewModel? mainVm = null;
                    var initCts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); 
                    
                    try
                    {
                        var databaseService = Services.GetRequiredService<DatabaseService>();
                        
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => splashScreen.UpdateStatus("Optimizing Database..."));
                        await databaseService.InitAsync().WaitAsync(initCts.Token);
                        
                        Serilog.Log.Information("✅ Database initialization completed successfully");
                        
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => splashScreen.UpdateStatus("Starting UI..."));
                        await Task.Delay(50);

                        // Create main window and show it immediately on the UI thread
                        // We resolve MainViewModel on the UI thread because it creates UI-bound components (like TreeDataGridSource)
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            mainVm = Services.GetRequiredService<MainViewModel>();
                            mainVm.StatusText = "Finalizing UI...";
                            mainVm.IsInitializing = true;
                            
                            var mainWindow = new Views.Avalonia.MainWindow
                            {
                                DataContext = mainVm
                            };

                            desktop.MainWindow = mainWindow;
                            mainWindow.Show();
                            splashScreen.Close();

                            // Real OS-level notifications (Windows Action Center toasts) need the
                            // main window's native handle, which only exists after Show().
                            var toastHandle = mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                            Services.GetRequiredService<WindowsToastService>().Initialize(toastHandle);
                        });

                        // --- THE BARRIER: WE ARE NOW DATA-SAFE ---
                        // All subsequent background services that hit the DB can now start.
                        
                        // Initialize and Start DownloadManager Orchestrator
                        var downloadManager = Services.GetRequiredService<DownloadManager>();
                        _ = downloadManager.StartAsync(); // Auto-start engine on launch

                        // Activate post-download spectral scan listener (eager resolve so it
                        // subscribes to TrackStateChangedEvent immediately after the engine starts).
                        _ = Services.GetRequiredService<PostDownloadSpectralScanService>();
                        _ = Services.GetRequiredService<PostDownloadDurationCaptureService>();

                        // NativeDependencyHealthService.IsHealthy defaults to false and only ever
                        // gets computed inside CheckHealthAsync() — which nothing in the app was
                        // actually calling (only its own unit tests did). Result: the Library
                        // context menu's "Analyse Track"/"Hard Retry" items (IsEnabled bound to
                        // AreDependenciesHealthy) stayed permanently disabled for the whole session
                        // regardless of whether FFmpeg/Essentia were genuinely available.
                        _ = Services.GetRequiredService<NativeDependencyHealthService>().CheckHealthAsync();

                        // Fire-and-forget: a single throttled GitHub Releases check. Never awaited
                        // so a slow/unreachable network never delays startup; all failures inside
                        // are caught and logged, never thrown.
                        _ = Services.GetRequiredService<IUpdateCheckService>().CheckForUpdatesAsync();

                        // Eager-resolve chat/notification services so they start listening for
                        // incoming Soulseek messages from app launch, not just after the user
                        // first opens Users & Contacts (these are otherwise only constructed
                        // on-demand, which meant incoming chat before that point was silently
                        // dropped — never persisted, never surfaced). PeerVerificationChallengeService
                        // in particular needs this: its whole purpose is reacting to peer messages
                        // that arrive during a download, which usually happens before the user has
                        // any reason to open the Users page.
                        _ = Services.GetRequiredService<ChatService>();
                        _ = Services.GetRequiredService<RoomChatService>();
                        _ = Services.GetRequiredService<NotificationCenterService>();
                        _ = Services.GetRequiredService<PeerVerificationChallengeService>();

                        // Start IHostedService background workers (like BackgroundJobWorker)
                        try
                        {
                            var hostedServices = Services.GetServices<IHostedService>();
                            foreach (var hostedService in hostedServices)
                            {
                                _ = hostedService.StartAsync(CancellationToken.None);
                            }
                            Serilog.Log.Information("✅ Started hosted background services");
                        }
                        catch (Exception hostedEx)
                        {
                            Serilog.Log.Error(hostedEx, "Failed to start hosted background services");
                        }

                        // Phase 2A: Initialize Crash Recovery Journal
                        try
                        {
                            var crashJournal = Services.GetRequiredService<CrashRecoveryJournal>();
                            await crashJournal.InitAsync();
                            Serilog.Log.Information("✅ Crash Recovery Journal initialized");
                            
                            var crashRecovery = Services.GetRequiredService<CrashRecoveryService>();
                            await crashRecovery.RecoverAsync();
                        }
                        catch (Exception journalEx)
                        {
                            Serilog.Log.Warning(journalEx, "Crash recovery failed (non-critical)");
                        }

                        // Start Library Sync
                        try
                        {
                            var libraryService = Services.GetRequiredService<ILibraryService>();
                            await libraryService.SyncLibraryEntriesFromTracksAsync();
                            Serilog.Log.Information("✅ Start-up Library synchronization completed");
                        }
                        catch (Exception syncEx)
                        {
                            Serilog.Log.Error(syncEx, "Start-up Library sync failed");
                        }
                        
                        // Start watching any library folders flagged for auto-import
                        try
                        {
                            var folderWatchService = Services.GetRequiredService<LibraryFolderWatchService>();
                            await folderWatchService.StartAsync();
                        }
                        catch (Exception watchEx)
                        {
                            Serilog.Log.Warning(watchEx, "Library folder watch service failed to start (non-critical)");
                        }

                        // Load projects into the LibraryViewModel
                        if (mainVm?.LibraryViewModel != null)
                        {
                            await mainVm.LibraryViewModel.LoadProjectsAsync();
                        }
                        
                        // Update UI on completion
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (mainVm != null)
                            {
                                mainVm.IsInitializing = false;
                                mainVm.StatusText = "Ready";
                            }
                            Serilog.Log.Information("Background initialization completed");

                            // Start maintenance tasks AFTER initialization is confirmed complete
                            _ = RunMaintenanceTasksAsync();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        Serilog.Log.Fatal("CRITICAL: Application initialization TIMED OUT after 2 minutes.");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                           splashScreen.UpdateStatus("Initialization Timeout. Please restart.");
                        });
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "Background initialization failed");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                            splashScreen.UpdateStatus($"Error: {ex.Message}");
                        });
                    }
                });
                
            }
            catch (Exception ex)
            {
                // Log startup error
                Serilog.Log.Fatal(ex, "Startup failed during framework initialization");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Tray Icon Event Handlers
    private void ShowWindow_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && 
            desktop.MainWindow != null)
        {
            desktop.MainWindow.Show();
            desktop.MainWindow.WindowState = WindowState.Normal;
            desktop.MainWindow.Activate();
        }
    }

    private void HideWindow_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && 
            desktop.MainWindow != null)
        {
            desktop.MainWindow.Hide();
        }
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IsExitRequested = true;
            desktop.Shutdown();
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        ConfigureSharedServices(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Shared service configuration used by both WPF and Avalonia
    /// </summary>
    public static void ConfigureSharedServices(IServiceCollection services)
    {
        // Logging - Use Serilog
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.Services.AddSingleton<ILoggerProvider>(new SerilogLoggerProvider(Serilog.Log.Logger, dispose: true));
        });

        // Configuration
        services.AddSingleton<ConfigMigrationService>(); // [NEW] Biggers App Migration
        services.AddSingleton<ConfigManager>();
        services.AddSingleton(provider =>
        {
            var configManager = provider.GetRequiredService<ConfigManager>();
            AppConfig appConfig;
            try
            {
                appConfig = configManager.Load();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load config.ini — falling back to default settings so the app can still start");
                appConfig = new AppConfig();
            }
            if (string.IsNullOrEmpty(appConfig.DownloadDirectory))
                appConfig.DownloadDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SLSKDONET");
            return appConfig;
        });

        // EventBus - Unified event communication
        services.AddSingleton<IEventBus, EventBusService>();
        
        // Phase 1A: SafeWrite Service - Atomic file operations (ORBIT v1.0)
        services.AddSingleton<SLSKDONET.Services.IO.IFileWriteService, SLSKDONET.Services.IO.SafeWriteService>();
        
        // Phase 2A: Crash Recovery - Journal & Recovery Services (ORBIT v1.0)
        services.AddSingleton<CrashRecoveryJournal>();
        services.AddSingleton<CrashRecoveryService>();
        
        //Session 1: Performance Optimization - Smart caching layer
        services.AddSingleton<LibraryCacheService>();
        services.AddSingleton<ISavedDoublesService, SavedDoublesService>();
        
        // Session 2: Performance Optimization - Extracted services
        services.AddSingleton<IAudioIntegrityService, AudioIntegrityService>();
        services.AddSingleton<PostDownloadSpectralScanService>(); // Runs FFT analysis on completed FLAC downloads
        services.AddSingleton<PostDownloadDurationCaptureService>(); // TagLib duration probe for every completed download (any format)
        services.AddSingleton<AudioCorruptionScannerService>();   // Fast per-file corruption probe (FFmpeg + NAudio)
        services.AddSingleton<LibraryCorruptionScanService>();    // Batch library-wide corruption scan
        services.AddSingleton<CorruptFileRemediationService>();   // Disk delete + DB reset + re-queue for corrupt/missing files
        services.AddSingleton<UnidentifiedTrackCleanupService>(); // Full removal of "ID" placeholder tracks (no identity to redownload against)
        services.AddSingleton<DuplicateTrackCleanupService>(); // Merges duplicate LibraryEntries + removes duplicate in-playlist track rows
        services.AddSingleton<EngineDiagnosticsService>(); // Structured import/search audit trail — "Engine Diagnostics"
        services.AddSingleton<AvailabilityStateReconciliationService>(); // Fixes tracks stuck "FILE MISSING" despite the file existing on disk
        services.AddSingleton<DurationBackfillService>(); // One-time TagLib duration sweep for tracks that predate PostDownloadDurationCaptureService
        services.AddSingleton<DragAdornerService>();
        
        // Session 3: Performance Optimization - Polymorphic taggers


        // Services
        services.AddSingleton<INetworkHealthService, NetworkHealthService>();
        services.AddSingleton<NetworkActivityMonitor>();
        services.AddSingleton<ShareIndexService>();
        services.AddSingleton<ChatAttachmentService>();
        services.AddSingleton<SoulseekAdapter>();
        services.AddSingleton<ISoulseekAdapter>(sp => sp.GetRequiredService<SoulseekAdapter>());
        // Phase B: Connection lifecycle state machine
        services.AddSingleton<ConnectionLifecycleService>();
        services.AddSingleton<IConnectionLifecycleService>(sp => sp.GetRequiredService<ConnectionLifecycleService>());
        services.AddSingleton<FileNameFormatter>();
        services.AddSingleton<ProtectedDataService>();
        services.AddSingleton<ISoulseekCredentialService, SoulseekCredentialService>();

        // Spotify services
        services.AddHttpClient<SpotifyBatchClient>(); // Phase 7: Batch Client for Throttling Fix
        services.AddSingleton<SpotifyInputSource>();
        services.AddSingleton<SpotifyScraperInputSource>();
        
        // Spotify OAuth services

        services.AddSingleton<ISecureTokenStorage>(sp => SecureTokenStorageFactory.Create(sp));
        services.AddSingleton<SpotifyAuthService>();
        services.AddSingleton<ISpotifyMetadataService, SpotifyMetadataService>();
        services.AddSingleton<SpotifyMetadataService>(); // Keep concrete registration just in case
        services.AddSingleton<ArtworkCacheService>(); // Phase 0: Artwork caching
        services.AddSingleton<PlaylistMosaicService>(); // Generates 2×2 mosaic cover art for playlists without a dedicated cover image
        services.AddSingleton<SpotifyBulkFetcher>(); // Phase 8: Robust Bulk Fetcher
        
        // Phase 1: Library Enrichment
        services.AddSingleton<SpotifyEnrichmentService>();
        services.AddSingleton<DiscoveryBridgeService>();

        // Input parsers
        services.AddSingleton<CsvInputSource>();

        // Import Plugin System
        services.AddSingleton<ImportOrchestrator>();
        services.AddSingleton<IImportOrchestrationService, ImportOrchestrationServiceAdapter>();
        // Register concrete types for direct injection
        services.AddSingleton<Services.ImportProviders.SpotifyImportProvider>();
        services.AddSingleton<Services.ImportProviders.CsvImportProvider>();
        services.AddSingleton<Services.ImportProviders.SpotifyLikedSongsImportProvider>();
        services.AddSingleton<Services.ImportProviders.TracklistImportProvider>();
        
        // Phase 1: Persistent Enrichment Queue
        services.AddSingleton<Services.Repositories.IEnrichmentTaskRepository, Services.Repositories.EnrichmentTaskRepository>();
        
        // Register as interface for Orchestrator
        services.AddSingleton<IImportProvider>(sp => sp.GetRequiredService<Services.ImportProviders.SpotifyImportProvider>());
        services.AddSingleton<IImportProvider>(sp => sp.GetRequiredService<Services.ImportProviders.CsvImportProvider>());
        services.AddSingleton<IImportProvider>(sp => sp.GetRequiredService<Services.ImportProviders.SpotifyLikedSongsImportProvider>());
        services.AddSingleton<IImportProvider>(sp => sp.GetRequiredService<Services.ImportProviders.TracklistImportProvider>());

        // Library Action System
        services.AddSingleton<Services.LibraryActions.LibraryActionProvider>();
        services.AddSingleton<Services.LibraryActions.ILibraryAction, Services.LibraryActions.OpenFolderAction>();
        services.AddSingleton<Services.LibraryActions.ILibraryAction, Services.LibraryActions.RemoveFromPlaylistAction>();
        services.AddSingleton<Services.LibraryActions.ILibraryAction, Services.LibraryActions.DeletePlaylistAction>();

        // Download logging and library management
        services.AddSingleton<DownloadLogService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILifecycleProjectionService, LifecycleProjectionService>();
        services.AddSingleton<ColumnConfigurationService>();
        services.AddSingleton<SmartCrateService>();
        services.AddSingleton<PlaylistExportService>();
        services.AddSingleton<Services.Export.UsbExportOrchestrator>();

        // Audio Player
        services.AddSingleton<IAudioPlayerService, AudioPlayerService>();
        services.AddSingleton<ILibraryPreviewPlayer, LibraryPreviewPlayer>();
        services.AddSingleton<PlayerViewModel>();

        // Entertainment Engine Services
        services.AddSingleton<IAmbientModeService, AmbientModeService>();
        services.AddSingleton<IFlowModeService, FlowModeService>();

        // Metadata and tagging service
        services.AddSingleton<ITaggerService, MetadataTaggerService>();
        services.AddSingleton<IFilePathResolverService, FilePathResolverService>();



        // Phase 25: Universal Music Engine (MusicBrainz Integration)
        services.AddSingleton<IMusicBrainzService, MusicBrainzService>();

        // Phase 2.5: Path provider for safe folder structure
        services.AddSingleton<PathProviderService>();
        
        // Library Folder Scanner
        services.AddSingleton<SLSKDONET.Services.Network.ProtocolHardeningService>();
        services.AddSingleton<SearchNormalizationService>();
        services.AddSingleton<ISafetyFilterService, SafetyFilterService>();
        services.AddSingleton<SearchResultMatcher>();
        services.AddSingleton<AutoSearchService>();
        services.AddSingleton<SLSKDONET.Services.Diagnostics.ITrackAuditLogger, SLSKDONET.Services.Diagnostics.TrackAuditLogger>();

        services.AddSingleton<MatchScorer>();
        services.AddSingleton<SoulseekSearchHelper>();
        services.AddSingleton<PrefetchVerifier>();
        services.AddSingleton<ISmartPlaylistService, SmartPlaylistService>();
        
        
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<PeerReliabilityService>();
        
        // Phase 2.5: Download Center ViewModel (singleton observer)
        services.AddSingleton<ViewModels.Downloads.DownloadCenterViewModel>();

        // Database
        services.AddDbContextFactory<AppDbContext>();
        services.AddSingleton<SchemaMigratorService>();
        services.AddSingleton<SLSKDONET.Services.Repositories.ITrackRepository, SLSKDONET.Services.Repositories.TrackRepository>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<IMetadataService, MetadataService>();

        // Navigation and UI services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<PerformanceTracker>(); // live perf overlay (Ctrl+Shift+P) — page-nav and opted-in ViewModel load timings
        services.AddSingleton<IUserInputService, UserInputService>();
        services.AddSingleton<IFileInteractionService, FileInteractionService>();
        services.AddSingleton<INotificationService, NotificationServiceAdapter>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<Services.Telemetry.FlowBuilderSuggestionTelemetryService>();
        // FlowBuilderView resolves this from DI at construction — without this registration the
        // whole Flow Builder tab silently gets a null DataContext (every control dead).
        services.AddSingleton<ViewModels.FlowBuilderViewModel>();
        services.AddSingleton<DashboardService>();
        // Keyboard mapping system (Epic #119)
        services.AddSingleton<IKeyboardMappingService, KeyboardMappingService>();
        services.AddSingleton<IKeyboardTelemetryService, KeyboardTelemetryService>();
        services.AddSingleton<KeyboardEventRouter>();
        services.AddSingleton<KeyboardMappingsViewModel>();
        services.AddSingleton<GlobalHotkeyService>();

        // Global Shell Services
        services.AddSingleton<IRightPanelService, RightPanelService>();
        services.AddSingleton<SimilarTracksViewModel>();
        services.AddSingleton<NotificationCenterService>();
        services.AddSingleton<SidebarViewModel>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        // Lazy<MainViewModel> breaks the circular dependency: MainViewModel → GlobalHotkeyService → KeyboardEventRouter → MainViewModel
        services.AddSingleton(sp => new Lazy<MainViewModel>(sp.GetRequiredService<MainViewModel>));
        services.AddSingleton<SearchViewModel>();
        // Transient (not singleton): a per-profile Users/Contacts page needs its own fresh browse
        // instance per opened profile. SearchViewModel is itself a singleton, so it still captures
        // exactly one UserCollectionViewModel instance at its own one-time construction — the
        // existing Search-page browse overlay's behavior is unchanged by this.
        services.AddTransient<UserCollectionViewModel>();

        // Social: presence, 1:1 chat, chat rooms
        services.AddSingleton<UserPresenceWatchService>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<RoomChatService>();
        services.AddSingleton<PeerVerificationChallengeService>();
        services.AddSingleton<WindowsToastService>();
        services.AddTransient<UsersViewModel>();
        services.AddTransient<UserProfileViewModel>();
        services.AddTransient<RoomsViewModel>();
        services.AddSingleton<SearchFilterViewModel>(); // [FIX] Added missing registration
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<AiEngineService>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<BulkOperationViewModel>();
        services.AddSingleton<HomeViewModel>();

        // [NEW] Library Scanning
        services.AddSingleton<LibraryFolderScannerService>();
        services.AddSingleton<LibraryFolderWatchService>();

        // Orchestration Services
        services.AddSingleton<SearchOrchestrationService>();
        services.AddSingleton<DownloadOrchestrationService>();
        services.AddSingleton<IBulkOperationCoordinator, BulkOperationCoordinator>(); // Phase 10.5: Refined Workflow
        services.AddSingleton<DownloadDiscoveryService>();

        
        // Phase 10: Tagging & Mobility
        services.AddSingleton<SLSKDONET.Services.IO.SafeWriteService>();
        services.AddSingleton<SLSKDONET.Services.IO.IFileWriteService>(sp => sp.GetRequiredService<SLSKDONET.Services.IO.SafeWriteService>());


        
        // Phase 0: ViewModel Refactoring - Library child ViewModels
        services.AddTransient<ViewModels.Library.ProjectListViewModel>();
        services.AddTransient<ViewModels.Library.TrackListViewModel>();
        services.AddTransient<ViewModels.Library.TrackOperationsViewModel>();
        services.AddTransient<ViewModels.Library.SmartPlaylistViewModel>();

        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<ImportPreviewViewModel>();
        services.AddSingleton<ImportHistoryViewModel>();
        services.AddSingleton<SpotifyImportViewModel>();
        services.AddSingleton<ViewModels.LibrarySourcesViewModel>();
        services.AddSingleton<Services.Import.AutoCleanerService>();

        // Utilities
        services.AddSingleton<SearchQueryNormalizer>();
        
        // Phase 10.5: Native Dependency Health (Reliability)
        services.AddSingleton<NativeDependencyHealthService>();

        // Update check — single GET against GitHub Releases, opt-out via AppConfig.EnableUpdateCheck.
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        
        // Views - Register all page controls for NavigationService
        services.AddTransient<Views.Avalonia.HomePage>();
        services.AddTransient<Views.Avalonia.SearchPage>();
        services.AddTransient<Views.Avalonia.LibraryPage>();
        services.AddTransient<Views.Avalonia.DownloadsPage>();
        services.AddTransient<Views.Avalonia.NowPlayingPage>();
        services.AddTransient<Views.Avalonia.SettingsPage>();
        services.AddTransient<Views.Avalonia.ImportPage>();
        services.AddTransient<Views.Avalonia.ImportPreviewPage>();
        services.AddTransient<Views.Avalonia.AnalysisPage>();
        services.AddSingleton<ViewModels.AnalysisPageViewModel>();
        services.AddTransient<Views.Avalonia.StemsPage>();
        services.AddTransient<Views.Avalonia.WorkstationPage>();
        services.AddTransient<Views.Avalonia.UsersPage>();
        services.AddTransient<Views.Avalonia.CueForgePagee>();
        services.AddTransient<Views.Avalonia.FlowBuilderPage>();
        services.AddSingleton<Services.ICuePointService, Services.CuePointService>();
        services.AddSingleton<Services.Audio.StemPreferenceService>();
        services.AddSingleton<Services.Audio.MixdownService>();
        services.AddSingleton<Services.WorkstationSessionService>();
        services.AddSingleton<Services.OrbSessionBundleService>();
        services.AddSingleton<Services.IUndoService, Services.UndoService>();

        // ── EDMFormer ML phrase detection service (optional — requires local Python service on port 7774) ──
        services.AddSingleton<Services.Audio.IEdmFormerService, Services.Audio.EdmFormerService>();

        // ── Auto-cue / phrase detection pipeline ──────────────────────────
        services.AddSingleton<Services.AudioAnalysis.CuePointDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.DnBTransientDetectionService>();
        services.AddSingleton<Services.DnBCueNamingService>();
        services.AddSingleton<Engine.Analysis.BreakbeatAnalysisStrategy>();
        services.AddSingleton<Engine.Analysis.FourOnTheFloorAnalysisStrategy>();
        services.AddSingleton<Services.CamelotKeyDisplayService>();
        services.AddSingleton<Services.PhraseAlignmentService>();
        services.AddSingleton<Services.IPhraseAlignmentService>(sp =>
            sp.GetRequiredService<Services.PhraseAlignmentService>());
        services.AddSingleton<Services.AnalyzeTrackStructureJob>();

        services.AddSingleton<SLSKDONET.ViewModels.Workstation.WorkstationViewModel>();
        services.AddSingleton<SLSKDONET.ViewModels.Workstation.CueEditorViewModel>();
        services.AddSingleton<SLSKDONET.ViewModels.CueForgeViewModel>();
        services.AddSingleton<SLSKDONET.Engine.Analysis.AnalysisPipeline>(sp =>
            new SLSKDONET.Engine.Analysis.AnalysisPipeline(
                sp.GetRequiredService<SLSKDONET.Services.AudioAnalysis.AudioIngestionPipeline>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SLSKDONET.Engine.Analysis.AnalysisPipeline>>(),
                sp.GetService<SLSKDONET.Services.Audio.IEdmFormerService>()));
        services.AddSingleton<SLSKDONET.Engine.Cueing.CueGenerationService>();
        services.AddSingleton<Services.AnalysisQueueService>();

        // ── Task 1.5: Beatgrid Detection ──────────────────────────────────
        services.AddSingleton<Services.AudioAnalysis.BeatgridDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.BpmDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.KeyDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.EnergyScoringService>();
        services.AddSingleton<Services.AudioAnalysis.HarmonicAnalysisService>();
        services.AddSingleton<Services.AudioAnalysis.HarmonicCompatibilityService>();
        services.AddSingleton<Services.AudioAnalysis.TrackFingerprintBuilderService>();
        services.AddSingleton<Services.AudioAnalysis.TrackFingerprintStore>();
        services.AddSingleton<Services.AudioAnalysis.TrackFingerprintBackfillService>();
        services.AddSingleton<Services.AudioAnalysis.AudioIngestionPipeline>();
        services.AddSingleton<Services.AudioAnalysis.EssentiaRunner>();
        services.AddSingleton<Services.FrequentSourceService>();
        services.AddSingleton<Services.PrefetchService>();

        // ── Task 1.6: Waveform + Energy Extraction ───────────────────────
        services.AddSingleton<Services.AudioAnalysis.WaveformExtractionService>();
        services.AddSingleton<Services.AudioAnalysis.EnergyAnalysisService>();
        // DiscogsEffnet embedding + genre/mood classification via ONNX Runtime, run in-process —
        // replaces the Essentia CLI's TensorFlow-model layer, which was found to silently produce
        // no output at all with the binary ORBIT bundles.
        services.AddSingleton<Services.Similarity.DiscogsEffnetEmbeddingExtractor>();
        services.AddSingleton<Services.Similarity.EffnetClassifierHeadService>();
        services.AddSingleton<Services.AudioAnalysis.AudioAnalysisService>();
        services.AddSingleton<Services.IAudioAnalysisService>(sp =>
            sp.GetRequiredService<Services.AudioAnalysis.AudioAnalysisService>());

        // ── Issue 2.1: Embedding Extraction Service ───────────────────────
        services.AddSingleton<Services.Embeddings.EmbeddingExtractionService>();
        services.AddSingleton<Services.Embeddings.IEmbeddingExtractionService>(sp =>
            sp.GetRequiredService<Services.Embeddings.EmbeddingExtractionService>());

        // ── Issue 2.2: Similarity Index ───────────────────────────────────
        services.AddSingleton<Services.Similarity.SimilarityIndex>();
        services.AddSingleton<Services.ISimilarityService, Services.SimilarityServiceAdapter>();
        // Section-level feature vectors (Intro/Drop/Outro per track) for
        // transition-aware playlist optimisation — no new DB schema needed.
        services.AddSingleton<Services.Similarity.SectionVectorService>();
        services.AddSingleton<Services.Similarity.TrackSimilarityService>();
        services.AddSingleton<Services.Similarity.TransitionStyleClassifier>();
        services.AddSingleton<Services.Playlist.PlaylistIntelligenceService>();

        // ── Tasks 5.1-5.5: Dual Deck Engine + Sync ───────────────────────
        services.AddSingleton<ViewModels.DeckViewModel>();

        // ── Tasks 6.1-6.5: Stem Separation + Mixer + EQ ──────────────────
        services.AddSingleton<Services.Audio.StemCacheService>();
        services.AddSingleton<Services.Audio.Separation.DemucsModelManager>();
        services.AddSingleton<Services.Audio.Separation.DemucsOnnxSeparator>();
        services.AddSingleton<Services.Audio.Separation.CachedStemSeparator>(sp =>
            new Services.Audio.Separation.CachedStemSeparator(
                sp.GetRequiredService<Services.Audio.Separation.DemucsOnnxSeparator>(),
                sp.GetRequiredService<Services.Audio.StemCacheService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Audio.Separation.CachedStemSeparator>>()));
        services.AddSingleton<Services.IStemSeparationService, Services.StemSeparationServiceAdapter>();
        services.AddSingleton<Services.Audio.ISurgicalProcessingService, Services.Audio.SurgicalProcessingService>();
        services.AddSingleton<Services.Audio.ITransitionPreviewPlayer, Services.Audio.TransitionPreviewPlayer>();
        services.AddSingleton<Services.IWaveformCacheService, Services.WaveformCacheService>();
        services.AddSingleton<ViewModels.StemMixerViewModel>();
        services.AddSingleton<ViewModels.StemWaveformViewModel>();
        services.AddSingleton<ViewModels.NeuralMixEqViewModel>();

        // ── Task 7.4-7.6: Timeline Editor ViewModel ───────────────────────
        services.AddSingleton<ViewModels.TimelineViewModel>();

        // ── Task 8.4: YouTube Chapter Export ─────────────────────────────
        services.AddSingleton<Services.Video.YouTubeChapterExportService>();

        // ── Task 9.1: Rekordbox USB translation + auto-export watcher ─────
        services.AddSingleton<Services.Library.RekordboxExportExtensions>();

        // ── Issue 2.3 + 2.4: Playlist Optimizer (AI Automix) ─────────────
        services.AddSingleton<Services.Playlist.PlaylistOptimizer>();

        // ── Issue 7.1: Background Job Queue (Channel<T>) ──────────────────
        services.AddSingleton<Services.Jobs.BackgroundJobQueue>();
        services.AddSingleton<Services.Jobs.IBackgroundJobQueue>(sp =>
            sp.GetRequiredService<Services.Jobs.BackgroundJobQueue>());
        services.AddHostedService<Services.Jobs.BackgroundJobWorker>(sp =>
            new Services.Jobs.BackgroundJobWorker(
                sp.GetRequiredService<Services.Jobs.BackgroundJobQueue>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Jobs.BackgroundJobWorker>>()));

        services.AddHostedService<Services.AutoDownload.GhostAcquisitionOrchestrator>(sp =>
            new Services.AutoDownload.GhostAcquisitionOrchestrator(
                sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                sp.GetRequiredService<AutoSearchService>(),
                sp.GetRequiredService<DownloadDiscoveryService>(),
                sp.GetRequiredService<SearchResultMatcher>(),
                sp.GetRequiredService<DownloadManager>(),
                sp.GetRequiredService<ILibraryService>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<AppConfig>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.AutoDownload.GhostAcquisitionOrchestrator>>()));
    }

    /// <summary>
    /// Phase 8: Maintenance Task - Runs daily cleanup operations.
    /// - Deletes backup files older than 7 days
    /// - Vacuums database for performance
    /// </summary>
    private async Task RunMaintenanceTasksAsync()
    {
        await PerformMaintenanceAsync();
    }

    private async Task PerformMaintenanceAsync()
    {
        var config = Services?.GetService<AppConfig>();
        if (config == null) return;
        
        Serilog.Log.Information("[Maintenance] Starting daily maintenance tasks...");
        
        // Task 1: Clean old backup files (7-day retention)
        if (!string.IsNullOrEmpty(config.DownloadDirectory) && Directory.Exists(config.DownloadDirectory))
        {
            try
            {
                var backupFiles = Directory.GetFiles(config.DownloadDirectory, "*.backup", SearchOption.AllDirectories)
                    .Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-7))
                    .ToList();
                
                if (backupFiles.Any())
                {
                    foreach (var backupFile in backupFiles)
                    {
                        try
                        {
                            File.Delete(backupFile);
                            Serilog.Log.Debug("[Maintenance] Deleted old backup: {File}", Path.GetFileName(backupFile));
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "[Maintenance] Failed to delete backup: {File}", backupFile);
                        }
                    }
                    
                    Serilog.Log.Information("[Maintenance] Cleaned {Count} old backup files (>7 days)", backupFiles.Count);
                }
                else
                {
                    Serilog.Log.Debug("[Maintenance] No old backups to clean");
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Maintenance] Backup cleanup failed");
            }
        }
        
        // Task 2: Vacuum database for performance
        try
        {
            var dbService = Services?.GetService<DatabaseService>();
            if (dbService != null)
            {
                await dbService.VacuumDatabaseAsync();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Maintenance] Database vacuum failed");
        }

        // Task 3: Schedule batch sync of embeddings
        try
        {
            var embeddingService = Services?.GetService<SLSKDONET.Services.Embeddings.IEmbeddingExtractionService>();
            if (embeddingService != null)
            {
                embeddingService.ScheduleBatchSync();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Maintenance] Failed to schedule embedding batch sync: {Message}", ex.Message);
        }
        
        Serilog.Log.Information("[Maintenance] Daily maintenance completed");
    }

    /// <summary>
    /// Phase 12: Global Exception Handling - Setup safety net for beta testing
    /// </summary>
    private void SetupGlobalExceptionHandling()
    {
        // Handle unhandled exceptions on the UI thread
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            HandleGlobalException(exception, "AppDomain Unhandled Exception", e.IsTerminating);
        };

        // Handle unobserved task exceptions (fire-and-forget tasks)
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            HandleGlobalException(e.Exception, "Unobserved Task Exception", false);
            e.SetObserved(); // Prevent the exception from crashing the finalizer thread
        };

        Serilog.Log.Information("✅ Global exception handling initialized");
    }

    /// <summary>
    /// Phase 12: Global exception handler - Stream errors to persistent window
    /// </summary>
    /// <summary>
    /// Returns true for transient Soulseek P2P network noise that should not surface in the UI.
    /// These are expected cancellation/disposal/network failures during distributed parent negotiation.
    /// </summary>
    private static bool IsTransientSoulseekError(Exception ex)
    {
        var rootCauses = GetRootCauseExceptions(ex).ToList();
        return rootCauses.Count > 0 && rootCauses.All(IsTransientSoulseekRootCause);
    }

    private static IEnumerable<Exception> GetRootCauseExceptions(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                foreach (var nested in GetRootCauseExceptions(inner))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (ex.InnerException is not null)
        {
            foreach (var inner in GetRootCauseExceptions(ex.InnerException))
            {
                yield return inner;
            }

            yield break;
        }

        yield return ex;
    }

    private static bool IsTransientSoulseekRootCause(Exception ex)
    {
        if (ex is OperationCanceledException)
            return true;

        // Soulseek.NET known teardown race: Timer can be null/disposed while network read loop
        // is unwinding. This surfaces as NullReferenceException in Soulseek.Extensions.Reset.
        // Treat as transient noise so it does not pollute the error stream.
        if (ex is NullReferenceException)
        {
            var stack = ex.StackTrace ?? string.Empty;
            if (stack.Contains("Soulseek.Extensions.Reset", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("Soulseek.Network.Tcp.Connection.ReadInternalAsync", StringComparison.OrdinalIgnoreCase) ||
                ex.ToString().Contains("Soulseek.Extensions.Reset(Timer timer)", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ex.Message.Contains("Transfer failed: Transfer complete", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Transfer complete", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ex is InvalidOperationException ioe &&
            ioe.Message.Contains("Not listening. You must call the Start() method before calling this method.", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ex is ObjectDisposedException ode)
        {
            if (ode.ObjectName?.Contains("MessageConnection", StringComparison.OrdinalIgnoreCase) == true)
                return true;

            if (ode.Message.Contains("MessageConnection", StringComparison.OrdinalIgnoreCase))
                return true;

            // Socket disposed inside Soulseek TCP listener during reconnect/dispose cycle.
            // Source is System.Net.Sockets but the stack trace reveals Soulseek internals.
            var odeStack = ode.StackTrace ?? string.Empty;
            if (odeStack.Contains("Soulseek.Network.Tcp.Listener", StringComparison.OrdinalIgnoreCase) ||
                odeStack.Contains("ListenContinuouslyAsync", StringComparison.OrdinalIgnoreCase))
                return true;

            // "Connection" object from Soulseek.NET disposed mid-transfer
            if (ode.ObjectName?.Equals("Connection", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        if (ex is IOException ioEx)
        {
            var ioMessage = ioEx.ToString();
            if (ioMessage.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase) ||
                ioMessage.Contains("Failed to read", StringComparison.OrdinalIgnoreCase) ||
                ioMessage.Contains("connected party did not properly respond", StringComparison.OrdinalIgnoreCase) ||
                ioMessage.Contains("connected host has failed to respond", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // SocketException: network timeout / connection refused / peer abort during Soulseek churn.
        if (ex is SocketException se && (se.NativeErrorCode == 10060 || se.NativeErrorCode == 10061 || se.NativeErrorCode == 10054 || se.NativeErrorCode == 10053 || se.NativeErrorCode == 995))
            return true;

        // Timeout / inactivity / cancelled I/O noise from Soulseek.NET internals
        if (ex is TimeoutException)
            return true;

        var msg = ex.Message;
        var stackTraceText = ex.StackTrace ?? string.Empty;
        if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("failed to respond", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Failed to read", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Inactivity timeout", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Remote connection closed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("I/O operation has been aborted", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("An existing connection was forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("MessageConnection", StringComparison.OrdinalIgnoreCase) ||
            stackTraceText.Contains("Soulseek.Network.Tcp.Connection.ReadInternalAsync", StringComparison.OrdinalIgnoreCase) ||
            stackTraceText.Contains("Soulseek.Network.MessageConnection.ReadContinuouslyAsync", StringComparison.OrdinalIgnoreCase))
            return true;

        // Any exception type declared in the Soulseek namespace (e.g. TransferReportedFailedException)
        if (ex.GetType().Namespace?.StartsWith("Soulseek", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // Any exception originating from Soulseek.NET library itself
        if (ex.Source?.Contains("Soulseek", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // "Download reported as failed by remote client" — peer aborted transfer, expected during P2P churn
        if (ex.Message.Contains("reported as failed by remote client", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private Views.Avalonia.ErrorStreamWindow CreateErrorStreamWindow()
    {
        var window = new Views.Avalonia.ErrorStreamWindow();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_errorStreamWindow, window))
            {
                _errorStreamWindow = null;
            }
        };

        return window;
    }

    private async void HandleGlobalException(Exception? exception, string source, bool isTerminating)
    {
        try
        {
            var errorMessage = exception?.Message ?? "Unknown error";
            var stackTrace = exception?.ToString() ?? "No stack trace available";

            // Filter transient Soulseek P2P network noise — expected failures during
            // distributed parent negotiation and peer connection cycling.
            if (exception != null && IsTransientSoulseekError(exception))
            {
                Serilog.Log.Debug("[Noise Filter] Suppressed transient Soulseek error: {Message}", exception.Message);
                return;
            }

            if (isTerminating)
            {
                Serilog.Log.Fatal(exception, "🚨 {Source}: {Message}", source, errorMessage);
            }
            else
            {
                Serilog.Log.Warning(exception, "⚠️ {Source}: {Message}", source, errorMessage);
            }

            // Add to error stream window on UI thread
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    // Create window if needed
                    if (_errorStreamWindow == null)
                    {
                        _errorStreamWindow = CreateErrorStreamWindow();
                    }

                    // Add the error
                    _errorStreamWindow.AddError(source, errorMessage, stackTrace);

                    // Show window if not already visible
                    if (!_errorStreamWindow.IsVisible)
                    {
                        _errorStreamWindow.Show();
                        _errorStreamWindow.Activate();
                    }
                    else
                    {
                        // Bring to front
                        _errorStreamWindow.Activate();
                    }

                    // If terminating, show a brief alert
                    if (isTerminating)
                    {
                        var alert = new Window
                        {
                            Title = "Critical Error",
                            Content = new TextBlock
                            {
                                Text = "A critical error occurred and the application will terminate.\nCheck the Error Stream window for details.",
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(20)
                            },
                            SizeToContent = SizeToContent.WidthAndHeight,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen
                        };
                        alert.Show();
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Fatal(ex, "Failed to show error stream window");
                    // Last resort: console output
                    Console.WriteLine($"CRITICAL ERROR: {source} - {errorMessage}");
                }
            });
        }
        catch (Exception handlerEx)
        {
            // Absolute last resort - log to console if everything fails
            Console.WriteLine($"CRITICAL: Exception handler failed: {handlerEx}");
            Console.WriteLine($"Original error: {exception}");
        }
    }

}
