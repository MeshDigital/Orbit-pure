using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET;

/// <summary>
/// Desktop startup &amp; shutdown bootstrapper extracted from <c>App.axaml.cs</c> (#298):
/// config migration, splash screen, DB initialization barrier, UI window bring-up,
/// post-DB background services, and clean shutdown.
/// </summary>
public partial class App
{
    private static void ActivateEarlyEventListeners(IServiceProvider services)
    {
        // Eagerly activate background queue listeners that subscribe to the event bus.
        _ = services.GetRequiredService<Services.AnalysisQueueService>();
        _ = services.GetRequiredService<Services.AudioAnalysis.LoudnessBackfillService>();
        _ = services.GetRequiredService<Services.Enrichment.MetadataEnrichmentOrchestrator>();
        _ = services.GetRequiredService<Services.NetworkActivityMonitor>();
    }

    private async Task BootstrapDesktopAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Services is null) return;

        try
        {
            RunConfigMigration(Services);

            var configDispatcher = Services.GetRequiredService<ConfigManager>();
            var config = configDispatcher.GetCurrent() ?? new AppConfig();
            string profile = config.RankingProfile ?? "Balanced";
            ResultSorter.SetConfig(config);
            Serilog.Log.Information("Loaded ranking strategy: {Profile}", profile);

            var splashScreen = new SLSKDONET.Views.Avalonia.SplashScreen();
            desktop.MainWindow = splashScreen;
            splashScreen.Show();
            splashScreen.UpdateStatus("Initializing Database...");

            // Yield to let the UI thread render the splash screen
            await Task.Delay(50);

            // Initialize Database BEFORE creating the UI to prevent SQLite locks
            _ = Task.Run(() => RunBackgroundBootstrapAsync(desktop, splashScreen));
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "Startup failed during framework initialization");
            throw;
        }
    }

    private static void RunConfigMigration(IServiceProvider services)
    {
        try
        {
            var configManager = services.GetRequiredService<ConfigManager>();
            var migrationConfig = configManager.Load();
            var migrationService = services.GetRequiredService<ConfigMigrationService>();

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
    }

    private async Task RunBackgroundBootstrapAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Views.Avalonia.SplashScreen splashScreen)
    {
        if (Services is null) return;

        MainViewModel? mainVm = null;
        using var initCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            var databaseService = Services.GetRequiredService<DatabaseService>();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => splashScreen.UpdateStatus("Optimizing Database..."));
            await databaseService.InitAsync().WaitAsync(initCts.Token);
            Serilog.Log.Information("✅ Database initialization completed successfully");

            var (showSetupWizard, setupMode) = await EvaluateFirstRunAsync(Services);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => splashScreen.UpdateStatus("Starting UI..."));
            await Task.Delay(50);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainVm = Services.GetRequiredService<MainViewModel>();
                mainVm.StatusText = "Finalizing UI...";
                mainVm.IsInitializing = true;
                splashScreen.UpdateStatus("Loading interface...");
            });

            await Task.Delay(1);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                ShowMainWindow(desktop, splashScreen, mainVm, showSetupWizard, setupMode));

            // --- THE BARRIER: WE ARE NOW DATA-SAFE ---
            await StartPostDatabaseServicesAsync(Services);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (mainVm != null)
                {
                    mainVm.IsInitializing = false;
                    mainVm.StatusText = "Ready";
                }
                Serilog.Log.Information("Background initialization completed");
                _ = Task.Run(RunMaintenanceTasksAsync);
            });
        }
        catch (OperationCanceledException)
        {
            Serilog.Log.Fatal("CRITICAL: Application initialization TIMED OUT after 2 minutes.");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                splashScreen.UpdateStatus("Initialization Timeout. Please restart."));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Background initialization failed");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                splashScreen.UpdateStatus($"Error: {ex.Message}"));
        }
    }

    private static async Task<(bool ShowWizard, ViewModels.Setup.SetupWizardMode Mode)> EvaluateFirstRunAsync(IServiceProvider services)
    {
        try
        {
            var firstRun = await services.GetRequiredService<Services.Setup.FirstRunCoordinator>().EvaluateAsync();
            return (
                firstRun.ShouldShowWizard,
                firstRun.IsUpgrade ? ViewModels.Setup.SetupWizardMode.Upgrade : ViewModels.Setup.SetupWizardMode.FirstRun);
        }
        catch (Exception setupEx)
        {
            Serilog.Log.Error(setupEx, "First-run check failed; the setup wizard stays closed (Settings → Advanced → Setup opens it)");
            return (false, ViewModels.Setup.SetupWizardMode.FirstRun);
        }
    }

    private void ShowMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        Views.Avalonia.SplashScreen splashScreen,
        MainViewModel? mainVm,
        bool showSetupWizard,
        ViewModels.Setup.SetupWizardMode setupMode)
    {
        if (Services is null) return;

        var mainWindow = new Views.Avalonia.MainWindow { DataContext = mainVm };
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        SLSKDONET.Services.Diagnostics.StartupTelemetry.MarkOnce("Main window shown");
        splashScreen.Close();

        Services.GetRequiredService<GlobalHotkeyService>().AttachToCurrentMainWindow();

        var toastHandle = mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Services.GetRequiredService<Services.Platform.IPlatformShell>().InitializeNotifications(toastHandle);

        Services.GetRequiredService<UiStallWatchdog>().Start();

        if (showSetupWizard)
            mainVm?.Setup?.OpenWizard(setupMode);

        try
        {
            Services.GetRequiredService<Services.Shell.TrayIconController>().Start(mainWindow);
        }
        catch (Exception shellEx)
        {
            Serilog.Log.Warning(shellEx, "Tray/taskbar integration failed to start (non-critical)");
        }
    }

    private static async Task StartPostDatabaseServicesAsync(IServiceProvider services)
    {
        var downloadManager = services.GetRequiredService<DownloadManager>();
        _ = downloadManager.StartAsync();

        // One-time analysis/migration backfill services (each is a no-op once completed).
        services.GetRequiredService<Services.AudioAnalysis.BeatGridRecomputeService>().StartIfPending();
        services.GetRequiredService<Services.AudioAnalysis.CueLayoutRepairService>().StartIfPending();
        services.GetRequiredService<Services.AudioAnalysis.PhraseBarMigrationService>().StartIfPending();
        services.GetRequiredService<Services.AudioAnalysis.MusicStartBackfillService>().StartIfPending();
        services.GetRequiredService<Services.AudioAnalysis.BpmSplitRepairService>().StartIfPending();
        services.GetRequiredService<Services.AudioAnalysis.GridConfidenceBackfillService>().StartIfPending();
        services.GetRequiredService<Services.AudioAnalysis.TimeTillDropBackfillService>().StartIfPending();
        services.GetRequiredService<Services.Genre.PersonalGenreHeadService>().Start();

        // Post-download & post-analysis event-bus listeners
        _ = services.GetRequiredService<PostDownloadSpectralScanService>();
        _ = services.GetRequiredService<PostDownloadDurationCaptureService>();
        _ = services.GetRequiredService<Services.Library.Files.PostAnalysisRefileService>();

        // Non-blocking startup checks
        _ = services.GetRequiredService<Services.Platform.FfmpegInstallerService>().EnsureInstalledAsync();
        _ = services.GetRequiredService<SpotifyAuthService>().VerifyConnectionAsync();
        _ = services.GetRequiredService<IUpdateCheckService>().CheckForUpdatesAsync();

        // Eager-resolve chat/notification listeners
        _ = services.GetRequiredService<ChatService>();
        _ = services.GetRequiredService<RoomChatService>();
        _ = services.GetRequiredService<NotificationCenterService>();
        _ = services.GetRequiredService<PeerVerificationChallengeService>();

        try
        {
            foreach (var hostedService in services.GetServices<IHostedService>())
                _ = hostedService.StartAsync(CancellationToken.None);
            Serilog.Log.Information("✅ Started hosted background services");
        }
        catch (Exception hostedEx)
        {
            Serilog.Log.Error(hostedEx, "Failed to start hosted background services");
        }

        try
        {
            var crashJournal = services.GetRequiredService<CrashRecoveryJournal>();
            await crashJournal.InitAsync();
            Serilog.Log.Information("✅ Crash Recovery Journal initialized");
            await services.GetRequiredService<CrashRecoveryService>().RecoverAsync();
        }
        catch (Exception journalEx)
        {
            Serilog.Log.Warning(journalEx, "Crash recovery failed (non-critical)");
        }

        try
        {
            await services.GetRequiredService<ILibraryService>().SyncLibraryEntriesFromTracksAsync();
            Serilog.Log.Information("✅ Start-up Library synchronization completed");
        }
        catch (Exception syncEx)
        {
            Serilog.Log.Error(syncEx, "Start-up Library sync failed");
        }

        try
        {
            await services.GetRequiredService<LibraryFolderWatchService>().StartAsync();
        }
        catch (Exception watchEx)
        {
            Serilog.Log.Warning(watchEx, "Library folder watch service failed to start (non-critical)");
        }

        try
        {
            services.GetRequiredService<MissionControlService>().Start();
        }
        catch (Exception missionControlEx)
        {
            Serilog.Log.Warning(missionControlEx, "Mission Control Service failed to start (non-critical)");
        }
    }

    private async Task ShutdownServicesAsync()
    {
        Serilog.Log.Information("Application shutdown initiated - cleaning up services...");
        try
        {
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

            try
            {
                var databaseService = Services?.GetService<DatabaseService>();
                if (databaseService != null)
                {
                    Serilog.Log.Information("Closing database connections...");
                    await databaseService.CloseConnectionsAsync();
                }

                var crashJournal = Services?.GetService<CrashRecoveryJournal>();
                if (crashJournal != null)
                {
                    Serilog.Log.Information("Closing crash recovery journal...");
                    await crashJournal.DisposeAsync();
                }

                Services?.GetService<Services.NetworkActivityMonitor>()?.Dispose();

                try
                {
                    if (Services != null)
                    {
                        foreach (var hostedService in Services.GetServices<IHostedService>())
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
            Serilog.Log.CloseAndFlush();
        }
    }
}
