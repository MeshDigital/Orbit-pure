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
                // Phase 2.4: Load ranking strategy from config
                // TEMPORARILY DISABLED: Causing NullReferenceException on startup
                // TODO: Fix this after app launches
                /*
                var config = Services.GetRequiredService<ConfigManager>().GetCurrent();
                ISortingStrategy strategy = (config.RankingPreset ?? "Balanced") switch
                {
                    "Quality First" => new QualityFirstStrategy(),
                    "DJ Mode" => new DJModeStrategy(),
                    _ => new BalancedStrategy()
                };
                ResultSorter.SetStrategy(strategy);
                Serilog.Log.Information("Loaded ranking strategy: {Strategy}", config.RankingPreset ?? "Balanced");
                */
                
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
                        });

                        // --- THE BARRIER: WE ARE NOW DATA-SAFE ---
                        // All subsequent background services that hit the DB can now start.
                        
                        // Initialize and Start DownloadManager Orchestrator
                        var downloadManager = Services.GetRequiredService<DownloadManager>();
                        _ = downloadManager.StartAsync(); // Auto-start engine on launch

                        // Activate post-download spectral scan listener (eager resolve so it
                        // subscribes to TrackStateChangedEvent immediately after the engine starts).
                        _ = Services.GetRequiredService<PostDownloadSpectralScanService>();



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
            var appConfig = configManager.Load();
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
        
        // Session 2: Performance Optimization - Extracted services
        services.AddSingleton<LibraryOrganizationService>();
        services.AddSingleton<IAudioIntegrityService, AudioIntegrityService>();
        services.AddSingleton<PostDownloadSpectralScanService>(); // Runs FFT analysis on completed FLAC downloads
        services.AddSingleton<ArtworkPipeline>();
        services.AddSingleton<DragAdornerService>();
        
        // Session 3: Performance Optimization - Polymorphic taggers


        // Services
        services.AddSingleton<INetworkHealthService, NetworkHealthService>();
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
        services.AddSingleton<ColumnConfigurationService>();
        services.AddSingleton<SmartCrateService>();
        services.AddSingleton<PlaylistExportService>();

        // Audio Player
        services.AddSingleton<IAudioPlayerService, AudioPlayerService>();
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
        services.AddSingleton<LibraryFolderScannerService>();

        // Download manager
        
        // Phase 4.6 Hotfix: Search String Normalization
        services.AddSingleton<SLSKDONET.Services.Network.ProtocolHardeningService>();
        services.AddSingleton<SearchNormalizationService>();
        services.AddSingleton<ISafetyFilterService, SafetyFilterService>();
        services.AddSingleton<SearchResultMatcher>();
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
        services.AddSingleton<IUserInputService, UserInputService>();
        services.AddSingleton<IFileInteractionService, FileInteractionService>();
        services.AddSingleton<INotificationService, NotificationServiceAdapter>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
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
        services.AddSingleton<SidebarViewModel>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        // Lazy<MainViewModel> breaks the circular dependency: MainViewModel → GlobalHotkeyService → KeyboardEventRouter → MainViewModel
        services.AddSingleton(sp => new Lazy<MainViewModel>(sp.GetRequiredService<MainViewModel>));
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<UserCollectionViewModel>();
        services.AddSingleton<SearchFilterViewModel>(); // [FIX] Added missing registration
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<BulkOperationViewModel>();
        services.AddSingleton<HomeViewModel>();

        // [NEW] Library Scanning
        services.AddSingleton<LibraryFolderScannerService>();
        
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
        services.AddTransient<Views.Avalonia.DecksPage>();
        services.AddTransient<Views.Avalonia.TimelinePage>();
        services.AddTransient<Views.Avalonia.StemsPage>();
        services.AddTransient<Views.Avalonia.WorkstationPage>();
        services.AddSingleton<Services.ICuePointService, Services.CuePointService>();
        services.AddSingleton<Services.Audio.StemPreferenceService>();
        services.AddSingleton<Services.Audio.MixdownService>();
        services.AddSingleton<Services.WorkstationSessionService>();
        services.AddSingleton<Services.IUndoService, Services.UndoService>();

        // ── Auto-cue / phrase detection pipeline ──────────────────────────
        services.AddSingleton<Services.CueGenerationService>();
        services.AddSingleton<Services.AudioAnalysis.CuePointDetectionService>();
        services.AddSingleton<Services.PhraseAlignmentService>();
        services.AddSingleton<Services.IPhraseAlignmentService>(sp =>
            sp.GetRequiredService<Services.PhraseAlignmentService>());
        services.AddSingleton<Services.AnalyzeTrackStructureJob>();

        services.AddSingleton<ViewModels.Workstation.WorkstationViewModel>();
        services.AddSingleton<Services.AnalysisQueueService>();

        // ── Task 1.5: Beatgrid Detection ──────────────────────────────────
        services.AddSingleton<Services.AudioAnalysis.BeatgridDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.BpmDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.KeyDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.AudioIngestionPipeline>();
        services.AddSingleton<Services.AudioAnalysis.EssentiaRunner>();

        // ── Task 1.6: Waveform + Energy Extraction ───────────────────────
        services.AddSingleton<Services.AudioAnalysis.WaveformExtractionService>();
        services.AddSingleton<Services.AudioAnalysis.EnergyAnalysisService>();
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

        // Any exception originating from Soulseek.NET library itself
        if (ex.Source?.Contains("Soulseek", StringComparison.OrdinalIgnoreCase) == true)
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
