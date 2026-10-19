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
}
