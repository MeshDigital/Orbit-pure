using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.Setup;

// The setup wizard's view models only talk to these interfaces, so tests drive the whole wizard
// with fakes. The real implementations (SetupGateways.cs) reuse the services Settings already uses.

/// <summary>Settings → "Run setup again" (published on the event bus, handled by the shell).</summary>
public sealed record OpenSetupWizardRequestedEvent;

/// <summary>Settings → "Take the tour".</summary>
public sealed record StartTourRequestedEvent;

/// <summary>[Setup] flags in config.ini.</summary>
public interface ISetupStateStore
{
    bool IsSetupCompleted { get; }
    bool IsTourCompleted { get; }
    void MarkSetupCompleted();
    void MarkTourCompleted();
}

public sealed record SoulseekAccountSnapshot(string Username, bool RememberAndAutoConnect, bool IsConnected);

public sealed record SoulseekAccountChoice(string Username, string Password, bool RememberAndAutoConnect);

public interface ISoulseekSetupGateway
{
    SoulseekAccountSnapshot GetCurrent();

    /// <summary>Really signs in through the app's connection lifecycle and reports how it went.</summary>
    Task<SoulseekLoginResult> TestLoginAsync(string username, string password, CancellationToken ct);

    /// <summary>Saves the account (username, remember / auto-connect, stored password) on confirm.</summary>
    Task SaveAccountAsync(SoulseekAccountChoice choice);
}

public sealed record LibraryFolderSetupEntry(string Path, bool Watch);

public interface ILibraryFolderSetupGateway
{
    Task<IReadOnlyList<LibraryFolderSetupEntry>> GetExistingAsync();

    /// <summary>Adds the folders that aren't Library Sources yet. Returns how many were added.</summary>
    Task<int> AddAsync(IReadOnlyList<LibraryFolderSetupEntry> folders);

    /// <summary>Imports every Library Source through the import sheet (phases, progress, summary).</summary>
    void StartScan();

    /// <summary>
    /// The Folders step's "Import now": the same import, but it starts in the background
    /// (activity chip) so the wizard carries on while it runs.
    /// </summary>
    void StartBackgroundScan() => StartScan();
}

public sealed record SetupFolderSettings(
    string DownloadDirectory,
    bool ShareOnSoulseek,
    string? ExtraSharedFolder,
    // null = follow the download folder (<downloads>/ORBIT Edits)
    string? EditLabOutputDirectory);

public sealed record SetupAudioSettings(string OutputMode, string? OutputDevice, bool AnalyzeNewDownloads);

/// <summary>Reads and writes the config values the wizard edits (the same AppConfig Settings binds to).</summary>
public interface ISetupSettingsGateway
{
    /// <summary>The current download folder, or null when only the built-in fallback is in use.</summary>
    string? ChosenDownloadDirectory { get; }
    SetupFolderSettings ReadFolders();
    SetupAudioSettings ReadAudio();
    void WriteFolders(SetupFolderSettings settings);
    void WriteAudio(SetupAudioSettings settings);
}

public sealed record SpotifyConnectResult(bool Connected, string Message);

public interface ISpotifySetupGateway
{
    bool IsConnected { get; }
    event EventHandler<bool>? ConnectionChanged;
    Task<SpotifyConnectResult> ConnectAsync();
    void CancelConnect();
}

public sealed record FfmpegCheckResult(bool IsAvailable, string Version);

public sealed record AnalysisModelStatus(int Present, int Total, IReadOnlyList<string> Missing)
{
    public bool AllPresent => Present == Total;
}

public interface IAudioSetupGateway
{
    IReadOnlyList<string> OutputModes { get; }
    IReadOnlyList<string> GetDevices(string outputMode);
    Task<FfmpegCheckResult> CheckFfmpegAsync();
    void OpenFfmpegDownloadPage();
    AnalysisModelStatus GetModelStatus();

    /// <summary>This machine and which heavy features it may run.</summary>
    SLSKDONET.Services.Platform.HardwareProfile Hardware { get; }

    /// <summary>True where ORBIT can download and install FFmpeg itself (Windows x64).</summary>
    bool CanInstallFfmpeg { get; }

    /// <summary>What to tell the user where <see cref="CanInstallFfmpeg"/> is false.</summary>
    string ManualFfmpegHint { get; }

    Task<SLSKDONET.Services.Platform.FfmpegInstallResult> InstallFfmpegAsync(IProgress<double>? progress);
}

/// <summary>One metadata source as the wizard's "Metadata & artwork" step shows it.</summary>
public sealed record SetupMetadataSource(
    string Name,
    string DisplayName,
    string Summary,
    bool IsEnabled,
    bool IsAlwaysOn,
    bool IsExperimental,
    SLSKDONET.Services.Enrichment.ProviderAvailability Availability);

/// <summary>What the wizard confirms on Next (or before "Start now").</summary>
public sealed record SetupEnrichmentChoice(
    IReadOnlyCollection<string> EnabledSources,
    bool AutoRun,
    string? DiscogsToken,
    string? AcoustIdKey);

/// <summary>
/// Metadata enrichment for the wizard: the same settings store as Settings → Connection →
/// Metadata sources ([Enrichment] in config.ini, keys in the DPAPI credential store) and the
/// same orchestrator as the Analysis page's Enrichment card.
/// </summary>
public interface ISetupEnrichmentGateway
{
    IReadOnlyList<SetupMetadataSource> GetSources();
    bool AutoRun { get; }
    bool HasDiscogsToken { get; }
    bool HasAcoustIdKey { get; }
    bool HasFpcalc { get; }

    /// <summary>Saves the choice. Returns null, or why a key couldn't be stored (settings are still saved).</summary>
    string? Save(SetupEnrichmentChoice choice);

    /// <summary>Tracks in the library right now.</summary>
    Task<int> CountLibraryTracksAsync();

    /// <summary>Queues every track missing metadata and starts. Returns how many need it.</summary>
    Task<int> StartAsync();
    void Pause();
    void Resume();
    void OpenUrl(string url);
    SLSKDONET.Services.Enrichment.EnrichmentProgress CurrentProgress();

    /// <summary>Progress (any thread, already throttled by the orchestrator).</summary>
    event EventHandler<SLSKDONET.Services.Enrichment.EnrichmentProgress>? ProgressChanged;

    /// <summary>Rows were added to the library (an import batch landed or finished; any thread).</summary>
    event EventHandler? LibraryChanged;
}
