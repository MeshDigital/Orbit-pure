using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>
/// Spotify for the wizard: the same sign-in Settings → Connection runs (default client ID,
/// port fallback, token storage), so both pages show the same connected state.
/// </summary>
public sealed class SettingsSpotifySetupGateway : ISpotifySetupGateway, IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly SpotifyAuthService _auth;
    private readonly EventHandler<bool> _handler;

    public SettingsSpotifySetupGateway(SettingsViewModel settings, SpotifyAuthService auth)
    {
        _settings = settings;
        _auth = auth;
        _handler = (_, connected) => ConnectionChanged?.Invoke(this, connected);
        _auth.AuthenticationChanged += _handler;
    }

    public bool IsConnected => _auth.IsAuthenticated;

    public event EventHandler<bool>? ConnectionChanged;

    public async Task<SpotifyConnectResult> ConnectAsync()
    {
        await _settings.ConnectSpotifyAsync();
        return _auth.IsAuthenticated
            ? new SpotifyConnectResult(true, "Connected")
            : new SpotifyConnectResult(false, _settings.SpotifyDisplayName is { Length: > 0 } msg && msg != "Not Connected"
                ? $"Not connected ({msg}). Try again, or connect later in Settings → Connection."
                : "Not connected. Try again, or connect later in Settings → Connection.");
    }

    public void CancelConnect() => _settings.CancelSpotifyConnect();

    public void Dispose() => _auth.AuthenticationChanged -= _handler;
}

/// <summary>Audio devices, FFmpeg and model checks, reusing what Settings → Playback / Advanced use.</summary>
public sealed class SettingsAudioSetupGateway : IAudioSetupGateway
{
    private readonly SettingsViewModel _settings;
    private readonly ILogger<SettingsAudioSetupGateway> _logger;
    private readonly SLSKDONET.Services.Platform.FfmpegInstallerService? _ffmpegInstaller;

    public SettingsAudioSetupGateway(SettingsViewModel settings, ILogger<SettingsAudioSetupGateway> logger,
        SLSKDONET.Services.Platform.FfmpegInstallerService? ffmpegInstaller = null)
    {
        _settings = settings;
        _logger = logger;
        _ffmpegInstaller = ffmpegInstaller;
    }

    public SLSKDONET.Services.Platform.HardwareProfile Hardware => SLSKDONET.Services.Platform.HardwareCapabilities.Current;

    public bool CanInstallFfmpeg => _ffmpegInstaller != null && SLSKDONET.Services.Platform.FfmpegInstallerService.CanAutoInstall;

    public string ManualFfmpegHint => SLSKDONET.Services.Platform.FfmpegInstallerService.ManualInstallHint;

    public Task<SLSKDONET.Services.Platform.FfmpegInstallResult> InstallFfmpegAsync(IProgress<double>? progress) =>
        _ffmpegInstaller?.InstallAsync(progress)
        ?? Task.FromResult(new SLSKDONET.Services.Platform.FfmpegInstallResult(false, ManualFfmpegHint));

    public IReadOnlyList<string> OutputModes => SettingsViewModel.AvailableAudioOutputModes;

    public IReadOnlyList<string> GetDevices(string outputMode) => outputMode switch
    {
        "WasapiShared" => SLSKDONET.Services.Audio.AudioOutputProvider.GetWasapiDeviceNames().ToList(),
        "Asio" => SLSKDONET.Services.Audio.AudioOutputProvider.GetAsioDriverNames().ToList(),
        _ => Array.Empty<string>(),
    };

    public async Task<FfmpegCheckResult> CheckFfmpegAsync()
    {
        await _settings.CheckFfmpegAsync();
        return new FfmpegCheckResult(_settings.IsFfmpegInstalled, _settings.FfmpegVersion);
    }

    public void OpenFfmpegDownloadPage() => _settings.OpenFfmpegDownloadPage();

    public AnalysisModelStatus GetModelStatus()
    {
        var status = AnalysisModelInventory.Check();
        if (!status.AllPresent)
            _logger.LogWarning("Setup: {Missing} analysis model(s) missing from {Dir}", status.Missing.Count, AnalysisModelInventory.ModelsDirectory());
        return status;
    }
}
