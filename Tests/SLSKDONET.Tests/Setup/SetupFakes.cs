using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;
using SLSKDONET.ViewModels.Setup;
using SLSKDONET.Views;

namespace SLSKDONET.Tests.Setup;

// Fakes for the setup wizard. Nothing here touches %AppData%: config goes to a temp file,
// folders to a temp directory, and no Soulseek/Spotify/database call is made.

internal sealed class FakeSetupState : ISetupStateStore
{
    public bool IsSetupCompleted { get; private set; }
    public bool IsTourCompleted { get; private set; }
    public int SetupMarks { get; private set; }
    public void MarkSetupCompleted() { IsSetupCompleted = true; SetupMarks++; }
    public void MarkTourCompleted() => IsTourCompleted = true;
}

internal sealed class FakeSoulseekGateway : ISoulseekSetupGateway
{
    public SoulseekAccountSnapshot Current { get; set; } = new("", true, false);
    public Func<string, string, SoulseekLoginResult> Login { get; set; } = (u, _) => SoulseekLoginResultMapper.Connected(u);
    public int TestCalls { get; private set; }
    public List<SoulseekAccountChoice> Saved { get; } = new();

    public SoulseekAccountSnapshot GetCurrent() => Current;

    public Task<SoulseekLoginResult> TestLoginAsync(string username, string password, CancellationToken ct)
    {
        TestCalls++;
        return Task.FromResult(Login(username, password));
    }

    public Task SaveAccountAsync(SoulseekAccountChoice choice)
    {
        Saved.Add(choice);
        return Task.CompletedTask;
    }
}

internal sealed class FakeLibraryFolders : ILibraryFolderSetupGateway
{
    public List<LibraryFolderSetupEntry> Existing { get; } = new();
    public List<LibraryFolderSetupEntry> Added { get; } = new();
    public int Scans { get; private set; }

    public Task<IReadOnlyList<LibraryFolderSetupEntry>> GetExistingAsync() => Task.FromResult<IReadOnlyList<LibraryFolderSetupEntry>>(Existing.ToList());

    public Task<int> AddAsync(IReadOnlyList<LibraryFolderSetupEntry> folders)
    {
        var fresh = folders.Where(f => Existing.Concat(Added).All(e => !FirstRunDetector.SamePath(e.Path, f.Path))).ToList();
        Added.AddRange(fresh);
        return Task.FromResult(fresh.Count);
    }

    public void StartScan() => Scans++;
}

internal sealed class FakeSpotify : ISpotifySetupGateway
{
    public bool IsConnected { get; set; }
    public event EventHandler<bool>? ConnectionChanged;
    public Task<SpotifyConnectResult> ConnectAsync()
    {
        IsConnected = true;
        ConnectionChanged?.Invoke(this, true);
        return Task.FromResult(new SpotifyConnectResult(true, "Connected"));
    }
    public void CancelConnect() { }
}

internal sealed class FakeAudio : IAudioSetupGateway
{
    public bool FfmpegAvailable { get; set; } = true;
    public int FfmpegPageOpens { get; private set; }
    public IReadOnlyList<string> OutputModes { get; } = new[] { "WasapiShared", "WaveOut", "Asio" };
    public IReadOnlyList<string> GetDevices(string outputMode) => outputMode == "WasapiShared" ? new[] { "Speakers", "Headphones" } : Array.Empty<string>();
    public Task<FfmpegCheckResult> CheckFfmpegAsync() => Task.FromResult(new FfmpegCheckResult(FfmpegAvailable, FfmpegAvailable ? "7.0" : ""));
    public void OpenFfmpegDownloadPage() => FfmpegPageOpens++;
    public AnalysisModelStatus GetModelStatus() => new(8, 8, Array.Empty<string>());
}

internal sealed class FakePicker : IFileInteractionService
{
    public Queue<string?> Folders { get; } = new();
    public Task<string?> OpenFolderDialogAsync(string title) => Task.FromResult(Folders.Count > 0 ? Folders.Dequeue() : null);
    public Task<string?> OpenFileDialogAsync(string title, IEnumerable<FileDialogFilter>? filters = null) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileDialogAsync(string title, string defaultFileName, string? extension = null) => Task.FromResult<string?>(null);
    public void RevealFileInExplorer(string filePath) { }
}

internal sealed class FakeNotifications : INotificationService
{
    public List<(string Title, string Message)> Shown { get; } = new();
    public void Show(string title, string message, NotificationType type = NotificationType.Information, TimeSpan? duration = null) => Shown.Add((title, message));
}

/// <summary>A temp directory + temp config.ini, deleted on dispose.</summary>
internal sealed class SetupSandbox : IDisposable
{
    public SetupSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "orbit-setup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ConfigPath = Path.Combine(Root, "config.ini");
        ConfigManager = new ConfigManager(ConfigPath);
        Config = new AppConfig { DownloadDirectory = AppConfig.FallbackDownloadDirectory };
    }

    public string Root { get; }
    public string ConfigPath { get; }
    public ConfigManager ConfigManager { get; }
    public AppConfig Config { get; }
    public FakeSetupState State { get; } = new();
    public FakeSoulseekGateway Soulseek { get; } = new();
    public FakeLibraryFolders LibraryFolders { get; } = new();
    public FakeSpotify Spotify { get; } = new();
    public FakeAudio Audio { get; } = new();
    public FakePicker Picker { get; } = new();
    public FakeNotifications Notifications { get; } = new();

    public string Dir(string name) => Path.Combine(Root, name);

    public ConfigSetupSettingsGateway SettingsGateway() =>
        new(Config, ConfigManager, NullLogger<ConfigSetupSettingsGateway>.Instance);

    public SetupWizardViewModel CreateWizard(ISetupFolderService? folders = null) => new(
        State, Soulseek, SettingsGateway(), LibraryFolders,
        folders ?? new SetupFolderService(null, canWrite: _ => true, freeBytes: _ => 100L * 1024 * 1024 * 1024),
        Picker, Spotify, Audio, NullLogger<SetupWizardViewModel>.Instance, Notifications);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* temp dir; the OS cleans up */ }
    }
}
