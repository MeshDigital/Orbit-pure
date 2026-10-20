using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Setup;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Setup;

/// <summary>Step 4: optional connections (Spotify). Connecting is an explicit action, so it saves its own tokens.</summary>
public sealed class ConnectionsStepViewModel : SetupStepViewModel, IDisposable
{
    private readonly ISpotifySetupGateway _spotify;
    private readonly ILogger _logger;
    private readonly AsyncRelayCommand _connectCommand;
    private readonly RelayCommand _cancelCommand;
    private bool _isConnected;
    private bool _isConnecting;
    private string? _statusMessage;

    public ConnectionsStepViewModel(ISpotifySetupGateway spotify, ILogger logger)
    {
        _spotify = spotify;
        _logger = logger;
        _isConnected = spotify.IsConnected;
        _connectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnecting && !IsSpotifyConnected);
        _cancelCommand = new RelayCommand(() => _spotify.CancelConnect(), () => IsConnecting);
        ConnectSpotifyCommand = _connectCommand;
        CancelSpotifyCommand = _cancelCommand;
        _spotify.ConnectionChanged += OnConnectionChanged;
    }

    public override string Title => "Optional connections";
    public override string ShortTitle => "Connections";
    public override string Subtitle => "Connect Spotify to import your playlists and Liked Songs. You can do this later in Settings.";
    public override bool CanSkip => true;

    public ICommand ConnectSpotifyCommand { get; }
    public ICommand CancelSpotifyCommand { get; }

    public bool IsSpotifyConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsSpotifyDisconnected));
                _connectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSpotifyDisconnected => !IsSpotifyConnected && !IsConnecting;

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(IsSpotifyDisconnected));
                _connectCommand.RaiseCanExecuteChanged();
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
                OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>
    /// How long after the page appears a Connect press is taken as carried over from the previous
    /// page (the click or Enter that pressed Next, a double-click, a held Enter) rather than meant.
    /// Spotify sign-in opens the user's browser, so it must only ever start from a deliberate press.
    /// </summary>
    internal TimeSpan ArmDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private DateTime _shownAtUtc = DateTime.MinValue;

    public override void OnShown() => _shownAtUtc = UtcNow();

    public async Task ConnectAsync()
    {
        var sinceShown = UtcNow() - _shownAtUtc;
        if (sinceShown < ArmDelay)
        {
            _logger.LogInformation("Setup: ignored a Spotify Connect {Ms:0} ms after the Connections page appeared (input carried over from the previous page)",
                sinceShown.TotalMilliseconds);
            return;
        }

        IsConnecting = true;
        StatusMessage = "Finish signing in in your browser window.";
        try
        {
            var result = await _spotify.ConnectAsync();
            IsSpotifyConnected = result.Connected || _spotify.IsConnected;
            StatusMessage = IsSpotifyConnected ? null : result.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: Spotify connect failed");
            StatusMessage = "Spotify sign-in failed: " + ex.Message;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public override string SummaryTitle => "Spotify";

    public override string SummaryDetail => IsSpotifyConnected ? "Connected" : Status == SetupStepStatus.Skipped ? "Skipped" : "Not connected";

    private void OnConnectionChanged(object? sender, bool connected)
    {
        // The auth service raises this off the UI thread; Avalonia marshals INPC from bindings.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSpotifyConnected = connected);
    }

    public void Dispose() => _spotify.ConnectionChanged -= OnConnectionChanged;
}

/// <summary>Step 5: audio output, FFmpeg, analysis models and auto-analysis of new downloads.</summary>
public sealed class AudioStepViewModel : SetupStepViewModel
{
    public const string SystemDefaultDevice = "System default";

    private readonly IAudioSetupGateway _audio;
    private readonly ISetupSettingsGateway _settings;
    private readonly ILogger _logger;
    private readonly AsyncRelayCommand _checkFfmpegCommand;

    private string _outputMode = "WasapiShared";
    private string _outputDevice = SystemDefaultDevice;
    private bool _analyzeNewDownloads = true;
    private bool _isCheckingFfmpeg;
    private bool? _ffmpegFound;
    private string _ffmpegText = "Not checked yet";
    private AnalysisModelStatus _models = new(0, 0, Array.Empty<string>());

    public AudioStepViewModel(IAudioSetupGateway audio, ISetupSettingsGateway settings, ILogger logger)
    {
        _audio = audio;
        _settings = settings;
        _logger = logger;
        _checkFfmpegCommand = new AsyncRelayCommand(CheckFfmpegAsync, () => !IsCheckingFfmpeg);
        CheckFfmpegCommand = _checkFfmpegCommand;
        GetFfmpegCommand = new RelayCommand(_audio.OpenFfmpegDownloadPage);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
    }

    public override string Title => "Audio and analysis";
    public override string ShortTitle => "Audio";
    public override string Subtitle => "Pick where ORBIT plays, and check what analysis needs.";
    public override bool CanSkip => true;

    public ICommand CheckFfmpegCommand { get; }
    public ICommand GetFfmpegCommand { get; }
    public ICommand RefreshDevicesCommand { get; }

    public ObservableCollection<string> OutputModes { get; } = new();
    public ObservableCollection<string> Devices { get; } = new();

    public string OutputMode
    {
        get => _outputMode;
        set
        {
            if (value != null && SetProperty(ref _outputMode, value))
                RefreshDevices();
        }
    }

    public string OutputDevice
    {
        get => _outputDevice;
        set => SetProperty(ref _outputDevice, string.IsNullOrEmpty(value) ? SystemDefaultDevice : value);
    }

    public bool HasDeviceChoice => Devices.Count > 1;

    public bool AnalyzeNewDownloads
    {
        get => _analyzeNewDownloads;
        set => SetProperty(ref _analyzeNewDownloads, value);
    }

    public bool IsCheckingFfmpeg
    {
        get => _isCheckingFfmpeg;
        private set
        {
            if (SetProperty(ref _isCheckingFfmpeg, value))
                _checkFfmpegCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>null = not checked yet.</summary>
    public bool? FfmpegFound
    {
        get => _ffmpegFound;
        private set
        {
            if (SetProperty(ref _ffmpegFound, value))
            {
                OnPropertyChanged(nameof(FfmpegIsMissing));
                OnPropertyChanged(nameof(FfmpegIsOk));
            }
        }
    }

    public bool FfmpegIsOk => FfmpegFound == true;
    public bool FfmpegIsMissing => FfmpegFound == false;

    public string FfmpegText
    {
        get => _ffmpegText;
        private set => SetProperty(ref _ffmpegText, value);
    }

    public AnalysisModelStatus Models
    {
        get => _models;
        private set
        {
            if (SetProperty(ref _models, value))
            {
                OnPropertyChanged(nameof(ModelsText));
                OnPropertyChanged(nameof(ModelsMissingText));
                OnPropertyChanged(nameof(ModelsAllPresent));
                OnPropertyChanged(nameof(ModelsIncomplete));
            }
        }
    }

    public bool ModelsAllPresent => Models.Total > 0 && Models.AllPresent;
    public bool ModelsIncomplete => !ModelsAllPresent;
    public string ModelsText => $"{Models.Present} of {Models.Total} models present";
    public string ModelsMissingText => Models.Missing.Count == 0
        ? "Genre, mood, embeddings and cue confidence are ready."
        : "Missing: " + string.Join(", ", Models.Missing) + ". These ship with ORBIT; reinstall to restore them. BPM, key and cues still work without them.";

    public override async Task InitializeAsync()
    {
        var current = _settings.ReadAudio();
        OutputModes.Clear();
        foreach (var mode in _audio.OutputModes) OutputModes.Add(mode);
        _outputMode = OutputModes.Contains(current.OutputMode) ? current.OutputMode : OutputModes.FirstOrDefault() ?? current.OutputMode;
        OnPropertyChanged(nameof(OutputMode));
        RefreshDevices();
        OutputDevice = current.OutputDevice != null && Devices.Contains(current.OutputDevice) ? current.OutputDevice : SystemDefaultDevice;
        AnalyzeNewDownloads = current.AnalyzeNewDownloads;
        Models = _audio.GetModelStatus();
        await CheckFfmpegAsync();
    }

    public async Task CheckFfmpegAsync()
    {
        IsCheckingFfmpeg = true;
        FfmpegText = "Checking…";
        try
        {
            var result = await _audio.CheckFfmpegAsync();
            FfmpegFound = result.IsAvailable;
            FfmpegText = result.IsAvailable
                ? (string.IsNullOrWhiteSpace(result.Version) ? "Installed" : $"Installed · v{result.Version}")
                : "Not found";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup: FFmpeg check failed");
            FfmpegFound = false;
            FfmpegText = "Check failed: " + ex.Message;
        }
        finally
        {
            IsCheckingFfmpeg = false;
        }
    }

    private void RefreshDevices()
    {
        var keep = _outputDevice;
        Devices.Clear();
        Devices.Add(SystemDefaultDevice);
        try
        {
            foreach (var device in _audio.GetDevices(OutputMode)) Devices.Add(device);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup: listing {Mode} devices failed", OutputMode);
            ErrorMessage = "Couldn't list audio devices: " + ex.Message;
        }
        OutputDevice = Devices.Contains(keep) ? keep : SystemDefaultDevice;
        OnPropertyChanged(nameof(OutputDevice));
        OnPropertyChanged(nameof(HasDeviceChoice));
    }

    public override Task<bool> CommitAsync()
    {
        ClearError();
        try
        {
            _settings.WriteAudio(new SetupAudioSettings(OutputMode, OutputDevice == SystemDefaultDevice ? null : OutputDevice, AnalyzeNewDownloads));
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup: saving audio settings failed");
            ErrorMessage = "The audio settings couldn't be saved: " + ex.Message;
            return Task.FromResult(false);
        }
    }

    public override string SummaryTitle => "Audio and analysis";

    public override string SummaryDetail => Status switch
    {
        SetupStepStatus.Configured => $"{OutputMode} · {OutputDevice}"
                                      + (AnalyzeNewDownloads ? " · new downloads analysed" : " · auto-analysis off")
                                      + (FfmpegFound == false ? " · FFmpeg missing" : string.Empty),
        SetupStepStatus.Skipped => "Skipped" + (FfmpegFound == false ? " · FFmpeg missing" : string.Empty),
        _ => "Not changed",
    };
}

/// <summary>Step 6: summary, "Scan now" for new library folders, then "Take the quick tour" / "Start using ORBIT".</summary>
public sealed class DoneStepViewModel : SetupStepViewModel
{
    private readonly ILibraryFolderSetupGateway _libraryFolders;
    private readonly RelayCommand _scanCommand;
    private int _newLibraryFolderCount;
    private bool _scanStarted;

    public DoneStepViewModel(ILibraryFolderSetupGateway libraryFolders)
    {
        _libraryFolders = libraryFolders;
        _scanCommand = new RelayCommand(StartScan, () => CanScanNow);
        ScanNowCommand = _scanCommand;
    }

    public override string Title => "You're set up";
    public override string ShortTitle => "Done";
    public override string Subtitle => "Here's what's configured. Everything can be changed later in Settings.";
    public override bool AppearsInSummary => false;

    public ObservableCollection<SetupSummaryItem> Summary { get; } = new();

    public ICommand ScanNowCommand { get; }

    public int NewLibraryFolderCount
    {
        get => _newLibraryFolderCount;
        set
        {
            if (SetProperty(ref _newLibraryFolderCount, value))
            {
                OnPropertyChanged(nameof(CanScanNow));
                OnPropertyChanged(nameof(ScanNowText));
                _scanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanScanNow => NewLibraryFolderCount > 0 && !ScanStarted;

    public string ScanNowText => NewLibraryFolderCount == 1
        ? "You added a library folder. Scan it now to bring its music into ORBIT."
        : $"You added {NewLibraryFolderCount} library folders. Scan them now to bring their music into ORBIT.";

    public bool ScanStarted
    {
        get => _scanStarted;
        private set
        {
            if (SetProperty(ref _scanStarted, value))
            {
                OnPropertyChanged(nameof(CanScanNow));
                _scanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void SetSummary(System.Collections.Generic.IEnumerable<SetupSummaryItem> items)
    {
        Summary.Clear();
        foreach (var item in items) Summary.Add(item);
    }

    private void StartScan()
    {
        if (!CanScanNow) return;
        _libraryFolders.StartScan();
        ScanStarted = true;
    }
}
