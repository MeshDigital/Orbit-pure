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
        // Invariant numbers in every NumericUpDown; a typed decimal comma is entered as '.'.
        Views.Avalonia.NumericInputLocale.Install();
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
            // Same for the loudness backfill: it listens for the inspector's single-track "Measure".
            _ = Services.GetRequiredService<Services.AudioAnalysis.LoudnessBackfillService>();
            // And library metadata enrichment: it listens for imports/downloads and resumes its queue.
            _ = Services.GetRequiredService<Services.Enrichment.MetadataEnrichmentOrchestrator>();

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

                        // First-run / upgrade onboarding: decide before the UI exists. A fresh install
                        // gets the full wizard; an install on an older wizard version gets "what's new".
                        var showSetupWizard = false;
                        var setupMode = ViewModels.Setup.SetupWizardMode.FirstRun;
                        try
                        {
                            var firstRun = await Services.GetRequiredService<Services.Setup.FirstRunCoordinator>().EvaluateAsync();
                            showSetupWizard = firstRun.ShouldShowWizard;
                            setupMode = firstRun.IsUpgrade
                                ? ViewModels.Setup.SetupWizardMode.Upgrade
                                : ViewModels.Setup.SetupWizardMode.FirstRun;
                        }
                        catch (Exception setupEx)
                        {
                            Serilog.Log.Error(setupEx, "First-run check failed; the setup wizard stays closed (Settings → Advanced → Setup opens it)");
                        }
                        
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => splashScreen.UpdateStatus("Starting UI..."));
                        await Task.Delay(50);

                        // We resolve MainViewModel on the UI thread because it creates UI-bound
                        // components (like TreeDataGridSource). This single DI resolve constructs
                        // ~15 ViewModels' worth of constructor work (Player/Library/Search/
                        // Settings/Home/Timeline/Sidebar/FlowBuilder/...), and constructing the
                        // MainWindow's whole visual tree is its own non-trivial chunk of work too.
                        // Previously both happened inside one atomic Dispatcher.InvokeAsync call —
                        // since a dispatched callback runs to completion before the UI thread gets
                        // to process anything else (paint included), the splash screen's status
                        // text and progress bar sat frozen on "Starting UI..." for the whole
                        // duration, which read as the app having hung. Splitting this into two
                        // separate InvokeAsync calls with a real status update and a yield between
                        // them lets the dispatcher actually pump a paint pass in between, so the
                        // splash visibly keeps moving instead of appearing stuck.
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            mainVm = Services.GetRequiredService<MainViewModel>();
                            mainVm.StatusText = "Finalizing UI...";
                            mainVm.IsInitializing = true;
                            splashScreen.UpdateStatus("Loading interface...");
                        });

                        await Task.Delay(1);

                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            var mainWindow = new Views.Avalonia.MainWindow
                            {
                                DataContext = mainVm
                            };

                            desktop.MainWindow = mainWindow;
                            mainWindow.Show();
                            splashScreen.Close();

                            // GlobalHotkeyService was constructed as part of mainVm's DI graph
                            // above, while desktop.MainWindow was still the splash screen — its
                            // constructor-time attach bound to that (about-to-close) window.
                            // Re-attach now that the real window is actually showing, or every DJ
                            // keyboard shortcut (play/pause, cues, loops, beat jump, ...) would
                            // silently never fire for the rest of the session.
                            Services.GetRequiredService<GlobalHotkeyService>().AttachToCurrentMainWindow();

                            // Real OS-level notifications via the cross-platform shell seam (#452).
                            var toastHandle = mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                            Services.GetRequiredService<Services.Platform.IPlatformShell>().InitializeNotifications(toastHandle);

                            Services.GetRequiredService<UiStallWatchdog>().Start();

                            if (showSetupWizard)
                                mainVm?.Setup?.OpenWizard(setupMode);

                            // Tray icon (live tooltip + menu) and the Windows taskbar button
                            // (thumbnail Previous/Play/Next + download progress).
                            try
                            {
                                Services.GetRequiredService<Services.Shell.TrayIconController>().Start(mainWindow);
                            }
                            catch (Exception shellEx)
                            {
                                Serilog.Log.Warning(shellEx, "Tray/taskbar integration failed to start (non-critical)");
                            }
                        });

                        // --- THE BARRIER: WE ARE NOW DATA-SAFE ---
                        // All subsequent background services that hit the DB can now start.
                        
                        // Initialize and Start DownloadManager Orchestrator
                        var downloadManager = Services.GetRequiredService<DownloadManager>();
                        _ = downloadManager.StartAsync(); // Auto-start engine on launch

                        // One-time re-fit of stored beat grids (quantised BPM fix); no-op once done.
                        Services.GetRequiredService<Services.AudioAnalysis.BeatGridRecomputeService>().StartIfPending();

                        // One-time repair of stored auto cues: Drop 2 clamped into the outro, and
                        // Drop 2 countdowns renamed to [IN2 …]. Auto cues only; no-op once done.
                        Services.GetRequiredService<Services.AudioAnalysis.CueLayoutRepairService>().StartIfPending();

                        // One-time conversion of phrase data to bar-indexed boundaries; no-op once done.
                        Services.GetRequiredService<Services.AudioAnalysis.PhraseBarMigrationService>().StartIfPending();

                        // One-time estimation of music start for tracks analysed before the detector; no-op once done.
                        Services.GetRequiredService<Services.AudioAnalysis.MusicStartBackfillService>().StartIfPending();

                        // One-time repair of pre-#220 BPM fixes that never reached the beat grid; no-op once done.
                        Services.GetRequiredService<Services.AudioAnalysis.BpmSplitRepairService>().StartIfPending();

                        // One-time grid confidence + variable-tempo flag for already-analysed grids
                        // (stored data only; never changes a grid); no-op once done.
                        Services.GetRequiredService<Services.AudioAnalysis.GridConfidenceBackfillService>().StartIfPending();

                        // Fill first-drop/intro timing from stored energy curves. No audio decode,
                        // cue/grid changes or Rekordbox writes; no-op after the first completed pass.
                        Services.GetRequiredService<Services.AudioAnalysis.TimeTillDropBackfillService>().StartIfPending();

                        // Personal genre head (GA11 #256): retrains only when genre labels changed; startup check is delayed.
                        Services.GetRequiredService<Services.Genre.PersonalGenreHeadService>().Start();

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
                        // Also installs FFmpeg into LocalAppData when it's missing (Windows x64).
                        _ = Services.GetRequiredService<Services.Platform.FfmpegInstallerService>().EnsureInstalledAsync();
                        // No .NET runtime auto-install here: this build is published self-contained
                        // (Tools/release-alpha.ps1 uses --self-contained true), so ORBIT bundles its
                        // own runtime. Auto-installing a machine-wide runtime used to trigger a UAC
                        // "install .NET" prompt on every launch for testers with no standalone
                        // runtime installed, so it was removed.

                        // SpotifyAuthService.IsAuthenticated defaults to false and only ever gets
                        // set true inside VerifyConnectionAsync()/RefreshAccessTokenAsync() — which
                        // nothing at startup was calling; the only caller was Settings' "Test
                        // Connection" button. Result: a genuinely-connected user (valid stored
                        // refresh token) still read as IsAuthenticated=false for the whole session
                        // until they happened to open Settings and click Test — so every Spotify
                        // call elsewhere (Library's playlist Sync included) silently fell back to
                        // Client Credentials auth, which Spotify 404s for private/collaborative
                        // playlists (i.e. almost anyone's own playlists) as if they don't exist.
                        _ = Services.GetRequiredService<SpotifyAuthService>().VerifyConnectionAsync();

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

                        // Library projects are loaded by LibraryPage.OnLoaded on first navigation.
                        // Startup normally opens Home; eagerly loading every playlist, selecting the
                        // first one and preparing its tracks here duplicated that lazy path and caused
                        // a large first-window UI stall for work the user had not requested.

                        // Mission Control's 500ms heartbeat (CPU load, zombie-process count,
                        // dead-letter/failed-retry count, adaptive library-health audit) — started
                        // here, after crash recovery and library sync have both already completed
                        // above, so its very first tick reads settled state rather than racing
                        // either of them. See the DashboardService/AddSingleton<MissionControlService>
                        // registration above for why this was silently never running before.
                        try
                        {
                            Services.GetRequiredService<MissionControlService>().Start();
                        }
                        catch (Exception missionControlEx)
                        {
                            Serilog.Log.Warning(missionControlEx, "Mission Control Service failed to start (non-critical)");
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
                            // Off the UI thread: it walks the download folder and touches the DB.
                            _ = Task.Run(RunMaintenanceTasksAsync);
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

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        ConfigureSharedServices(services);
        return services.BuildServiceProvider();
    }



}
