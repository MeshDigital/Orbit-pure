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

public partial class App
{
    /// <summary>
    /// Shared service configuration used by both WPF and Avalonia
    /// </summary>
    public static void ConfigureSharedServices(IServiceCollection services)
    {
        AddOrbitCore(services);
        AddOrbitConnectivity(services);
        AddOrbitLibrary(services);
        AddOrbitPresentation(services);
        AddOrbitSetup(services);
        AddOrbitAnalysis(services);
        AddOrbitAudioAndWorkers(services);
    }

    private static void AddOrbitCore(IServiceCollection services)
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
                appConfig.DownloadDirectory = AppConfig.FallbackDownloadDirectory;
            // A --profile / --setup-preview instance usually runs next to the real ORBIT, which
            // already holds the Soulseek listen port; give the preview its own free port.
            if (SLSKDONET.Data.OrbitPaths.IsProfileOverridden)
                appConfig.ListenPort = SLSKDONET.Services.Setup.PortProbe.FindFreePortFrom(appConfig.ListenPort);
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
        services.AddSingleton<ILibraryCorruptionScanService, LibraryCorruptionScanService>();    // Batch library-wide corruption scan
        services.AddSingleton<ICorruptFileRemediationService, CorruptFileRemediationService>();   // Disk delete + DB reset + re-queue for corrupt/missing files
        services.AddSingleton<UnidentifiedTrackCleanupService>(); // Full removal of "ID" placeholder tracks (no identity to redownload against)
        services.AddSingleton<DuplicateTrackCleanupService>(); // Merges duplicate LibraryEntries + removes duplicate in-playlist track rows
        services.AddSingleton<EngineDiagnosticsService>(); // Structured import/search audit trail — "Engine Diagnostics"
        services.AddSingleton<AvailabilityStateReconciliationService>(); // Fixes tracks stuck "FILE MISSING" despite the file existing on disk
        services.AddSingleton<DurationBackfillService>(); // One-time TagLib duration sweep for tracks that predate PostDownloadDurationCaptureService
        services.AddSingleton<DragAdornerService>();
    }

    private static void AddOrbitConnectivity(IServiceCollection services)
    {
        
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
        // Playlist Discover tab: Beatport public pages + Deezer API → ranked suggestions.
        services.AddHttpClient<Services.Discovery.BeatportCatalogClient>();
        services.AddHttpClient<Services.Discovery.DeezerCatalogClient>();
        services.AddSingleton<Services.Discovery.PlaylistDiscoveryService>();
        services.AddSingleton<Services.Discovery.DiscoveryCache>();
        services.AddSingleton<Services.Integrations.Serato.SeratoCueExportService>(); // cues → Serato Markers2 file tags
        services.AddSingleton<Services.AudioAnalysis.CueDetrService>(); // CUE-DETR (ONNX) cue point detector
        services.AddSingleton<Services.AudioAnalysis.CueDetrCueService>(); // cue regeneration with CUE-DETR (compare / AI only)
        services.AddSingleton<Services.Transitions.IUserTransitionPresetService, Services.Transitions.UserTransitionPresetService>(); // "My presets" in the transition editor (#204 D5)
        services.AddSingleton<Services.Transitions.TransitionPlanService>(); // Mixxx-style sections + DnB transition planning
        services.AddSingleton<PlaylistDiscoveryViewModel>();
    }

    private static void AddOrbitLibrary(IServiceCollection services)
    {

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
        services.AddSingleton<Services.LibraryActions.ILibraryAction, Services.LibraryActions.TogglePathLockAction>(); // LF4 (#346)
        services.AddSingleton<Services.LibraryActions.ILibraryAction, Services.LibraryActions.FileToLibraryAction>(); // LF3 (#345)

        // Download logging and library management
        services.AddSingleton<DownloadLogService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ILifecycleProjectionService, LifecycleProjectionService>();
        services.AddSingleton<ColumnConfigurationService>();
        services.AddSingleton<SmartCrateService>();
        services.AddSingleton<PlaylistExportService>();
        services.AddSingleton<Services.Export.UsbExportOrchestrator>();
        services.AddSingleton<Services.Library.Files.FileRelocationService>(); // LF2: single move/rename path
        services.AddSingleton<Services.Library.Files.PathProtectionService>(); // LF4: path lock/unlock
        services.AddSingleton<Services.Library.Files.ReorganizePreviewService>(); // LF5: reorganise preview

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
        services.AddScoped<SLSKDONET.Services.Copilot.IOrbitCopilot, SLSKDONET.Services.Copilot.OrbitCopilot>();
        services.AddSingleton<SchemaMigratorService>();
        services.AddSingleton<SLSKDONET.Services.Repositories.ITrackRepository, SLSKDONET.Services.Repositories.TrackRepository>();
        services.AddSingleton<SLSKDONET.Services.Repositories.ITransitionRepository, SLSKDONET.Services.Repositories.TransitionRepository>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<IMetadataService, MetadataService>();
    }

    private static void AddOrbitPresentation(IServiceCollection services)
    {

        // Navigation and UI services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<PerformanceTracker>(); // live perf overlay (Ctrl+Shift+P) — page-nav and opted-in ViewModel load timings
        services.AddSingleton<UiStallWatchdog>();
        services.AddSingleton<Services.AudioAnalysis.BeatGridRecomputeService>();
        services.AddSingleton<Services.AudioAnalysis.IBeatGridEditService, Services.AudioAnalysis.BeatGridEditService>();
        services.AddSingleton<Services.AudioAnalysis.CueLayoutRepairService>();
        services.AddSingleton<Services.AudioAnalysis.PhraseBarMigrationService>();
        services.AddSingleton<Services.AudioAnalysis.MusicStartBackfillService>();
        services.AddSingleton<Services.AudioAnalysis.GridConfidenceBackfillService>();
        services.AddSingleton<Services.AudioAnalysis.BpmSplitRepairService>();
        services.AddSingleton<IFileInteractionService, FileInteractionService>();
        services.AddSingleton<INotificationService, NotificationServiceAdapter>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<Services.Diagnostics.FeedbackReportService>(); // top-bar "Send feedback" zip
        services.AddSingleton<Services.Platform.FfmpegInstallerService>();
        services.AddSingleton<SLSKDONET.Services.Update.GitHubUpdateService>(sp =>
            new SLSKDONET.Services.Update.GitHubUpdateService(
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(),
                SLSKDONET.Services.Update.GitHubUpdateService.GetCurrentAppVersion()));
        services.AddSingleton<SLSKDONET.ViewModels.UpdateViewModel>();
        services.AddSingleton<Services.Rekordbox.RekordboxLibraryService>(); // reads Rekordbox's master.db via RekordboxBridge // automatic FFmpeg install (startup + setup wizard)
        services.AddSingleton<Services.Telemetry.FlowBuilderSuggestionTelemetryService>();
        // FlowBuilderView resolves this from DI at construction — without this registration the
        // whole Flow Builder tab silently gets a null DataContext (every control dead).
        services.AddSingleton<ViewModels.FlowBuilderViewModel>();
        services.AddSingleton<DashboardService>();
        // Never registered before — its Start() (500ms heartbeat publishing DashboardSnapshot)
        // was consequently never called by anything, so HomeViewModel's CurrentSnapshot stayed at
        // `new DashboardSnapshot()` (all-default) for the entire app session. The Dashboard's
        // "System Status" tile (Stuck Processes/Failed Retries/CPU Load) and the Audio Quality
        // tile's Gold/Silver/Bronze counts all read from CurrentSnapshot — they always rendered as
        // 0/0/0/OPTIMAL regardless of real state, not just during a startup race. Started further
        // down, once the background init sequence confirms the app is data-safe.
        services.AddSingleton<MissionControlService>();
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
        // Transient (not Singleton): both SidebarViewModel's compact "Mix" tab AND
        // FlowBuilderViewModel's full-option transition editor inject this — sharing one
        // singleton instance meant opening a pair in one silently clobbered whatever the other
        // had loaded. Both consumers are themselves singletons, so DI still resolves this once
        // per consumer at construction and holds it for the app's lifetime; this just gives each
        // consumer its own independent instance instead of one shared, fought-over one.
        services.AddTransient<MixTransitionViewModel>();
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
        // Tray icon + Windows taskbar button (thumbnail toolbar, download progress). Started from
        // the startup barrier once the main window is open; all three constructors are no-throw.
        services.AddSingleton<Services.Shell.ShellStateMonitor>();
        services.AddSingleton<Services.Shell.WindowsTaskbarIntegration>();
        services.AddSingleton<Services.Shell.TrayIconController>();
        // Single cross-platform shell seam (#452): file reveal/open, OS notifications, taskbar,
        // child-process lifetime, and per-user secrets.
        services.AddSingleton<SLSKDONET.Services.Platform.IPlatformShell>(sp =>
        {
            SLSKDONET.Services.Platform.IPlatformShell shell = OperatingSystem.IsWindows()
                ? new SLSKDONET.Services.Platform.WindowsPlatformShell(
                    sp.GetRequiredService<WindowsToastService>(),
                    sp.GetRequiredService<Services.Shell.WindowsTaskbarIntegration>())
                : OperatingSystem.IsMacOS()
                    ? new SLSKDONET.Services.Platform.MacOSPlatformShell()
                    : new SLSKDONET.Services.Platform.LinuxPlatformShell();
            SLSKDONET.Services.Platform.PlatformShell.SetCurrent(shell);
            return shell;
        });
        // Cross-platform external tool locator (FFmpeg, Essentia, fpcalc, curl per OS/RID, #453).
        services.AddSingleton<SLSKDONET.Services.Platform.IToolLocator>(_ =>
        {
            var locator = new SLSKDONET.Services.Platform.DefaultToolLocator();
            SLSKDONET.Services.Platform.ToolLocator.SetCurrent(locator);
            return locator;
        });
        services.AddTransient<UsersViewModel>();
        services.AddTransient<UserProfileViewModel>();
        services.AddTransient<RoomsViewModel>();
        services.AddSingleton<SearchFilterViewModel>(); // [FIX] Added missing registration
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    private static void AddOrbitSetup(IServiceCollection services)
    {

        // First-run setup wizard + quick tour (DOCS/FIRST_RUN_SETUP.md). The wizard's view models
        // only see these interfaces; the implementations reuse the services Settings uses.
        services.AddSingleton<Services.Setup.ISetupStateStore, Services.Setup.ConfigSetupStateStore>();
        services.AddSingleton<Services.Setup.ISetupFolderService, Services.Setup.SetupFolderService>();
        services.AddSingleton<Services.Setup.ISetupSettingsGateway, Services.Setup.ConfigSetupSettingsGateway>();
        services.AddSingleton<Services.Setup.ISoulseekSetupGateway>(sp => new Services.Setup.SoulseekSetupGateway(
            sp.GetRequiredService<AppConfig>(),
            sp.GetRequiredService<ConfigManager>(),
            sp.GetRequiredService<IConnectionLifecycleService>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ISoulseekCredentialService>(),
            sp.GetRequiredService<ILogger<Services.Setup.SoulseekSetupGateway>>()));
        services.AddSingleton<Services.Setup.ILibraryFolderSetupGateway, Services.Setup.LibraryFolderSetupGateway>();
        services.AddSingleton<Services.Setup.ISpotifySetupGateway, ViewModels.Setup.SettingsSpotifySetupGateway>();
        services.AddSingleton<Services.Setup.IAudioSetupGateway, ViewModels.Setup.SettingsAudioSetupGateway>();
        services.AddSingleton<Services.Setup.ISetupEnrichmentGateway, Services.Setup.EnrichmentSetupGateway>();
        services.AddSingleton<Services.Setup.FirstRunCoordinator>();
        services.AddTransient<ViewModels.Setup.SetupWizardViewModel>();
        services.AddSingleton(sp => new ViewModels.Setup.SetupShellViewModel(
            mode => new ViewModels.Setup.SetupWizardViewModel(
                sp.GetRequiredService<Services.Setup.ISetupStateStore>(),
                sp.GetRequiredService<Services.Setup.ISoulseekSetupGateway>(),
                sp.GetRequiredService<Services.Setup.ISetupSettingsGateway>(),
                sp.GetRequiredService<Services.Setup.ILibraryFolderSetupGateway>(),
                sp.GetRequiredService<Services.Setup.ISetupFolderService>(),
                sp.GetRequiredService<IFileInteractionService>(),
                sp.GetRequiredService<Services.Setup.ISpotifySetupGateway>(),
                sp.GetRequiredService<Services.Setup.IAudioSetupGateway>(),
                sp.GetRequiredService<ILogger<ViewModels.Setup.SetupWizardViewModel>>(),
                notifications: sp.GetService<INotificationService>(),
                enrichment: sp.GetService<Services.Setup.ISetupEnrichmentGateway>(),
                activity: sp.GetService<ViewModels.Activity.BackgroundActivityHubViewModel>(),
                mode: mode),
            sp.GetRequiredService<Services.Setup.ISetupStateStore>(),
            sp.GetRequiredService<ILogger<ViewModels.Setup.SetupShellViewModel>>(),
            sp.GetRequiredService<IEventBus>(),
            // The wizard has its own sign-in step; don't stack the login dialog under it.
            onWizardOpened: () => sp.GetRequiredService<ConnectionViewModel>().IsLoginOverlayVisible = false,
            onWizardClosed: () =>
            {
                sp.GetRequiredService<ConnectionViewModel>().ReloadFromConfig();
                sp.GetRequiredService<SettingsViewModel>().RefreshFromConfig();
                sp.GetRequiredService<ViewModels.MetadataSourcesSettingsViewModel>().RefreshFromConfig();
            },
            notifications: sp.GetService<INotificationService>()));
        services.AddSingleton<BulkOperationViewModel>();
        services.AddSingleton<HomeViewModel>();

        // [NEW] Library Scanning
        services.AddSingleton<LibraryFolderScannerService>();
        services.AddSingleton<LibraryFolderWatchService>();
        // The import sheet is the launcher every "add / scan folder" entry point goes through.
        services.AddSingleton<ViewModels.LibraryImportViewModel>();
        services.AddSingleton<ILibraryImportLauncher>(sp => sp.GetRequiredService<ViewModels.LibraryImportViewModel>());

        // Orchestration Services
        services.AddSingleton<SearchOrchestrationService>();
        services.AddSingleton<DownloadOrchestrationService>();
        services.AddSingleton<IBulkOperationCoordinator, BulkOperationCoordinator>(); // Phase 10.5: Refined Workflow
        services.AddSingleton<DownloadDiscoveryService>();

        
        // Phase 10: Tagging & Mobility
        services.AddSingleton<SLSKDONET.Services.IO.SafeWriteService>();
        services.AddSingleton<SLSKDONET.Services.IO.IFileWriteService>(sp => sp.GetRequiredService<SLSKDONET.Services.IO.SafeWriteService>());


        
        // Phase 0: ViewModel Refactoring - Library child ViewModels
        services.AddSingleton<
            SLSKDONET.Services.Library.ILibraryTrackListViewStateStore,
            SLSKDONET.Services.Library.LibraryTrackListViewStateStore>();
        services.AddTransient<ViewModels.Library.ProjectListViewModel>();
        services.AddTransient<ViewModels.Library.TrackListViewModel>();
        services.AddTransient<ViewModels.Library.TrackOperationsViewModel>();
        services.AddTransient<ViewModels.Library.SmartPlaylistViewModel>();

        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<ImportPreviewViewModel>();
        services.AddSingleton<ImportHistoryViewModel>();
        services.AddSingleton<SpotifyImportViewModel>();
        services.AddSingleton<ViewModels.LibrarySourcesViewModel>();
        services.AddSingleton<ViewModels.Library.LibraryHealthViewModel>();
        services.AddSingleton<Services.Import.AutoCleanerService>();

        // Utilities
        services.AddSingleton<SearchQueryNormalizer>();
        
        // Phase 10.5: Native Dependency Health (Reliability)
        services.AddSingleton<NativeDependencyHealthService>();

        // Update check — one throttled GET against the GitHub Releases list (stable + pre-releases),
        // opt-out via AppConfig.EnableUpdateCheck.
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
        services.AddTransient<Views.Avalonia.EditLabPage>();
        services.AddTransient<Views.Avalonia.WorkstationPage>();
        services.AddTransient<Views.Avalonia.UsersPage>();
        services.AddTransient<Views.Avalonia.CueForgePage>();
        services.AddTransient<Views.Avalonia.FlowBuilderPage>();
        services.AddSingleton<ViewModels.Sets.SetsViewModel>();
        services.AddTransient<Views.Avalonia.SetsPage>();
        services.AddSingleton<Services.ICuePointService, Services.CuePointService>();
        services.AddSingleton<Services.Audio.StemPreferenceService>();
        services.AddSingleton<Services.Audio.MixdownService>();
        services.AddSingleton<Services.WorkstationSessionService>();
        services.AddSingleton<Services.OrbSessionBundleService>();
        services.AddSingleton<Services.IUndoService, Services.UndoService>();
    }

    private static void AddOrbitAnalysis(IServiceCollection services)
    {


        // ── Rekordbox PSSI phrase analysis (optional — reads Rekordbox's own local analysis cache) ──
        services.AddSingleton<Services.Rekordbox.IRekordboxPssiService, Services.Rekordbox.RekordboxPssiService>();

        // ── Auto-cue / phrase detection pipeline ──────────────────────────
        services.AddSingleton<Services.AudioAnalysis.CuePointDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.DnBTransientDetectionService>();
        services.AddSingleton<Services.DnBCueNamingService>();
        services.AddSingleton<Engine.Analysis.BreakbeatAnalysisStrategy>();
        services.AddSingleton<Engine.Analysis.FourOnTheFloorAnalysisStrategy>();
        services.AddSingleton<Engine.Analysis.BassMusicAnalysisStrategy>();
        services.AddSingleton<Services.CamelotKeyDisplayService>();
        services.AddSingleton<Services.PhraseAlignmentService>();
        services.AddSingleton<Services.IPhraseAlignmentService>(sp =>
            sp.GetRequiredService<Services.PhraseAlignmentService>());
        services.AddSingleton<Services.AnalyzeTrackStructureJob>();

        services.AddSingleton<SLSKDONET.ViewModels.Workstation.WorkstationViewModel>();
        services.AddSingleton<SLSKDONET.ViewModels.Workstation.CueEditorViewModel>();
        // Links every cue editor session (Cue Forge, Flow Builder / Mix decks) and holds the shared
        // drop cue template. No I/O in its constructor, so it can't poison the container.
        services.AddSingleton<SLSKDONET.ViewModels.CueSessionRegistry>();
        services.AddSingleton<SLSKDONET.ViewModels.CueForgeViewModel>();
        services.AddSingleton<SLSKDONET.Engine.Analysis.AnalysisPipeline>(sp =>
            new SLSKDONET.Engine.Analysis.AnalysisPipeline(
                sp.GetRequiredService<SLSKDONET.Services.AudioAnalysis.AudioIngestionPipeline>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SLSKDONET.Engine.Analysis.AnalysisPipeline>>()));
        services.AddSingleton<SLSKDONET.Engine.Cueing.CueGenerationService>(sp =>
        {
            var generator = ActivatorUtilities.CreateInstance<SLSKDONET.Engine.Cueing.CueGenerationService>(sp);
            // Build-in cues follow the cue template chosen in the editors (read at generation time).
            var configManager = sp.GetRequiredService<ConfigManager>();
            generator.CountdownBars = (genre, bpm) =>
            {
                var config = configManager.GetCurrent();
                var bars = SLSKDONET.Engine.Cueing.DropCountdownCues.ResolveBars(config.DropCountdownMode, genre, bpm, config.CustomCountdownBars);
                return bars.Count > 0 ? bars : new[] { 16, 8 }; // "Off" only affects manual editing
            };
            generator.CountdownsAsMemoryCues = () => configManager.GetCurrent().CountdownsAsMemoryCues;
            return generator;
        });
        services.AddSingleton<Services.AnalysisQueueService>();
        services.AddSingleton<Services.AudioAnalysis.LoudnessBackfillService>(sp =>
            new Services.AudioAnalysis.LoudnessBackfillService(
                sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                sp.GetRequiredService<Services.AudioAnalysis.AudioIngestionPipeline>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.AudioAnalysis.LoudnessBackfillService>>(),
                sp.GetService<Services.AnalysisQueueService>()));
        services.AddSingleton<Services.AudioAnalysis.TimeTillDropBackfillService>();
        services.AddSingleton<ViewModels.LoudnessBackfillViewModel>();

        // ── Library metadata enrichment (Services/Enrichment, DOCS/METADATA_ENRICHMENT.md) ──
        services.AddHttpClient("enrichment", c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<Services.Enrichment.EnrichmentStore>();
        services.AddSingleton<Services.Enrichment.IMetadataLookupCache>(sp => sp.GetRequiredService<Services.Enrichment.EnrichmentStore>());

        // ── Library Maintenance (#211): reversible tag writes + run undo ──
        services.AddSingleton<Services.Library.Maintenance.TrackMetadataWriter>();
        services.AddSingleton<Services.Library.Maintenance.TagChangeService>();
        services.AddSingleton<Services.Library.Maintenance.LibraryIdentityIndexLoader>();
        services.AddSingleton<Services.Library.Maintenance.StyleBackfillService>();
        // GA10 (#255): re-fuses genre from stored model outputs (no audio); the Jamendo head is the shared one.
        services.AddSingleton<Services.Library.Maintenance.GenreRefreshService>();
        // GA11 (#256): personal genre head, retrained in the background from genre corrections.
        services.AddSingleton<Services.Genre.PersonalGenreHeadService>();
        services.AddSingleton<ViewModels.Settings.PersonalGenreSettingsViewModel>();
        services.AddSingleton<Services.Library.Maintenance.StylePlaylistApplier>();
        services.AddSingleton<Services.Library.Maintenance.UseAsGenreService>();
        services.AddSingleton<Services.Library.Maintenance.MaintenanceUndoService>();
        // A fresh wizard per open: state must not leak between maintenance runs.
        services.AddTransient<ViewModels.Maintenance.LibraryMaintenanceViewModel>();
        services.AddSingleton(sp => new ViewModels.Maintenance.MaintenanceShellViewModel(
            () => sp.GetRequiredService<ViewModels.Maintenance.LibraryMaintenanceViewModel>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ViewModels.Maintenance.MaintenanceShellViewModel>>(),
            sp.GetRequiredService<IEventBus>(),
            duplicatesFactory: () => sp.GetRequiredService<ViewModels.Maintenance.ExactDuplicatesViewModel>()));
        services.AddSingleton<Services.Library.AudioContentHasher>();
        services.AddSingleton<Services.Library.Maintenance.LibraryContentHashBackfillService>();
        // Exact-duplicate review (#238): merge via DuplicateTrackCleanupService, opt-in Recycle Bin.
        services.AddSingleton<Services.Library.Maintenance.IFileRecycler, Services.Library.Maintenance.ShellFileRecycler>();
        services.AddSingleton<Services.Library.Maintenance.ExactDuplicateResolver>();
        services.AddTransient<ViewModels.Maintenance.ExactDuplicatesViewModel>();
        services.AddSingleton(sp => new Services.Enrichment.MetadataCredentialStore(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Enrichment.MetadataCredentialStore>>()));
        static System.Net.Http.HttpClient EnrichmentHttp(IServiceProvider sp) =>
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("enrichment");
        services.AddSingleton<Services.Enrichment.IMetadataProvider, Services.Enrichment.Providers.LocalTagsProvider>();
        services.AddSingleton<Services.Enrichment.IMetadataProvider>(sp => new Services.Enrichment.Providers.DeezerProvider(
            EnrichmentHttp(sp), sp.GetRequiredService<Services.Enrichment.IMetadataLookupCache>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Enrichment.Providers.DeezerProvider>>()));
        services.AddSingleton<Services.Enrichment.IMetadataProvider>(sp => new Services.Enrichment.Providers.MusicBrainzProvider(
            EnrichmentHttp(sp), sp.GetRequiredService<Services.Enrichment.IMetadataLookupCache>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Enrichment.Providers.MusicBrainzProvider>>()));
        services.AddSingleton<Services.Enrichment.IMetadataProvider>(sp => new Services.Enrichment.Providers.DiscogsProvider(
            EnrichmentHttp(sp), sp.GetRequiredService<Services.Enrichment.IMetadataLookupCache>(),
            sp.GetRequiredService<Services.Enrichment.MetadataCredentialStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Enrichment.Providers.DiscogsProvider>>()));
        services.AddSingleton<Services.Enrichment.IMetadataProvider>(sp => new Services.Enrichment.Providers.AcoustIdProvider(
            EnrichmentHttp(sp), sp.GetRequiredService<Services.Enrichment.IMetadataLookupCache>(),
            sp.GetRequiredService<Services.Enrichment.MetadataCredentialStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Enrichment.Providers.AcoustIdProvider>>()));
        services.AddSingleton<Services.Enrichment.IMetadataProvider>(sp => new Services.Enrichment.Providers.SpotifyProvider(
            sp.GetRequiredService<SpotifyEnrichmentService>(), sp.GetRequiredService<SpotifyAuthService>()));
        services.AddSingleton<Services.Enrichment.IMetadataProvider>(sp => new Services.Enrichment.Providers.BeatportProvider(
            sp.GetRequiredService<Services.Discovery.BeatportCatalogClient>()));
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<AppConfig>();
            return new Services.Enrichment.MetadataEnrichmentOrchestrator(
                sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                sp.GetRequiredService<Services.Enrichment.EnrichmentStore>(),
                sp.GetServices<Services.Enrichment.IMetadataProvider>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Enrichment.MetadataEnrichmentOrchestrator>>(),
                () => SLSKDONET.Services.Enrichment.EnrichmentSettings.From(config),
                sp.GetService<ArtworkCacheService>(),
                () => sp.GetService<Services.AnalysisQueueService>()?.PendingCount > 0,
                () => sp.GetService<IAudioPlayerService>()?.IsPlaying == true,
                SLSKDONET.Services.Enrichment.MetadataEnrichmentOrchestrator.DefaultWorkers,
                TimeSpan.FromSeconds(3),
                startupResumeDelay: TimeSpan.FromSeconds(45),
                isImportRunning: () => sp.GetService<LibraryFolderScannerService>()?.IsAnyImportRunning == true);
        });
        services.AddSingleton<ViewModels.EnrichmentViewModel>();
        services.AddSingleton<ViewModels.MetadataSourcesSettingsViewModel>();
        // The one background-activity chip/flyout (top bar + setup wizard header).
        services.AddSingleton(sp => new ViewModels.Activity.BackgroundActivityHubViewModel(
            sp.GetRequiredService<IEventBus>(),
            sp.GetService<ViewModels.LibraryImportViewModel>(),
            new ViewModels.Activity.OrchestratorEnrichmentActivityControl(sp.GetRequiredService<Services.Enrichment.MetadataEnrichmentOrchestrator>()),
            page => sp.GetRequiredService<INavigationService>().NavigateTo(page),
            sp.GetService<ILogger<ViewModels.Activity.BackgroundActivityHubViewModel>>()));

        // ── Task 1.5: Beatgrid Detection ──────────────────────────────────
        services.AddSingleton<Services.AudioAnalysis.BeatgridDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.BpmDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.KeyDetectionService>();
        services.AddSingleton<Services.AudioAnalysis.InProcessTempoKeyEngine>();
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
        services.AddSingleton<Services.Similarity.IJamendoGenreHead>(sp => sp.GetRequiredService<Services.Similarity.EffnetClassifierHeadService>());
        services.AddSingleton<Services.Similarity.DiscogsStyleLabels>();
        services.AddSingleton<Services.AudioAnalysis.AudioAnalysisService>();
        services.AddSingleton<Services.IAudioAnalysisService>(sp =>
            sp.GetRequiredService<Services.AudioAnalysis.AudioAnalysisService>());

        // Per-playlist analysis completeness ("what exactly is missing") for the Analysis page.
        services.AddSingleton<Services.AudioAnalysis.AnalysisCoverageService>();

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
    }

    private static void AddOrbitAudioAndWorkers(IServiceCollection services)
    {

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

        // ── Vocal activity (#285): on-demand vocal span/density from the Demucs vocal stem ──
        services.AddSingleton<Services.AudioAnalysis.IVocalActivityService, Services.AudioAnalysis.VocalActivityService>();

        // ── Edit Lab: stems + track editing (replaces the old, unreachable Stems page) ──
        // None of these do I/O in their constructors, so they can't poison the container.
        services.AddSingleton<SLSKDONET.Services.EditLab.EditProjectStore>(sp =>
            new SLSKDONET.Services.EditLab.EditProjectStore(sp.GetService<Microsoft.Extensions.Logging.ILogger<SLSKDONET.Services.EditLab.EditProjectStore>>()));
        services.AddSingleton<SLSKDONET.Services.EditLab.EditLabSourceService>(sp =>
            new SLSKDONET.Services.EditLab.EditLabSourceService(
                sp.GetService<SLSKDONET.Services.ILibraryService>(),
                sp.GetService<SLSKDONET.Services.DatabaseService>(),
                sp.GetService<SLSKDONET.Services.Audio.Separation.CachedStemSeparator>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<SLSKDONET.Services.EditLab.EditLabSourceService>>()));
        services.AddSingleton<SLSKDONET.Services.EditLab.EditPreviewEngine>(sp =>
        {
            var cfg = sp.GetService<AppConfig>();
            return new SLSKDONET.Services.EditLab.EditPreviewEngine(() =>
            {
                try
                {
                    return SLSKDONET.Services.Audio.AudioOutputProvider.CreatePreviewDevice(cfg?.AudioOutputMode, cfg?.AudioOutputDeviceName);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    Serilog.Log.Warning(ex, "Edit Lab preview: couldn't open {Device}; using the Windows default", cfg?.AudioOutputDeviceName ?? "(default)");
                    return new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                }
            });
        });
        services.AddSingleton<SLSKDONET.Services.EditLab.EditLabRenderService>(sp =>
            new SLSKDONET.Services.EditLab.EditLabRenderService(
                sp.GetRequiredService<SLSKDONET.Services.EditLab.EditLabSourceService>(),
                sp.GetService<AppConfig>(),
                sp.GetService<SLSKDONET.Services.IEventBus>(),
                sp.GetService<SLSKDONET.Services.ICuePointService>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<SLSKDONET.Services.EditLab.EditLabRenderService>>()));
        services.AddSingleton<ViewModels.EditLab.IEditLabPlayerGate>(sp =>
            new ViewModels.EditLab.PlayerViewModelEditLabGate(sp.GetRequiredService<PlayerViewModel>()));
        services.AddSingleton<ViewModels.EditLab.EditLabViewModel>(sp =>
            new ViewModels.EditLab.EditLabViewModel(
                sp.GetRequiredService<SLSKDONET.Services.EditLab.EditProjectStore>(),
                sp.GetRequiredService<SLSKDONET.Services.EditLab.EditLabSourceService>(),
                sp.GetRequiredService<SLSKDONET.Services.EditLab.EditPreviewEngine>(),
                sp.GetService<ViewModels.EditLab.IEditLabPlayerGate>(),
                sp.GetService<Views.INotificationService>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<ViewModels.EditLab.EditLabViewModel>>(),
                renderService: sp.GetRequiredService<SLSKDONET.Services.EditLab.EditLabRenderService>()));

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

        // ── Phase 2: Knowledge Lifecycle agent swarm ─────────────────────
        // Gated by AppConfig.EnableKnowledgeAgents (default on; Suggest next reads the candidates). PerformanceAnalystAgent and
        // ExportAgent are not registered: nothing publishes PerformanceSessionRecordedEvent or
        // SetExportRequestedEvent yet (rebuild plan D7; they return with P5.1 / P6.1).
        services.AddSingleton<Services.KnowledgeLifecycle.IKnowledgeAgent, Services.KnowledgeLifecycle.TransitionAgent>();
        services.AddHostedService<Services.KnowledgeLifecycle.KnowledgeSwarmHost>();

        // ── Sets (rebuild plan Phase 2): single write path for SetProjects ─
        services.AddSingleton<Services.Sets.ISetProjectService, Services.Sets.SetProjectService>();
        services.AddSingleton<Services.Sets.SetTransitionRepository>();
        services.AddSingleton<Services.Sets.SetExportService>(); // Sets → Export (#206)
        services.AddSingleton<Services.KnowledgeLifecycle.RekordboxHistoryImportService>(); // Rekordbox play history → live evidence (#207)
        services.AddSingleton<Services.Sets.ITransitionHistoryService, Services.Sets.TransitionHistoryService>();
        services.AddSingleton<Services.KnowledgeLifecycle.ITransitionRatingService, Services.KnowledgeLifecycle.TransitionRatingService>();
    }
}
