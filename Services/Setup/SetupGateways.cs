using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.Services.Setup;

/// <summary>[Setup] Completed / Version / TourCompleted, persisted through the shared ConfigManager.</summary>
public sealed class ConfigSetupStateStore : ISetupStateStore
{
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ILogger<ConfigSetupStateStore> _logger;

    public ConfigSetupStateStore(AppConfig config, ConfigManager configManager, ILogger<ConfigSetupStateStore> logger)
    {
        _config = config;
        _configManager = configManager;
        _logger = logger;
    }

    public bool IsSetupCompleted => _config.SetupCompleted;
    public bool IsTourCompleted => _config.TourCompleted;

    public void MarkSetupCompleted()
    {
        _config.SetupCompleted = true;
        _config.SetupVersion = FirstRunDetector.CurrentSetupVersion;
        Save("setup completed");
    }

    public void MarkTourCompleted()
    {
        _config.TourCompleted = true;
        Save("tour completed");
    }

    private void Save(string what)
    {
        try
        {
            _configManager.Save(_config);
            _logger.LogInformation("Setup: {What} saved to {Path}", what, _configManager.ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Setup: failed to save '{What}' to config.ini", what);
            throw;
        }
    }
}

/// <summary>
/// Writes the wizard's folder and audio choices to the same <see cref="AppConfig"/> the Settings
/// page binds to, then saves config.ini once per confirmed step.
/// </summary>
public sealed class ConfigSetupSettingsGateway : ISetupSettingsGateway
{
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ILogger<ConfigSetupSettingsGateway> _logger;
    private readonly ISoulseekAdapter? _soulseek;
    private readonly IAudioPlayerService? _player;

    public ConfigSetupSettingsGateway(
        AppConfig config,
        ConfigManager configManager,
        ILogger<ConfigSetupSettingsGateway> logger,
        ISoulseekAdapter? soulseek = null,
        IAudioPlayerService? player = null)
    {
        _config = config;
        _configManager = configManager;
        _logger = logger;
        _soulseek = soulseek;
        _player = player;
    }

    public string? ChosenDownloadDirectory =>
        string.IsNullOrWhiteSpace(_config.DownloadDirectory) || FirstRunDetector.SamePath(_config.DownloadDirectory, AppConfig.FallbackDownloadDirectory)
            ? null
            : _config.DownloadDirectory;

    public SetupFolderSettings ReadFolders() => new(
        _config.DownloadDirectory ?? AppConfig.FallbackDownloadDirectory,
        _config.EnableLibrarySharing,
        string.IsNullOrWhiteSpace(_config.SharedFolderPath) ? null : _config.SharedFolderPath,
        string.IsNullOrWhiteSpace(_config.EditLabOutputDirectory) ? null : _config.EditLabOutputDirectory);

    public SetupAudioSettings ReadAudio() => new(_config.AudioOutputMode, _config.AudioOutputDeviceName, _config.AnalyzeNewDownloads);

    public void WriteFolders(SetupFolderSettings settings)
    {
        _config.DownloadDirectory = settings.DownloadDirectory;
        _config.EnableLibrarySharing = settings.ShareOnSoulseek;
        _config.SharedFolderPath = string.IsNullOrWhiteSpace(settings.ExtraSharedFolder) ? null : settings.ExtraSharedFolder;
        _config.EditLabOutputDirectory = string.IsNullOrWhiteSpace(settings.EditLabOutputDirectory) ? null : settings.EditLabOutputDirectory;
        _configManager.Save(_config);
        _logger.LogInformation("Setup: folders saved (downloads={Downloads}, sharing={Sharing}, extraShare={Share}, editLab={EditLab})",
            settings.DownloadDirectory, settings.ShareOnSoulseek, settings.ExtraSharedFolder ?? "-", settings.EditLabOutputDirectory ?? "(follows downloads)");

        if (_soulseek != null)
        {
            _ = Task.Run(async () =>
            {
                try { await _soulseek.RefreshShareStateAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Setup: share refresh after folder change failed"); }
            });
        }
    }

    public void WriteAudio(SetupAudioSettings settings)
    {
        var outputChanged = _config.AudioOutputMode != settings.OutputMode || _config.AudioOutputDeviceName != settings.OutputDevice;
        _config.AudioOutputMode = settings.OutputMode;
        _config.AudioOutputDeviceName = string.IsNullOrWhiteSpace(settings.OutputDevice) ? null : settings.OutputDevice;
        _config.AnalyzeNewDownloads = settings.AnalyzeNewDownloads;
        _configManager.Save(_config);
        _logger.LogInformation("Setup: audio saved (mode={Mode}, device={Device}, analyseNewDownloads={Analyse})",
            settings.OutputMode, settings.OutputDevice ?? "(system default)", settings.AnalyzeNewDownloads);

        if (outputChanged && _player != null)
        {
            _ = Task.Run(() =>
            {
                try { _player.ApplyOutputSettings(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Setup: applying the new audio output failed"); }
            });
        }
    }
}

/// <summary>
/// Soulseek sign-in for the wizard. Uses the app's own <see cref="IConnectionLifecycleService"/>
/// (the same path as the login dialog), so a successful test leaves ORBIT connected.
/// </summary>
public sealed class SoulseekSetupGateway : ISoulseekSetupGateway
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly IConnectionLifecycleService _lifecycle;
    private readonly IEventBus _eventBus;
    private readonly ISoulseekCredentialService _credentials;
    private readonly ILogger<SoulseekSetupGateway> _logger;
    private readonly TimeSpan _timeout;

    public SoulseekSetupGateway(
        AppConfig config,
        ConfigManager configManager,
        IConnectionLifecycleService lifecycle,
        IEventBus eventBus,
        ISoulseekCredentialService credentials,
        ILogger<SoulseekSetupGateway> logger)
        : this(config, configManager, lifecycle, eventBus, credentials, logger, DefaultTimeout) { }

    public SoulseekSetupGateway(
        AppConfig config,
        ConfigManager configManager,
        IConnectionLifecycleService lifecycle,
        IEventBus eventBus,
        ISoulseekCredentialService credentials,
        ILogger<SoulseekSetupGateway> logger,
        TimeSpan timeout)
    {
        _config = config;
        _configManager = configManager;
        _lifecycle = lifecycle;
        _eventBus = eventBus;
        _credentials = credentials;
        _logger = logger;
        _timeout = timeout;
    }

    public SoulseekAccountSnapshot GetCurrent() => new(
        _config.Username ?? string.Empty,
        // A fresh account defaults to "remember me"; an existing one shows what it has.
        string.IsNullOrEmpty(_config.Username) || (_config.RememberPassword && _config.AutoConnectEnabled),
        _lifecycle.CurrentState == ConnectionLifecycleState.LoggedIn);

    public async Task<SoulseekLoginResult> TestLoginAsync(string username, string password, CancellationToken ct)
    {
        username = username?.Trim() ?? string.Empty;
        if (username.Length == 0) return SoulseekLoginResultMapper.MissingInput("username");
        if (string.IsNullOrEmpty(password)) return SoulseekLoginResultMapper.MissingInput("password");

        var previousUsername = _config.Username;

        if (_lifecycle.CurrentState == ConnectionLifecycleState.LoggedIn)
        {
            if (string.Equals(previousUsername, username, StringComparison.Ordinal))
                return SoulseekLoginResultMapper.AlreadyConnected(username);

            _logger.LogInformation("Setup: switching Soulseek account from {Old} to {New} for a connection test", previousUsername, username);
            await _lifecycle.RequestDisconnectAsync("setup wizard: switching account");
            await WaitForStateAsync(s => s == ConnectionLifecycleState.Disconnected, TimeSpan.FromSeconds(10), ct);
        }

        if (_lifecycle.CurrentState == ConnectionLifecycleState.CoolingDown)
            return SoulseekLoginResultMapper.CoolingDown();

        var terminal = new TaskCompletionSource<ConnectionLifecycleStateChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _eventBus.GetEvent<ConnectionLifecycleStateChangedEvent>().Subscribe(evt =>
        {
            if (evt.Current is "LoggedIn" or "Disconnected" or "CoolingDown")
                terminal.TrySetResult(evt);
        });

        SoulseekLoginResult? result = null;
        // The adapter signs in with AppConfig.Username, so the name under test goes there for the
        // attempt; it is only saved to config.ini when the step is confirmed (SaveAccountAsync).
        _config.Username = username;
        try
        {
            try
            {
                await _lifecycle.RequestConnectAsync(password, "setup-wizard", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Setup: Soulseek connection test threw");
                return result = SoulseekLoginResultMapper.FromException(ex);
            }

            if (_lifecycle.CurrentState == ConnectionLifecycleState.LoggedIn)
                return result = SoulseekLoginResultMapper.Connected(username);

            var finished = await Task.WhenAny(terminal.Task, Task.Delay(_timeout, ct));
            if (finished != terminal.Task)
            {
                result = _lifecycle.CurrentState == ConnectionLifecycleState.LoggedIn
                    ? SoulseekLoginResultMapper.Connected(username)
                    : SoulseekLoginResultMapper.Timeout();
                return result;
            }

            var evt = await terminal.Task;
            result = SoulseekLoginResultMapper.FromLifecycle(evt.Current, evt.Reason, username);
            return result;
        }
        finally
        {
            // A failed test must not leave a half-changed account in memory.
            if (!(result?.IsSuccess ?? false))
                _config.Username = previousUsername;
            _logger.LogInformation("Setup: Soulseek connection test for {User} → {Outcome}", username, result?.Outcome.ToString() ?? "cancelled");
        }
    }

    public async Task SaveAccountAsync(SoulseekAccountChoice choice)
    {
        _config.Username = choice.Username.Trim();
        _config.RememberPassword = choice.RememberAndAutoConnect;
        _config.AutoConnectEnabled = choice.RememberAndAutoConnect;
        _lifecycle.AutoReconnectEnabled = choice.RememberAndAutoConnect;
        _configManager.Save(_config);

        if (choice.RememberAndAutoConnect && !string.IsNullOrEmpty(choice.Password))
            await _credentials.SaveCredentialsAsync(_config.Username, choice.Password);
        else if (!choice.RememberAndAutoConnect)
            await _credentials.DeleteCredentialsAsync();

        _logger.LogInformation("Setup: Soulseek account saved for {User} (remember + auto-connect: {Remember})", _config.Username, choice.RememberAndAutoConnect);
    }

    private async Task WaitForStateAsync(Func<ConnectionLifecycleState, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate(_lifecycle.CurrentState) && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
    }
}

/// <summary>Library Sources (the LibraryFolders table) for the Folders step, plus "Scan now".</summary>
public sealed class LibraryFolderSetupGateway : ILibraryFolderSetupGateway
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly LibraryFolderScannerService _scanner;
    private readonly IEventBus _eventBus;
    private readonly ILogger<LibraryFolderSetupGateway> _logger;
    private readonly ILibraryImportLauncher? _importLauncher;

    public LibraryFolderSetupGateway(
        IDbContextFactory<AppDbContext> dbFactory,
        LibraryFolderScannerService scanner,
        IEventBus eventBus,
        ILogger<LibraryFolderSetupGateway> logger,
        ILibraryImportLauncher? importLauncher = null)
    {
        _dbFactory = dbFactory;
        _scanner = scanner;
        _eventBus = eventBus;
        _logger = logger;
        _importLauncher = importLauncher;
    }

    public async Task<IReadOnlyList<LibraryFolderSetupEntry>> GetExistingAsync()
    {
        await using var context = await _dbFactory.CreateDbContextAsync();
        return await context.LibraryFolders
            .Select(f => new LibraryFolderSetupEntry(f.FolderPath, f.IsWatched))
            .ToListAsync();
    }

    public async Task<int> AddAsync(IReadOnlyList<LibraryFolderSetupEntry> folders)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();
        var existing = await context.LibraryFolders.ToListAsync();
        var added = 0;
        foreach (var folder in folders)
        {
            var match = existing.FirstOrDefault(e => FirstRunDetector.SamePath(e.FolderPath, folder.Path));
            if (match != null)
            {
                if (match.IsWatched != folder.Watch) match.IsWatched = folder.Watch;
                continue;
            }
            var entity = new Data.Entities.LibraryFolderEntity
            {
                Id = Guid.NewGuid(),
                FolderPath = folder.Path,
                IsEnabled = true,
                IsWatched = folder.Watch,
                AddedAt = DateTime.UtcNow,
            };
            context.LibraryFolders.Add(entity);
            existing.Add(entity);
            added++;
        }
        await context.SaveChangesAsync();
        _eventBus.Publish(new LibraryFoldersChangedEvent());
        _logger.LogInformation("Setup: {Added} library folder(s) added ({Total} confirmed)", added, folders.Count);
        return added;
    }

    /// <summary>"Scan now" opens the import sheet (over the wizard) for every Library Source.</summary>
    public void StartScan()
    {
        if (_importLauncher != null)
        {
            _ = _importLauncher.StartImportAsync(null, "Importing your music");
            return;
        }

        // Headless fallback (no shell): import in the background and report the result as a toast.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest());
                _eventBus.Publish(new ToastRequestedEvent("Library scan finished",
                    $"{SLSKDONET.Utils.CountFormat.N(result.Added)} tracks added from {SLSKDONET.Utils.CountFormat.N(result.FilesFound)} files.", NotificationType.Success, TimeSpan.FromSeconds(6)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Setup: library folder scan failed");
                _eventBus.Publish(new ToastRequestedEvent("Library scan failed", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(8)));
            }
        });
    }
}

/// <summary>Which bundled analysis models are present (they ship in Tools/Essentia/models; there is no download step).</summary>
public static class AnalysisModelInventory
{
    public static readonly IReadOnlyList<(string File, string Purpose)> CoreModels = new[]
    {
        ("discogs-effnet-bsdynamic-1.onnx", "audio embeddings"),
        ("mtg_jamendo_genre-discogs-effnet-1.onnx", "genre"),
        ("mood_happy-discogs-effnet-1.onnx", "mood"),
        ("mood_sad-discogs-effnet-1.onnx", "mood"),
        ("mood_relaxed-discogs-effnet-1.onnx", "mood"),
        ("mood_party-discogs-effnet-1.onnx", "mood"),
        ("mood_aggressive-discogs-effnet-1.onnx", "mood"),
        ("cue-detr.onnx", "cue confidence"),
    };

    public static string ModelsDirectory(string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Tools", "Essentia", "models");

    public static AnalysisModelStatus Check(string? baseDirectory = null)
    {
        var dir = ModelsDirectory(baseDirectory);
        var missing = CoreModels.Where(m => !File.Exists(Path.Combine(dir, m.File))).Select(m => m.File).ToList();
        return new AnalysisModelStatus(CoreModels.Count - missing.Count, CoreModels.Count, missing);
    }
}

/// <summary>Startup check: decides whether the wizard appears, and silently marks existing installs as set up.</summary>
public sealed class FirstRunCoordinator
{
    private readonly AppConfig _config;
    private readonly ISetupStateStore _state;
    private readonly ISoulseekCredentialService _credentials;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly ILogger<FirstRunCoordinator> _logger;

    public FirstRunCoordinator(
        AppConfig config,
        ISetupStateStore state,
        ISoulseekCredentialService credentials,
        ILogger<FirstRunCoordinator> logger,
        IDbContextFactory<AppDbContext>? dbFactory = null)
    {
        _config = config;
        _state = state;
        _credentials = credentials;
        _logger = logger;
        _dbFactory = dbFactory;
    }

    public async Task<FirstRunSignals> GatherSignalsAsync()
    {
        var hasCredentials = false;
        try
        {
            var (_, password) = await _credentials.LoadCredentialsAsync();
            hasCredentials = !string.IsNullOrEmpty(password);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "First-run check: could not read stored Soulseek credentials");
        }

        int tracks = 0, folders = 0;
        if (_dbFactory != null)
        {
            try
            {
                await using var context = await _dbFactory.CreateDbContextAsync();
                tracks = await context.LibraryEntries.CountAsync();
                if (tracks == 0) tracks = await context.Tracks.CountAsync();
                var folderPaths = await context.LibraryFolders.Select(f => f.FolderPath).ToListAsync();
                // The download folder is auto-registered as a Library Source; it isn't a sign of use.
                folders = folderPaths.Count(p => !FirstRunDetector.SamePath(p, _config.DownloadDirectory)
                                              && !FirstRunDetector.SamePath(p, AppConfig.FallbackDownloadDirectory));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "First-run check: could not count tracks / library folders");
            }
        }

        return new FirstRunSignals(_config.SetupCompleted, _config.Username, hasCredentials,
            _config.DownloadDirectory, _config.SharedFolderPath, tracks, folders);
    }

    /// <summary>Evaluates the install; an existing install is marked completed here (config.ini is saved).</summary>
    public async Task<FirstRunResult> EvaluateAsync()
    {
        var signals = await GatherSignalsAsync();
        var result = FirstRunDetector.Evaluate(signals, AppConfig.FallbackDownloadDirectory);
        _logger.LogInformation("First-run check: {Decision} ({Evidence})", result.Decision, string.Join(", ", result.Evidence));
        if (result.ShouldMarkCompleted)
        {
            try
            {
                _state.MarkSetupCompleted();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still never show the wizard to an existing user this session.
                _logger.LogError(ex, "First-run check: could not save [Setup] Completed for an existing install");
            }
        }
        return result;
    }
}
