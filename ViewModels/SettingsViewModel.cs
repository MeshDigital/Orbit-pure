using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Platform;
using SLSKDONET.Views; // For AsyncRelayCommand
using SLSKDONET.ViewModels.Settings;
using Avalonia.Threading;

using System.Collections.ObjectModel; // Added
using System.Linq;
using SLSKDONET.Models; // For SearchPolicy and Events
using SLSKDONET.Data.Entities;
using SLSKDONET.Data; // For AppDbContext
using Microsoft.EntityFrameworkCore;
namespace SLSKDONET.ViewModels;


public enum SpotifyAuthStatus
{
    Disconnected,
    Connecting,
    Connected
}

public class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private const string NonStrictPreferredFormats = "flac,wav,aiff,aif,mp3";
    private const int NonStrictMinBitrate = 192;
    private const int NonStrictMaxBitrate = 0;
    private const int NonStrictSearchResponseLimit = 300;
    private const int NonStrictSearchFileLimit = 300;
    private const int NonStrictMaxPeerQueueLength = 200;

    private const string StrictPreferredFormats = "flac,wav,aiff,aif";
    private const int StrictMinBitrate = 320;
    private const int StrictMaxBitrate = 0;
    private const int StrictSearchResponseLimit = 200;
    private const int StrictSearchFileLimit = 200;
    private const int StrictMaxPeerQueueLength = 120;

    private const string StricterPreferredFormats = "flac";
    private const int StricterMinBitrate = 701;
    private const int StricterMaxBitrate = 0;
    private const int StricterSearchResponseLimit = 100;
    private const int StricterSearchFileLimit = 100;
    private const int StricterMaxPeerQueueLength = 50;

    private bool _isApplyingSearchProfile;
    private bool _isDisposed;
    private bool _isInitialized;
    private IDisposable? _libraryFoldersSubscription;

    private string _settingsSearchText = string.Empty;
    /// <summary>Drives the Settings page's search box (SettingsSearchMatchConverter) — filters
    /// the currently active tab's sections by keyword. Not persisted; page-local UI state only.</summary>
    public string SettingsSearchText
    {
        get => _settingsSearchText;
        set
        {
            if (_settingsSearchText != value)
            {
                _settingsSearchText = value;
                OnPropertyChanged();
            }
        }
    }

    private readonly ILogger<SettingsViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly SpotifyAuthService _spotifyAuthService;
    private readonly ISpotifyMetadataService _spotifyMetadataService;
    private readonly DatabaseService _databaseService;
    private readonly LibraryFolderScannerService _libraryFolderScannerService;
    private readonly ILibraryImportLauncher? _importLauncher;
    private readonly IEventBus _eventBus;
    private readonly NetworkActivityMonitor? _networkActivityMonitor;
    private readonly EngineDiagnosticsService? _engineDiagnosticsService;
    private readonly ISoulseekAdapter _soulseek;
    private readonly ISoulseekCredentialService _credentialService;
    private readonly IConnectionLifecycleService _lifecycle;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly ILibraryService? _libraryService;
    private readonly IDialogService? _dialogService;

    // Hardcoded public client ID provided by user/project
    // Ideally this would be in a secured config, but for this desktop app scenario it's acceptable as a default.
    private const string DefaultSpotifyClientId = SpotifyAuthService.DefaultClientId;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Settings Properties
    public string DownloadPath
    {
        get => _config.DownloadDirectory ?? "";
        set
        {
            if (_config.DownloadDirectory != value)
            {
                _config.DownloadDirectory = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EditLabOutputDirectoryDisplay));
                SaveSettings(); 
            }
        }
    }

    public string SharedFolderPath
    {
        get => _config.SharedFolderPath ?? "";
        set
        {
            if (_config.SharedFolderPath != value)
            {
                _config.SharedFolderPath = value;
                OnPropertyChanged();
                SaveSettings();
                // Immediately refresh share counts so the LED updates
                _ = Task.Run(() => _soulseek.RefreshShareStateAsync());
            }
        }
    }

    /// <summary>Settings → Library: queue every finished download for analysis.</summary>
    public bool AnalyzeNewDownloads
    {
        get => _config.AnalyzeNewDownloads;
        set
        {
            if (_config.AnalyzeNewDownloads != value)
            {
                _config.AnalyzeNewDownloads = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Option labels for the BPM/key backend ComboBox. Member names of
    /// <see cref="SLSKDONET.Models.AnalysisAudioEngine"/>.</summary>
    public static string[] AvailableAnalysisAudioEngines { get; } = { "Essentia", "InProcess" };

    /// <summary>Settings → Analysis: which backend produces BPM, key and the beat grid.
    /// "Essentia" (external CLI) or "InProcess" (NWaves/NAudio, no external binary).</summary>
    public string AnalysisAudioEngine
    {
        get => _config.AnalysisAudioEngine;
        set
        {
            if (_config.AnalysisAudioEngine != value)
            {
                _config.AnalysisAudioEngine = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Settings → Downloads: where Edit Lab renders go. Empty = &lt;download folder&gt;/ORBIT Edits.</summary>
    public string EditLabOutputDirectory
    {
        get => _config.EditLabOutputDirectory ?? "";
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (_config.EditLabOutputDirectory != normalized)
            {
                _config.EditLabOutputDirectory = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EditLabOutputDirectoryDisplay));
                OnPropertyChanged(nameof(HasCustomEditLabOutputDirectory));
                SaveSettings();
            }
        }
    }

    public string EditLabOutputDirectoryDisplay => string.IsNullOrWhiteSpace(_config.EditLabOutputDirectory)
        ? System.IO.Path.Combine(DownloadPath, "ORBIT Edits")
        : _config.EditLabOutputDirectory!;

    public bool HasCustomEditLabOutputDirectory => !string.IsNullOrWhiteSpace(_config.EditLabOutputDirectory);

    private ICommand? _browseEditLabOutputDirectoryCommand;
    public ICommand BrowseEditLabOutputDirectoryCommand => _browseEditLabOutputDirectoryCommand ??= new AsyncRelayCommand(async () =>
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Edit Lab Output Folder");
        if (!string.IsNullOrEmpty(path)) EditLabOutputDirectory = path;
    });

    private ICommand? _resetEditLabOutputDirectoryCommand;
    public ICommand ResetEditLabOutputDirectoryCommand => _resetEditLabOutputDirectoryCommand ??= new RelayCommand(() => EditLabOutputDirectory = "");

    /// <summary>Settings → Advanced → Setup: reopen the first-run wizard / start the quick tour.</summary>
    private ICommand? _runSetupAgainCommand;
    public ICommand RunSetupAgainCommand => _runSetupAgainCommand ??= new RelayCommand(() => _eventBus.Publish(new SLSKDONET.Services.Setup.OpenSetupWizardRequestedEvent()));
    private ICommand? _takeTourCommand;
    public ICommand TakeTourCommand => _takeTourCommand ??= new RelayCommand(() => _eventBus.Publish(new SLSKDONET.Services.Setup.StartTourRequestedEvent()));

    /// <summary>Cancels a Spotify sign-in started from here or from the setup wizard.</summary>
    public void CancelSpotifyConnect() => _connectCts?.Cancel();

    /// <summary>
    /// Re-reads every value the setup wizard can change (it writes AppConfig directly), so this
    /// page shows the same values right away.
    /// </summary>
    public void RefreshFromConfig()
    {
        foreach (var name in new[]
                 {
                     nameof(DownloadPath), nameof(SharedFolderPath), nameof(EnableLibrarySharing),
                     nameof(AudioOutputMode), nameof(AudioOutputDeviceName), nameof(AnalyzeNewDownloads),
                     nameof(EditLabOutputDirectory), nameof(EditLabOutputDirectoryDisplay), nameof(HasCustomEditLabOutputDirectory),
                     nameof(SoulseekUsername), nameof(SoulseekAutoConnectEnabled), nameof(SoulseekRememberPassword),
                 })
            OnPropertyChanged(name);
        RefreshAvailableAudioOutputDevices();
    }

    public bool EnableLibrarySharing
    {
        get => _config.EnableLibrarySharing;
        set
        {
            if (_config.EnableLibrarySharing != value)
            {
                _config.EnableLibrarySharing = value;
                OnPropertyChanged();
                SaveSettings();
                // Reflect toggle change in share state immediately
                _ = Task.Run(() => _soulseek.RefreshShareStateAsync());
                RaiseShareStatusChanged();
            }
        }
    }

    /// <summary>Opt-in toggle for local keyboard-action usage tracking (Task 19).</summary>
    public bool EnableKeyboardTelemetry
    {
        get => _config.EnableKeyboardTelemetry;
        set
        {
            if (_config.EnableKeyboardTelemetry != value)
            {
                _config.EnableKeyboardTelemetry = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// Close button minimises ORBIT to the tray (downloads keep running) instead of quitting.
    /// Read by MainWindow at close time, so it applies immediately.
    /// </summary>
    public bool CloseToTray
    {
        get => _config.CloseToTray;
        set
        {
            if (_config.CloseToTray != value)
            {
                _config.CloseToTray = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Toggle for the live network-activity feed (Soulseek + HTTP + socket/DNS). Local-only.</summary>
    public bool EnableNetworkActivityMonitor
    {
        get => _config.EnableNetworkActivityMonitor;
        set
        {
            if (_config.EnableNetworkActivityMonitor != value)
            {
                _config.EnableNetworkActivityMonitor = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Opt-in toggle for local Frequent Sources tracking and prefetch staging.</summary>
    public bool EnableFrequentSources
    {
        get => _config.EnableFrequentSources;
        set
        {
            if (_config.EnableFrequentSources != value)
            {
                _config.EnableFrequentSources = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Local staging folder for opt-in prefetching before fingerprinting.</summary>
    public string FrequentSourcesStagingPath
    {
        get => _config.FrequentSourcesStagingPath ?? string.Empty;
        set
        {
            if (_config.FrequentSourcesStagingPath != value)
            {
                _config.FrequentSourcesStagingPath = value ?? string.Empty;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }


    public string FileNameFormat
    {
        get => _config.NameFormat ?? "{artist} - {title}";
        set
        {
            if (_config.NameFormat != value)
            {
                _config.NameFormat = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }
    
    public bool CheckForDuplicates
    {
        get => _config.CheckForDuplicates;
        set
        {
            if (_config.CheckForDuplicates != value)
            {
                _config.CheckForDuplicates = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Auto-reply to peer "reply with this word to unlock downloads" gate-bots, only for peers we've recently attempted to download from.</summary>
    public bool AutoSolveDownloadVerificationChallenges
    {
        get => _config.AutoSolveDownloadVerificationChallenges;
        set
        {
            if (_config.AutoSolveDownloadVerificationChallenges != value)
            {
                _config.AutoSolveDownloadVerificationChallenges = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Matches <see cref="SLSKDONET.Services.Audio.AudioOutputMode"/>'s member names. No
    /// WasapiExclusive: an exclusive stream locks the device, and ORBIT always plays several
    /// streams at once (two decks per crossfade, plus previews) — it broke every mix.</summary>
    public static string[] AvailableAudioOutputModes { get; } = { "WasapiShared", "WaveOut", "Asio" };

    public string AudioOutputMode
    {
        get => _config.AudioOutputMode;
        set
        {
            if (_config.AudioOutputMode != value)
            {
                _config.AudioOutputMode = value;
                OnPropertyChanged();
                SaveSettings();
                RefreshAvailableAudioOutputDevices();
                ApplyOutputDeviceNow();
            }
        }
    }

    public string? AudioOutputDeviceName
    {
        get => _config.AudioOutputDeviceName;
        set
        {
            if (_config.AudioOutputDeviceName != value)
            {
                _config.AudioOutputDeviceName = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
                SaveSettings();
                ApplyOutputDeviceNow();
            }
        }
    }

    /// <summary>Device/driver names for the currently-selected <see cref="AudioOutputMode"/> — empty for WaveOut, which has no per-device selection.</summary>
    public ObservableCollection<string> AvailableAudioOutputDevices { get; } = new();

    public ICommand RefreshAudioDevicesCommand => new RelayCommand(RefreshAvailableAudioOutputDevices);

    /// <summary>Switches what is playing right now onto the newly chosen device (off the UI thread — stopping a WASAPI output blocks briefly).</summary>
    private static void ApplyOutputDeviceNow()
    {
        if (Avalonia.Application.Current is not SLSKDONET.App app || app.Services == null) return;
        if (app.Services.GetService(typeof(SLSKDONET.Services.IAudioPlayerService)) is SLSKDONET.Services.IAudioPlayerService player)
            System.Threading.Tasks.Task.Run(player.ApplyOutputSettings);
    }

    public void RefreshAvailableAudioOutputDevices()
    {
        AvailableAudioOutputDevices.Clear();
        IEnumerable<string> names = AudioOutputMode switch
        {
            "WasapiShared" or "WasapiExclusive" => SLSKDONET.Services.Audio.AudioOutputProvider.GetWasapiDeviceNames(),
            "Asio" => SLSKDONET.Services.Audio.AudioOutputProvider.GetAsioDriverNames(),
            _ => Enumerable.Empty<string>(),
        };
        foreach (var name in names)
            AvailableAudioOutputDevices.Add(name);
    }

    public bool LoudnessNormalizationEnabled
    {
        get => _config.LoudnessNormalizationEnabled;
        set
        {
            if (_config.LoudnessNormalizationEnabled != value)
            {
                _config.LoudnessNormalizationEnabled = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public double LoudnessNormalizationTargetLufs
    {
        get => _config.LoudnessNormalizationTargetLufs;
        set
        {
            if (Math.Abs(_config.LoudnessNormalizationTargetLufs - value) > 0.001)
            {
                _config.LoudnessNormalizationTargetLufs = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    // Phase 8: Upgrade Scout
    public bool UpgradeScoutEnabled
    {
        get => _config.UpgradeScoutEnabled;
        set
        {
            if (_config.UpgradeScoutEnabled != value)
            {
                _config.UpgradeScoutEnabled = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int UpgradeMinBitrateThreshold
    {
        get => _config.UpgradeMinBitrateThreshold;
        set
        {
            if (_config.UpgradeMinBitrateThreshold != value)
            {
                _config.UpgradeMinBitrateThreshold = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int UpgradeMinGainKbps
    {
        get => _config.UpgradeMinGainKbps;
        set
        {
            if (_config.UpgradeMinGainKbps != value)
            {
                _config.UpgradeMinGainKbps = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool UpgradeAutoQueueEnabled
    {
        get => _config.UpgradeAutoQueueEnabled;
        set
        {
            if (_config.UpgradeAutoQueueEnabled != value)
            {
                _config.UpgradeAutoQueueEnabled = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool LibraryNavigationAutoHideEnabled
    {
        get => _config.LibraryNavigationAutoHideEnabled;
        set
        {
            if (_config.LibraryNavigationAutoHideEnabled != value)
            {
                _config.LibraryNavigationAutoHideEnabled = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int LibraryNavigationAutoHideActivationToggleCount
    {
        get => Math.Max(2, _config.LibraryNavigationAutoHideActivationToggleCount);
        set
        {
            var normalized = Math.Max(2, value);
            if (_config.LibraryNavigationAutoHideActivationToggleCount != normalized)
            {
                _config.LibraryNavigationAutoHideActivationToggleCount = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int MinBitrate
    {
        get => _config.PreferredMinBitrate;
        set
        {
                // Enforce minimum base kbps of 320
                var clampedValue = Math.Max(value, 320);
                if (_config.PreferredMinBitrate != clampedValue)
                {
                    _config.PreferredMinBitrate = clampedValue;
                SaveSettings();
            }
        }
    }

    public int MaxBitrate
    {
        get => _config.PreferredMaxBitrate;
        set
        {
            if (_config.PreferredMaxBitrate != value)
            {
                _config.PreferredMaxBitrate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchProfileNonStrict));
                OnPropertyChanged(nameof(SearchProfileStrict));
                OnPropertyChanged(nameof(SearchProfileStricter));
                OnPropertyChanged(nameof(SearchProfileModeText));
                SaveSettings();
            }
        }
    }

    public string PreferredFormats
    {
        get => string.Join(",", _config.PreferredFormats ?? new List<string>());
        set
        {
            _config.PreferredFormats = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SearchProfileNonStrict));
            OnPropertyChanged(nameof(SearchProfileStrict));
            OnPropertyChanged(nameof(SearchProfileStricter));
            OnPropertyChanged(nameof(SearchProfileModeText));
            SaveSettings();
        }
    }

    public bool SearchProfileNonStrict
    {
        get => IsNonStrictProfileActive();
        set
        {
            if (!value || _isApplyingSearchProfile)
                return;

            ApplySearchProfile("NonStrict");
        }
    }

    public bool SearchProfileStrict
    {
        get => IsStrictProfileActive();
        set
        {
            if (!value || _isApplyingSearchProfile)
                return;

            ApplySearchProfile("Strict");
        }
    }

    public bool SearchProfileStricter
    {
        get => IsStricterProfileActive();
        set
        {
            if (!value || _isApplyingSearchProfile)
                return;

            ApplySearchProfile("Stricter");
        }
    }

    public string SearchProfileModeText => SearchProfileStricter
        ? "STRICTER overwrite: FLAC-only + 701kbps floor"
        : SearchProfileStrict
            ? "STRICT overwrite: FLAC/WAV/AIFF/AIF + 320kbps floor"
            : "NON-STRICT overwrite: expanded formats + 192kbps floor";

    public bool EnableAutoDownloadStrictMode
    {
        get => _config.EnableAutoDownloadStrictMode;
        set
        {
            if (_config.EnableAutoDownloadStrictMode != value)
            {
                _config.EnableAutoDownloadStrictMode = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool EnableUpdateCheck
    {
        get => _config.EnableUpdateCheck;
        set
        {
            if (_config.EnableUpdateCheck != value)
            {
                _config.EnableUpdateCheck = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>True = Neon RGB palette, false = Classic RGB palette — two options, so a single toggle rather than a full picker.</summary>
    public bool WaveformUseNeonPalette
    {
        get => string.Equals(_config.WaveformPalette, "NeonRgb", StringComparison.OrdinalIgnoreCase);
        set
        {
            var newValue = value ? "NeonRgb" : "ClassicRgb";
            if (!string.Equals(_config.WaveformPalette, newValue, StringComparison.OrdinalIgnoreCase))
            {
                _config.WaveformPalette = newValue;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool WaveformShowEnergyCurve
    {
        get => _config.WaveformShowEnergyCurve;
        set
        {
            if (_config.WaveformShowEnergyCurve != value)
            {
                _config.WaveformShowEnergyCurve = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool WaveformShowVocalGhost
    {
        get => _config.WaveformShowVocalGhost;
        set
        {
            if (_config.WaveformShowVocalGhost != value)
            {
                _config.WaveformShowVocalGhost = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool WaveformShowPhraseSections
    {
        get => _config.WaveformShowPhraseSections;
        set
        {
            if (_config.WaveformShowPhraseSections != value)
            {
                _config.WaveformShowPhraseSections = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public float WaveformGain
    {
        get => _config.WaveformGain;
        set
        {
            var normalized = Math.Clamp(value, 0.5f, 2.0f);
            if (Math.Abs(_config.WaveformGain - normalized) > 0.001f)
            {
                _config.WaveformGain = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    // ── Waveform Appearance live preview ────────────────────────────────────
    // A small synthetic sample — not a real analyzed track — purely so the
    // Settings page can show what each toggle actually does instead of just
    // flipping an abstract switch. Built once and cached; every toggle above
    // drives this same instance via WaveformControl's own StyledProperties,
    // so no extra plumbing is needed here beyond the sample data itself.
    private WaveformAnalysisData? _waveformPreviewData;
    public WaveformAnalysisData WaveformPreviewData => _waveformPreviewData ??= BuildWaveformPreviewData();

    private IEnumerable<float>? _waveformPreviewEnergyCurve;
    public IEnumerable<float> WaveformPreviewEnergyCurve => _waveformPreviewEnergyCurve ??= BuildWaveformPreviewEnergyCurve();

    private IEnumerable<float>? _waveformPreviewVocalCurve;
    public IEnumerable<float> WaveformPreviewVocalCurve => _waveformPreviewVocalCurve ??= BuildWaveformPreviewVocalCurve();

    private List<PhraseSegment>? _waveformPreviewPhraseSegments;
    public IEnumerable<PhraseSegment> WaveformPreviewPhraseSegments => _waveformPreviewPhraseSegments ??= BuildWaveformPreviewPhraseSegments();

    private const int WaveformPreviewSampleCount = 300;
    private const double WaveformPreviewDurationSeconds = 30.0;

    private static WaveformAnalysisData BuildWaveformPreviewData()
    {
        var low = new byte[WaveformPreviewSampleCount];
        var mid = new byte[WaveformPreviewSampleCount];
        var high = new byte[WaveformPreviewSampleCount];
        var peak = new byte[WaveformPreviewSampleCount];
        var rms = new byte[WaveformPreviewSampleCount];

        for (var i = 0; i < WaveformPreviewSampleCount; i++)
        {
            var t = (double)i / WaveformPreviewSampleCount;
            // A gentle overall envelope (builds, drops, tails off) so the preview reads as a real
            // track shape rather than a flat noise band, plus distinct phase offsets per band so
            // the tri-band RGB blend actually shows visible color separation.
            var envelope = 0.35 + 0.65 * Math.Pow(Math.Sin(t * Math.PI), 0.6);

            low[i] = ToByte(envelope * (0.7 + 0.3 * Math.Sin(t * 18.0)));
            mid[i] = ToByte(envelope * (0.6 + 0.4 * Math.Sin(t * 30.0 + 1.2)));
            high[i] = ToByte(envelope * (0.5 + 0.5 * Math.Sin(t * 46.0 + 2.4)));
            peak[i] = Math.Max(low[i], Math.Max(mid[i], high[i]));
            rms[i] = ToByte(envelope * 0.6);
        }

        return new WaveformAnalysisData
        {
            PeakData = peak,
            RmsData = rms,
            LowData = low,
            MidData = mid,
            HighData = high,
            DurationSeconds = WaveformPreviewDurationSeconds,
            PointsPerSecond = (int)Math.Round(WaveformPreviewSampleCount / WaveformPreviewDurationSeconds)
        };

        static byte ToByte(double value) => (byte)Math.Clamp(value * 255.0, 0, 255);
    }

    private static IEnumerable<float> BuildWaveformPreviewEnergyCurve()
    {
        var curve = new float[WaveformPreviewSampleCount];
        for (var i = 0; i < WaveformPreviewSampleCount; i++)
        {
            var t = (double)i / WaveformPreviewSampleCount;
            curve[i] = (float)Math.Clamp(0.3 + 0.7 * Math.Pow(Math.Sin(t * Math.PI), 0.6), 0, 1);
        }
        return curve;
    }

    private static IEnumerable<float> BuildWaveformPreviewVocalCurve()
    {
        var curve = new float[WaveformPreviewSampleCount];
        for (var i = 0; i < WaveformPreviewSampleCount; i++)
        {
            var t = (double)i / WaveformPreviewSampleCount;
            // Vocal-density curve reads as "high vocal presence" when this value is low (see
            // WaveformControl's vocal-ghost rendering) — dip it in the back half of the preview.
            curve[i] = (float)Math.Clamp(0.7 - 0.6 * Math.Max(0, Math.Sin((t - 0.5) * Math.PI)), 0, 1);
        }
        return curve;
    }

    private static List<PhraseSegment> BuildWaveformPreviewPhraseSegments() => new()
    {
        new PhraseSegment { Label = "Intro", Start = 0f, Duration = 6f },
        new PhraseSegment { Label = "Build", Start = 6f, Duration = 6f },
        new PhraseSegment { Label = "Drop", Start = 12f, Duration = 10f },
        new PhraseSegment { Label = "Outro", Start = 22f, Duration = 8f },
    };

    public int AutoDownloadInitialWaitMs
    {
        get => _config.AutoDownloadInitialWaitMs;
        set
        {
            var normalized = Math.Clamp(value, 1000, 10000);
            if (_config.AutoDownloadInitialWaitMs != normalized)
            {
                _config.AutoDownloadInitialWaitMs = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int AutoDownloadExtendedWaitMs
    {
        get => _config.AutoDownloadExtendedWaitMs;
        set
        {
            var normalized = Math.Clamp(value, 5000, 60000);
            if (_config.AutoDownloadExtendedWaitMs != normalized)
            {
                _config.AutoDownloadExtendedWaitMs = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public string AutoDownloadAllowedExtensions
    {
        get => string.Join(",", _config.AutoDownloadAllowedExtensions ?? new List<string>());
        set
        {
            var parsed = SplitCsvList(value);
            if (!(_config.AutoDownloadAllowedExtensions ?? new List<string>()).SequenceEqual(parsed, StringComparer.OrdinalIgnoreCase))
            {
                _config.AutoDownloadAllowedExtensions = parsed;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int AutoDownloadMinFileSizeBytes
    {
        get => (int)Math.Clamp(_config.AutoDownloadMinFileSizeBytes, 256 * 1024, 5 * 1024 * 1024);
        set
        {
            var normalized = Math.Clamp(value, 256 * 1024, 5 * 1024 * 1024);
            if (_config.AutoDownloadMinFileSizeBytes != normalized)
            {
                _config.AutoDownloadMinFileSizeBytes = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int AutoDownloadMinBitrateKbps
    {
        get => _config.AutoDownloadMinBitrateKbps;
        set
        {
            var normalized = Math.Clamp(value, 128, 320);
            if (_config.AutoDownloadMinBitrateKbps != normalized)
            {
                _config.AutoDownloadMinBitrateKbps = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int AutoDownloadMinMatchScore
    {
        get => Math.Clamp(_config.AutoDownloadMinMatchScore, 0, 100);
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (_config.AutoDownloadMinMatchScore != normalized)
            {
                _config.AutoDownloadMinMatchScore = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool AutoDownloadExactFirstOnly
    {
        get => _config.AutoDownloadExactFirstOnly;
        set
        {
            if (_config.AutoDownloadExactFirstOnly != value)
            {
                _config.AutoDownloadExactFirstOnly = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool AutoDownloadAllowFuzzyFallback
    {
        get => _config.AutoDownloadAllowFuzzyFallback;
        set
        {
            if (_config.AutoDownloadAllowFuzzyFallback != value)
            {
                _config.AutoDownloadAllowFuzzyFallback = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int AutoDownloadDurationToleranceSeconds
    {
        get => Math.Clamp(_config.AutoDownloadDurationToleranceSeconds, 0, 30);
        set
        {
            var normalized = Math.Clamp(value, 0, 30);
            if (_config.AutoDownloadDurationToleranceSeconds != normalized)
            {
                _config.AutoDownloadDurationToleranceSeconds = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int AutoDownloadMaxCandidatesToScore
    {
        get => _config.AutoDownloadMaxCandidatesToScore;
        set
        {
            var normalized = Math.Clamp(value, 10, 200);
            if (_config.AutoDownloadMaxCandidatesToScore != normalized)
            {
                _config.AutoDownloadMaxCandidatesToScore = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public string AutoDownloadExcludedPhrases
    {
        get => _config.AutoDownloadExcludedPhrases ?? string.Empty;
        set
        {
            var normalized = string.Join(",", SplitCsvList(value));
            if (!string.Equals(_config.AutoDownloadExcludedPhrases ?? string.Empty, normalized, StringComparison.Ordinal))
            {
                _config.AutoDownloadExcludedPhrases = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool AutoDownloadDiagnosticsEnabled
    {
        get => _config.AutoDownloadDiagnosticsEnabled;
        set
        {
            if (_config.AutoDownloadDiagnosticsEnabled != value)
            {
                _config.AutoDownloadDiagnosticsEnabled = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int MinSearchDurationSeconds
    {
        get => _config.MinSearchDurationSeconds;
        set
        {
            var normalized = Math.Clamp(value, 1, 60);
            if (_config.MinSearchDurationSeconds != normalized)
            {
                _config.MinSearchDurationSeconds = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    private static List<string> SplitCsvList(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToList();
    }

    private void ApplySearchProfile(string mode)
    {
        if (_isApplyingSearchProfile)
            return;

        _isApplyingSearchProfile = true;
        try
        {
            if (string.Equals(mode, "Stricter", StringComparison.OrdinalIgnoreCase))
            {
                _config.PreferredFormats = StricterPreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                _config.PreferredMinBitrate = StricterMinBitrate;
                _config.PreferredMaxBitrate = StricterMaxBitrate;
                _config.SearchResponseLimit = StricterSearchResponseLimit;
                _config.SearchFileLimit = StricterSearchFileLimit;
                _config.MaxPeerQueueLength = StricterMaxPeerQueueLength;
            }
            else if (string.Equals(mode, "Strict", StringComparison.OrdinalIgnoreCase))
            {
                _config.PreferredFormats = StrictPreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                _config.PreferredMinBitrate = StrictMinBitrate;
                _config.PreferredMaxBitrate = StrictMaxBitrate;
                _config.SearchResponseLimit = StrictSearchResponseLimit;
                _config.SearchFileLimit = StrictSearchFileLimit;
                _config.MaxPeerQueueLength = StrictMaxPeerQueueLength;
            }
            else
            {
                _config.PreferredFormats = NonStrictPreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                _config.PreferredMinBitrate = NonStrictMinBitrate;
                _config.PreferredMaxBitrate = NonStrictMaxBitrate;
                _config.SearchResponseLimit = NonStrictSearchResponseLimit;
                _config.SearchFileLimit = NonStrictSearchFileLimit;
                _config.MaxPeerQueueLength = NonStrictMaxPeerQueueLength;
            }

            OnPropertyChanged(nameof(PreferredFormats));
            OnPropertyChanged(nameof(MinBitrate));
            OnPropertyChanged(nameof(MaxBitrate));
            OnPropertyChanged(nameof(SearchProfileNonStrict));
            OnPropertyChanged(nameof(SearchProfileStrict));
            OnPropertyChanged(nameof(SearchProfileStricter));
            OnPropertyChanged(nameof(SearchProfileModeText));
            SaveSettings();
        }
        finally
        {
            _isApplyingSearchProfile = false;
        }
    }

    private bool IsNonStrictProfileActive()
    {
        var formats = NormalizeFormats(_config.PreferredFormats);
        return formats.SetEquals(new HashSet<string>(new[] { "flac", "wav", "aiff", "aif", "mp3" }))
               && _config.PreferredMinBitrate <= NonStrictMinBitrate
               && _config.SearchResponseLimit >= NonStrictSearchResponseLimit
               && _config.SearchFileLimit >= NonStrictSearchFileLimit;
    }

    private bool IsStrictProfileActive()
    {
        var formats = NormalizeFormats(_config.PreferredFormats);
        return formats.SetEquals(new HashSet<string>(new[] { "flac", "wav", "aiff", "aif" }))
               && _config.PreferredMinBitrate >= StrictMinBitrate
               && _config.PreferredMinBitrate < StricterMinBitrate;
    }

    private bool IsStricterProfileActive()
    {
        var formats = NormalizeFormats(_config.PreferredFormats);
        return formats.Count == 1
               && formats.Contains("flac")
               && _config.PreferredMinBitrate >= StricterMinBitrate
               && _config.SearchResponseLimit <= StricterSearchResponseLimit
               && _config.SearchFileLimit <= StricterSearchFileLimit;
    }

    private static HashSet<string> NormalizeFormats(List<string>? formats)
    {
        return (formats ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .ToHashSet();
    }

    public bool SpotifyUseApi
    {
        get => _config.SpotifyUseApi;
        set
        {
            if (_config.SpotifyUseApi != value)
            {
                _config.SpotifyUseApi = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>
    /// The user's own Spotify app Client ID; empty means ORBIT's built-in one (which only serves
    /// accounts on its development-mode allowlist).
    /// </summary>
    public string SpotifyClientId
    {
        get => IsUsingBuiltInSpotifyClientId ? "" : _config.SpotifyClientId ?? "";
        set
        {
            var clientId = string.IsNullOrWhiteSpace(value) ? DefaultSpotifyClientId : value.Trim();
            if (clientId == _config.SpotifyClientId) return;

            _config.SpotifyClientId = clientId;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUsingBuiltInSpotifyClientId));
            SaveSettings();

            // Tokens belong to the app that issued them; the old session can't refresh under the new ID.
            if (_spotifyAuthService.IsAuthenticated)
                _ = SignOutAfterClientIdChangeAsync();
        }
    }

    public bool IsUsingBuiltInSpotifyClientId =>
        string.IsNullOrWhiteSpace(_config.SpotifyClientId) || _config.SpotifyClientId == DefaultSpotifyClientId;

    /// <summary>What to register as the Redirect URI in the user's own Spotify app.</summary>
    public string SpotifyRedirectUri => _config.SpotifyRedirectUri;

    private async Task SignOutAfterClientIdChangeAsync()
    {
        try
        {
            await _spotifyAuthService.SignOutAsync();
            _eventBus.Publish(new ToastRequestedEvent("Spotify Client ID changed",
                "Connect Spotify again to sign in with the new app.", NotificationType.Information, TimeSpan.FromSeconds(5)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signing out of Spotify after a Client ID change failed");
        }
    }

    private void OpenSpotifyDashboard()
    {
        const string url = "https://developer.spotify.com/dashboard/create";
        if (!SLSKDONET.Utils.ExternalUrlHelper.TryOpen(url))
        {
            _logger.LogWarning("Could not open the Spotify developer dashboard");
            _eventBus.Publish(new ToastRequestedEvent("Couldn't open browser", $"Open {url} manually.", NotificationType.Warning, TimeSpan.FromSeconds(6)));
        }
    }

    private async Task CopySpotifyRedirectUriAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(SpotifyRedirectUri);
                _eventBus.Publish(new ToastRequestedEvent("Redirect URI copied", SpotifyRedirectUri, NotificationType.Success, TimeSpan.FromSeconds(3)));
                return;
            }
            _eventBus.Publish(new ToastRequestedEvent("Clipboard unavailable", $"Type {SpotifyRedirectUri} into the Spotify dashboard.", NotificationType.Warning, TimeSpan.FromSeconds(6)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copying the Spotify redirect URI failed");
            _eventBus.Publish(new ToastRequestedEvent("Copy failed", $"Type {SpotifyRedirectUri} into the Spotify dashboard.", NotificationType.Warning, TimeSpan.FromSeconds(6)));
        }
    }
    
    public string SpotifyClientSecret
    {
        get => _config.SpotifyClientSecret ?? "";
        set { _config.SpotifyClientSecret = value; OnPropertyChanged(); SaveSettings(); }
    }

    public bool ClearSpotifyOnExit
    {
        get => _config.ClearSpotifyOnExit;
        set { _config.ClearSpotifyOnExit = value; OnPropertyChanged(); }
    }

    // Soulseek Connection Settings
    public string SoulseekUsername
    {
        get => _config.Username ?? "";
        set
        {
            if (_config.Username != value)
            {
                _config.Username = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool SoulseekAutoConnectEnabled
    {
        get => _config.AutoConnectEnabled;
        set
        {
            if (_config.AutoConnectEnabled != value)
            {
                _config.AutoConnectEnabled = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool SoulseekRememberPassword
    {
        get => _config.RememberPassword;
        set
        {
            if (_config.RememberPassword != value)
            {
                _config.RememberPassword = value;
                OnPropertyChanged();
                SaveSettings();
                // Clear stored credentials if remember password is disabled
                if (!value)
                {
                    _ = _credentialService.DeleteCredentialsAsync();
                }
            }
        }
    }

    // Brain 2.0 & Quality Guard
    public bool EnableFuzzyNormalization
    {
        get => _config.EnableFuzzyNormalization;
        set
        {
            if (_config.EnableFuzzyNormalization != value)
            {
                _config.EnableFuzzyNormalization = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool EnableRelaxationStrategy
    {
        get => _config.EnableRelaxationStrategy;
        set
        {
            if (_config.EnableRelaxationStrategy != value)
            {
                _config.EnableRelaxationStrategy = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool EnableVbrFraudDetection
    {
        get => _config.EnableVbrFraudDetection;
        set
        {
            if (_config.EnableVbrFraudDetection != value)
            {
                _config.EnableVbrFraudDetection = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public bool AutoRetryFailedDownloads
    {
        get => _config.AutoRetryFailedDownloads;
        set
        {
            if (_config.AutoRetryFailedDownloads != value)
            {
                _config.AutoRetryFailedDownloads = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int MaxDownloadRetries
    {
        get => _config.MaxDownloadRetries;
        set
        {
            if (_config.MaxDownloadRetries != value)
            {
                _config.MaxDownloadRetries = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    public int RelaxationTimeoutSeconds
    {
        get => _config.RelaxationTimeoutSeconds;
        set
        {
            if (_config.RelaxationTimeoutSeconds != value)
            {
                _config.RelaxationTimeoutSeconds = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    // Phase 0.10: Library Folders
    public ObservableCollection<LibraryFolderViewModel> LibraryFolders { get; } = new();

    private LibraryFolderViewModel? _selectedLibraryFolder;
    public LibraryFolderViewModel? SelectedLibraryFolder
    {
        get => _selectedLibraryFolder;
        set => SetProperty(ref _selectedLibraryFolder, value);
    }
    
    // Phase 2.4: Strategy Command Pattern
    public ObservableCollection<RankingStrategyViewModel> Strategies { get; } = new();

    private RankingStrategyViewModel? _selectedStrategy;
    public RankingStrategyViewModel? SelectedStrategy
    {
        get => _selectedStrategy;
        set
        {
            if (SetProperty(ref _selectedStrategy, value) && value != null)
            {
                ApplyStrategy(value.Id);
            }
        }
    }

    public ICommand SelectStrategyCommand { get; }

    // ── Safety Gate Properties (bound to SettingsPage toggles) ───────────

    public bool EnforceFileIntegrity
    {
        get => _config.SearchPolicy.EnforceFileIntegrity;
        set
        {
            if (_config.SearchPolicy.EnforceFileIntegrity == value) return;
            _config.SearchPolicy.EnforceFileIntegrity = value;
            OnPropertyChanged(nameof(EnforceFileIntegrity));
            SaveSettings();
        }
    }

    public bool EnforceStrictTitleMatch
    {
        get => _config.SearchPolicy.EnforceStrictTitleMatch;
        set
        {
            if (_config.SearchPolicy.EnforceStrictTitleMatch == value) return;
            _config.SearchPolicy.EnforceStrictTitleMatch = value;
            OnPropertyChanged(nameof(EnforceStrictTitleMatch));
            SaveSettings();
        }
    }

    public bool EnforceDurationMatch
    {
        get => _config.SearchPolicy.EnforceDurationMatch;
        set
        {
            if (_config.SearchPolicy.EnforceDurationMatch == value) return;
            _config.SearchPolicy.EnforceDurationMatch = value;
            OnPropertyChanged(nameof(EnforceDurationMatch));
            SaveSettings();
        }
    }

    private void InitializeStrategies()
    {
        Strategies.Add(new RankingStrategyViewModel
        {
            Id = "Quality First",
            Title = "Audiophile",
            Description = "Prioritizes lossless, high-bitrate, and perfect rips. No compromises.",
            IsSelected = _config.RankingProfile == "Quality First"
        });

        Strategies.Add(new RankingStrategyViewModel
        {
            Id = "Balanced",
            Title = "Balanced",
            Description = "The best mix of quality, speed, and metadata accuracy. Recommended.",
            IsSelected = _config.RankingProfile == "Balanced" || string.IsNullOrEmpty(_config.RankingProfile)
        });

        Strategies.Add(new RankingStrategyViewModel
        {
            Id = "DJ Mode",
            Title = "DJ Ready",
            Description = "Prioritizes BPM, Key, and mix-friendly files (Extended Mixes).",
            IsSelected = _config.RankingProfile == "DJ Mode"
        });

        // Set initial selection without triggering logic (already loaded from config)
        _selectedStrategy = Strategies.FirstOrDefault(s => s.IsSelected);
    }

    private void ExecuteSelectStrategy(RankingStrategyViewModel? strategy)
    {
        if (strategy == null) return;

        foreach (var s in Strategies) s.IsSelected = false;
        strategy.IsSelected = true;
        SelectedStrategy = strategy; // Triggers ApplyStrategy
    }

    private void ApplyStrategy(string strategyId)
    {
        _config.RankingProfile = strategyId;
        _logger.LogInformation("Applying Search Strategy: {Strategy}", strategyId);

        // Map ID to SearchPolicy
        if (strategyId == "Quality First") _config.SearchPolicy = SearchPolicy.QualityFirst();
        else if (strategyId == "DJ Mode") _config.SearchPolicy = SearchPolicy.DjReady();
        else 
        {
            // Balanced / Default
            _config.SearchPolicy = new SearchPolicy 
            { 
                Priority = SearchPriority.QualityFirst, 
                PreferredMinBitrate = 320,
                RelaxationParams = new() // Moderate relaxation
            };
        }

        SaveSettings();
    }


    // Unified State Management
    private SpotifyAuthStatus _spotifyState = SpotifyAuthStatus.Disconnected;
    public SpotifyAuthStatus SpotifyState
    {
        get => _spotifyState;
        set
        {
            if (SetProperty(ref _spotifyState, value))
            {
                UpdateDerivedProperties(value);
            }
        }
    }
    
    // Explicitly update all derived properties when state changes
    private void UpdateDerivedProperties(SpotifyAuthStatus newState)
    {
        IsSpotifyDisconnected = newState == SpotifyAuthStatus.Disconnected;
        IsSpotifyConnecting = newState == SpotifyAuthStatus.Connecting;
        IsSpotifyConnected = newState == SpotifyAuthStatus.Connected;
        
        // Refresh commands
        (ConnectSpotifyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisconnectSpotifyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RevokeAndReAuthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TestSpotifyConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestartSpotifyAuthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }


    // Helper properties for cleaner XAML bindings
    // We use explicit backing fields to ensure binding systems have concrete values to latch onto
    private bool _isSpotifyDisconnected = true;
    public bool IsSpotifyDisconnected
    {
        get => _isSpotifyDisconnected;
        set => SetProperty(ref _isSpotifyDisconnected, value);
    }

    private bool _isSpotifyConnecting;
    public bool IsSpotifyConnecting
    {
        get => _isSpotifyConnecting;
        set => SetProperty(ref _isSpotifyConnecting, value);
    }

    private bool _isSpotifyConnected;
    public bool IsSpotifyConnected
    {
        get => _isSpotifyConnected;
        set => SetProperty(ref _isSpotifyConnected, value);
    }



    private string _spotifyDisplayName = "Not Connected";
    public string SpotifyDisplayName
    {
        get => _spotifyDisplayName;
        set => SetProperty(ref _spotifyDisplayName, value);
    }

    private bool _isAuthenticating;
    // Remnants of old logic, kept private to drive the public Enum state
    // We map: IsAuthenticating=true -> Connecting
    //         IsAuthenticated=true -> Connected
    private DateTime _authStateSetAt = DateTime.MinValue;
    private CancellationTokenSource? _authWatchdogCts;
    private CancellationTokenSource? _connectCts; // Added for robust cancellation

    public bool IsAuthenticating
    {
        get => _isAuthenticating;
        set
        {
            if (_isAuthenticating == value)
                return;

            _logger.LogDebug("IsAuthenticating changing from {Old} to {New}", _isAuthenticating, value);
                
            if (SetProperty(ref _isAuthenticating, value))
            {
                if (value)
                {
                    SpotifyState = SpotifyAuthStatus.Connecting;
                    _authStateSetAt = DateTime.UtcNow;
                    StartAuthWatchdog();
                }
                else
                {
                    // When turning off authenticating, we must decide if we are connected or disconnected
                    // This is usually handled by UpdateSpotifyUIState, but as a fallback:
                    if (SpotifyState == SpotifyAuthStatus.Connecting)
                    {
                         // If we were connecting and stopped, but NOT connected, revert to Disconnected
                         // If we are actually connected, UpdateSpotifyUIState will override this shortly.
                         SpotifyState = _spotifyAuthService.IsAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;
                    }

                    _authWatchdogCts?.Cancel();
                    _authWatchdogCts = null;
                    _connectCts?.Cancel();
                }
            }
        }
    }

    private void StartAuthWatchdog()
    {
        try
        {
            _authWatchdogCts?.Cancel();
            _authWatchdogCts = new CancellationTokenSource();
            var token = _authWatchdogCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Increased timeout to 60 seconds to allow for slower user interaction in browser
                    await Task.Delay(TimeSpan.FromSeconds(60), token);
                    
                    if (!token.IsCancellationRequested && IsAuthenticating)
                    {
                        // Double check we haven't been cancelled in the microsecond between check and action
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (IsAuthenticating)
                            {
                                _logger.LogWarning("Auth UI watchdog: clearing stuck IsAuthenticating after 60s timeout");
                                IsAuthenticating = false;
                                SpotifyDisplayName = "Auth Timeout - Try Again";
                            }
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auth UI watchdog encountered an error");
                }
                finally
                {
                     // Cleanup if we finished naturally
                     if (_authWatchdogCts?.Token == token)
                     {
                         _authWatchdogCts = null;
                     }
                }
            }, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start auth watchdog");
        }
    }

    public ICommand SaveSettingsCommand { get; }
    public ICommand BrowseDownloadPathCommand { get; }
    public ICommand BrowseSharedFolderCommand { get; }
    public ICommand BrowseFrequentSourcesStagingPathCommand { get; }
    public ICommand ConnectSpotifyCommand { get; }
    public ICommand DisconnectSpotifyCommand { get; }
    public ICommand TestSpotifyConnectionCommand { get; }
    public ICommand ClearSpotifyCacheCommand { get; }
    public ICommand RevokeAndReAuthCommand { get; }
    public ICommand RestartSpotifyAuthCommand { get; }
    public ICommand CheckFfmpegCommand { get; } // Phase 8: Dependency validation
    public ICommand OpenFfmpegDownloadPageCommand { get; }
    public ICommand OpenSpotifyDashboardCommand { get; }
    public ICommand CopySpotifyRedirectUriCommand { get; }
    public ICommand ResetDatabaseCommand { get; }
    public ICommand BackupLibraryNowCommand { get; }
    public ICommand OpenBackupsFolderCommand { get; }
    public ICommand ResetToDefaultsCommand { get; }
    public ICommand ScanLibraryCommand { get; } // [NEW] Manual Scan
    public ICommand ReconcileLibraryCommand { get; }
    public ICommand FullLibrarySyncCommand { get; }
    public ICommand EnrichMissingMetadataCommand { get; }
    public ICommand RefreshRemovalCandidatesCommand { get; }
    public ICommand AddLibraryFolderCommand { get; }
    public ICommand RemoveLibraryFolderCommand { get; }

    public ObservableCollection<RemovalCandidateItemViewModel> RemovalCandidates { get; } = new();

    private string _removalCandidatesStatus = "No removal candidates yet.";
    public string RemovalCandidatesStatus
    {
        get => _removalCandidatesStatus;
        private set => SetProperty(ref _removalCandidatesStatus, value);
    }

    public bool HasRemovalCandidates => RemovalCandidates.Count > 0;

    // Scan State
    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }
    
    private string _scanStatus = "Idle";
    public string ScanStatus
    {
        get => _scanStatus;
        set => SetProperty(ref _scanStatus, value);
    }

    private string _enrichStatus = string.Empty;
    public string EnrichStatus
    {
        get => _enrichStatus;
        set => SetProperty(ref _enrichStatus, value);
    }

    private readonly EnrichmentViewModel? _enrichment;

    /// <summary>Settings → Connection → Metadata sources (null in design/test construction).</summary>
    public MetadataSourcesSettingsViewModel? MetadataSources { get; }
    public bool HasMetadataSources => MetadataSources != null;

    /// <summary>True while the library enrichment run (MetadataEnrichmentOrchestrator) works.</summary>
    public bool IsEnriching => _enrichment?.IsRunning == true;

    public bool CanEnrich => _enrichment != null && !_enrichment.IsRunning;

    private bool _isReconciling;
    public bool IsReconciling
    {
        get => _isReconciling;
        set => SetProperty(ref _isReconciling, value);
    }

    private string _reconcileStatus = string.Empty;
    public string ReconcileStatus
    {
        get => _reconcileStatus;
        set => SetProperty(ref _reconcileStatus, value);
    }

    private bool _isFullSyncing;
    public bool IsFullSyncing
    {
        get => _isFullSyncing;
        set => SetProperty(ref _isFullSyncing, value);
    }

    private string _fullSyncStatus = string.Empty;
    public string FullSyncStatus
    {
        get => _fullSyncStatus;
        set => SetProperty(ref _fullSyncStatus, value);
    }
    
    // Phase 8: FFmpeg Dependency State
    private bool _isFfmpegInstalled;
    public bool IsFfmpegInstalled
    {
        get => _isFfmpegInstalled;
        set => SetProperty(ref _isFfmpegInstalled, value);
    }

    private string _ffmpegStatus = "Checking...";
    public string FfmpegStatus
    {
        get => _ffmpegStatus;
        set => SetProperty(ref _ffmpegStatus, value);
    }

    private string _ffmpegVersion = "";
    public string FfmpegVersion
    {
        get => _ffmpegVersion;
        set => SetProperty(ref _ffmpegVersion, value);
    }

    // Phase 6: Security Audit Feed
    private const int AuditFeedMaxEntries = 100;
    private const int AdaptiveLaneHistoryMaxEntries = 10;
    public ObservableCollection<SecurityAuditEntryViewModel> SecurityAuditFeed { get; } = new();
    public ObservableCollection<AdaptiveLaneDecisionEntryViewModel> AdaptiveLaneDecisionHistory { get; } = new();
    private IDisposable? _securityAuditSubscription;
    private IDisposable? _adaptiveLaneStatusSubscription;
    private IDisposable? _searchPressureSubscription;

    // Phase E3: snapshot state
    private SearchPressureStatusEvent? _lastSearchPressure;
    private string _diagnosticsSnapshotStatus = "";
    public string DiagnosticsSnapshotStatus
    {
        get => _diagnosticsSnapshotStatus;
        private set
        {
            SetProperty(ref _diagnosticsSnapshotStatus, value);
            OnPropertyChanged(nameof(HasDiagnosticsSnapshotStatus));
        }
    }
    public bool HasDiagnosticsSnapshotStatus => !string.IsNullOrEmpty(_diagnosticsSnapshotStatus);

    // Network Activity Monitor: live feed of every outbound network call
    private const int NetworkActivityFeedMaxEntries = 300;
    public ObservableCollection<NetworkActivityEntryViewModel> NetworkActivityFeed { get; } = new();
    private IDisposable? _networkActivitySubscription;

    private int _soulseekCallsLast4Min;
    public int SoulseekCallsLast4Min
    {
        get => _soulseekCallsLast4Min;
        private set => SetProperty(ref _soulseekCallsLast4Min, value);
    }

    private int _httpCallsLast4Min;
    public int HttpCallsLast4Min
    {
        get => _httpCallsLast4Min;
        private set => SetProperty(ref _httpCallsLast4Min, value);
    }

    private int _socketConnectsLast4Min;
    public int SocketConnectsLast4Min
    {
        get => _socketConnectsLast4Min;
        private set => SetProperty(ref _socketConnectsLast4Min, value);
    }

    private static readonly TimeSpan NetworkActivityRateWindow = TimeSpan.FromMinutes(4);

    private void RefreshNetworkActivityRates()
    {
        if (_networkActivityMonitor == null) return;

        SoulseekCallsLast4Min = _networkActivityMonitor.CountSince(NetworkActivityRateWindow, "Soulseek");
        HttpCallsLast4Min = _networkActivityMonitor.CountSince(NetworkActivityRateWindow, "HTTP");
        SocketConnectsLast4Min = _networkActivityMonitor.CountSince(NetworkActivityRateWindow, "Socket");
    }

    private void OnNetworkActivityEvent(NetworkActivityEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            NetworkActivityFeed.Insert(0, new NetworkActivityEntryViewModel(e));
            while (NetworkActivityFeed.Count > NetworkActivityFeedMaxEntries)
                NetworkActivityFeed.RemoveAt(NetworkActivityFeed.Count - 1);

            RefreshNetworkActivityRates();
        });
    }

    // Engine Diagnostics: import/search audit trail (what a pasted line became, what was
    // searched, what candidates were evaluated, why a search did/didn't match)
    private const int EngineDiagnosticsFeedMaxEntries = 300;
    public ObservableCollection<EngineDiagnosticEntryViewModel> EngineDiagnosticsFeed { get; } = new();
    private IDisposable? _engineDiagnosticsSubscription;

    public string[] EngineDiagnosticsEventTypeFilters { get; } =
    {
        "All",
        EngineDiagnosticEventType.ImportLine,
        EngineDiagnosticEventType.TracksAddedToPlaylist,
        EngineDiagnosticEventType.SearchDispatched,
        EngineDiagnosticEventType.SearchCandidateEvaluated,
        EngineDiagnosticEventType.SearchResolved,
    };

    private string _engineDiagnosticsEventTypeFilter = "All";
    public string EngineDiagnosticsEventTypeFilter
    {
        get => _engineDiagnosticsEventTypeFilter;
        set
        {
            if (SetProperty(ref _engineDiagnosticsEventTypeFilter, value))
            {
                _ = RefreshEngineDiagnosticsFeedAsync();
            }
        }
    }

    private async System.Threading.Tasks.Task RefreshEngineDiagnosticsFeedAsync()
    {
        if (_engineDiagnosticsService == null) return;

        var filter = EngineDiagnosticsEventTypeFilter == "All" ? null : EngineDiagnosticsEventTypeFilter;
        var recent = await _engineDiagnosticsService.GetRecentAsync(filter, take: EngineDiagnosticsFeedMaxEntries);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EngineDiagnosticsFeed.Clear();
            foreach (var entry in recent)
                EngineDiagnosticsFeed.Add(new EngineDiagnosticEntryViewModel(entry));
        });
    }

    private void OnEngineDiagnosticEvent(EngineDiagnosticEvent e)
    {
        if (EngineDiagnosticsEventTypeFilter != "All" && EngineDiagnosticsEventTypeFilter != e.EventType)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            EngineDiagnosticsFeed.Insert(0, new EngineDiagnosticEntryViewModel(e));
            while (EngineDiagnosticsFeed.Count > EngineDiagnosticsFeedMaxEntries)
                EngineDiagnosticsFeed.RemoveAt(EngineDiagnosticsFeed.Count - 1);
        });
    }

    private void OnSecurityAuditEvent(SecurityAuditEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SecurityAuditFeed.Insert(0, new SecurityAuditEntryViewModel(e));
            while (SecurityAuditFeed.Count > AuditFeedMaxEntries)
                SecurityAuditFeed.RemoveAt(SecurityAuditFeed.Count - 1);
        });
    }

    private void OnAdaptiveLaneStatusEvent(AdaptiveLaneStatusEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AdaptiveLaneDecisionHistory.Insert(0, new AdaptiveLaneDecisionEntryViewModel(e));
            while (AdaptiveLaneDecisionHistory.Count > AdaptiveLaneHistoryMaxEntries)
                AdaptiveLaneDecisionHistory.RemoveAt(AdaptiveLaneDecisionHistory.Count - 1);
        });
    }

    private void OnSearchPressureStatusEvent(SearchPressureStatusEvent e)
    {
        _lastSearchPressure = e;
    }

    public ICommand ClearSecurityAuditCommand { get; private set; } = null!;
    public ICommand CopyDiagnosticsSnapshotCommand { get; private set; } = null!;

    // Phase 6: Live Share Status (bound in Settings page)
    private int    _shareFileCount;
    private string _shareReputationLabel = "Unknown";
    private IDisposable? _shareHealthSubscription;

    // Soulseek connection live state
    private IDisposable? _soulseekLifecycleSubscription;
    private bool _soulseekIsConnected;
    public bool SoulseekIsConnected
    {
        get => _soulseekIsConnected;
        private set
        {
            if (SetProperty(ref _soulseekIsConnected, value))
                OnPropertyChanged(nameof(SoulseekIsDisconnected));
        }
    }
    public bool SoulseekIsDisconnected => !_soulseekIsConnected;

    private string _soulseekConnectionStatusText = "";
    public string SoulseekConnectionStatusText
    {
        get => _soulseekConnectionStatusText;
        private set => SetProperty(ref _soulseekConnectionStatusText, value);
    }

    public int ShareFileCount
    {
        get => _shareFileCount;
        private set { _shareFileCount = value; OnPropertyChanged(); RaiseShareStatusChanged(); }
    }

    // Share health drives the status dot on the Settings page (Ellipse.status-dot + Classes.*).
    // Disabled sharing leaves all three false, which renders the neutral grey dot.
    public bool ShareIsEmpty   => EnableLibrarySharing && _shareFileCount == 0;
    public bool ShareIsLow     => EnableLibrarySharing && _shareFileCount is > 0 and < 500;
    public bool ShareIsHealthy => EnableLibrarySharing && _shareFileCount >= 500;

    private void RaiseShareStatusChanged()
    {
        OnPropertyChanged(nameof(ShareStatusSummary));
        OnPropertyChanged(nameof(ShareIsEmpty));
        OnPropertyChanged(nameof(ShareIsLow));
        OnPropertyChanged(nameof(ShareIsHealthy));
    }

    public string ShareStatusSummary => !EnableLibrarySharing
        ? "Sharing is disabled"
        : _shareFileCount == 0
            ? "No shared files detected — check the folder path"
            : $"{SLSKDONET.Utils.CountFormat.N(_shareFileCount)} files shared · {_shareReputationLabel}";

    public ICommand RefreshShareNowCommand { get; private set; } = null!;

    // ── Keyboard Mapping sub-ViewModel (Epic #119) ────────────────────────────
    public KeyboardMappingsViewModel KeyboardMappings { get; private set; } = null!;

    // Soulseek Connection Commands
    public ICommand SoulseekConnectCommand { get; private set; } = null!;
    public ICommand SoulseekDisconnectCommand { get; private set; } = null!;
    public ICommand SoulseekReconnectCommand { get; private set; } = null!;

    private void OnShareHealthUpdated(ShareHealthUpdatedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _shareReputationLabel = e.SharedFileCount == 0 ? "🔴 Critical" :
                                    e.SharedFileCount <  500 ? "🟡 Low" : "🟢 Healthy";
            ShareFileCount = e.SharedFileCount; // notifies all dependents
        });
    }

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        IFileInteractionService fileInteractionService,
        SpotifyAuthService spotifyAuthService,
        ISpotifyMetadataService spotifyMetadataService,
        DatabaseService databaseService,
        LibraryFolderScannerService libraryFolderScannerService,
        IEventBus eventBus,
        ISoulseekAdapter soulseek,
        ISoulseekCredentialService credentialService,
        IConnectionLifecycleService lifecycle,
        KeyboardMappingsViewModel keyboardMappings,
        IDbContextFactory<AppDbContext>? dbFactory = null,
        ILibraryService? libraryService = null,
        IDialogService? dialogService = null,
        NetworkActivityMonitor? networkActivityMonitor = null,
        EngineDiagnosticsService? engineDiagnosticsService = null,
        ILibraryImportLauncher? importLauncher = null,
        EnrichmentViewModel? enrichment = null,
        MetadataSourcesSettingsViewModel? metadataSources = null)
    {
        _logger = logger;
        _config = config;
        _enrichment = enrichment;
        MetadataSources = metadataSources;
        if (_enrichment != null)
            _enrichment.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(EnrichmentViewModel.IsRunning) or nameof(EnrichmentViewModel.NoticeText))
                {
                    OnPropertyChanged(nameof(CanEnrich));
                    OnPropertyChanged(nameof(IsEnriching));
                    EnrichStatus = _enrichment.IsRunning ? _enrichment.NoticeText + " · progress on the Analysis page" : _enrichment.NoticeText;
                    (EnrichMissingMetadataCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            };
        _importLauncher = importLauncher;
        _networkActivityMonitor = networkActivityMonitor;
        _engineDiagnosticsService = engineDiagnosticsService;
        _configManager = configManager;
        _fileInteractionService = fileInteractionService;
        _spotifyAuthService = spotifyAuthService;
        _spotifyMetadataService = spotifyMetadataService;
        _databaseService = databaseService;
        _libraryFolderScannerService = libraryFolderScannerService;
        _eventBus = eventBus;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _dbFactory = dbFactory;
        _lifecycle = lifecycle;
        _libraryService = libraryService;
        KeyboardMappings = keyboardMappings;
        _dialogService = dialogService;

        // Ensure default Client ID is set if empty
        if (string.IsNullOrEmpty(_config.SpotifyClientId))
        {
            _config.SpotifyClientId = DefaultSpotifyClientId;
            // Clear secret if we are setting the public ID, as PKCE doesn't use it
            _config.SpotifyClientSecret = ""; 
        }

        SaveSettingsCommand = new RelayCommand(SaveSettingsWithConfirmation);
        BrowseDownloadPathCommand = new AsyncRelayCommand(BrowseDownloadPathAsync);
        BrowseSharedFolderCommand = new AsyncRelayCommand(BrowseSharedFolderAsync);
        BrowseFrequentSourcesStagingPathCommand = new AsyncRelayCommand(BrowseFrequentSourcesStagingPathAsync);

        ConnectSpotifyCommand = new AsyncRelayCommand(ConnectSpotifyAsync, () => IsSpotifyDisconnected);
        DisconnectSpotifyCommand = new AsyncRelayCommand(DisconnectSpotifyAsync, () => IsSpotifyConnected);
        TestSpotifyConnectionCommand = new AsyncRelayCommand(TestSpotifyConnectionAsync); // Always allow testing if user expands advanced
        ClearSpotifyCacheCommand = new AsyncRelayCommand(ClearSpotifyCacheAsync);
        RevokeAndReAuthCommand = new AsyncRelayCommand(RevokeAndReAuthAsync);
        CheckFfmpegCommand = new AsyncRelayCommand(CheckFfmpegAsync); // Phase 8
        OpenFfmpegDownloadPageCommand = new RelayCommand(OpenFfmpegDownloadPage);
        OpenSpotifyDashboardCommand = new RelayCommand(OpenSpotifyDashboard);
        CopySpotifyRedirectUriCommand = new AsyncRelayCommand(CopySpotifyRedirectUriAsync);
        RestartSpotifyAuthCommand = new AsyncRelayCommand(RestartSpotifyAuthAsync, () => IsSpotifyConnecting);
        ResetDatabaseCommand = new AsyncRelayCommand(ResetDatabaseAsync);
        BackupLibraryNowCommand = new AsyncRelayCommand(BackupLibraryNowAsync);
        OpenBackupsFolderCommand = new RelayCommand(OpenBackupsFolder);
        ResetToDefaultsCommand = new AsyncRelayCommand(ResetToDefaultsAsync);
        ScanLibraryCommand = new AsyncRelayCommand(ScanLibraryAsync, () => !IsScanning && !IsFullSyncing);
        ReconcileLibraryCommand = new AsyncRelayCommand(ReconcileLibraryAsync, () => !IsReconciling && !IsFullSyncing);
        FullLibrarySyncCommand = new AsyncRelayCommand(FullLibrarySyncAsync, () => !IsFullSyncing && !IsScanning && !IsReconciling);
        EnrichMissingMetadataCommand = new AsyncRelayCommand(EnrichMissingMetadataAsync, () => CanEnrich);
        RefreshRemovalCandidatesCommand = new AsyncRelayCommand(LoadRemovalCandidatesAsync);
        AddLibraryFolderCommand = new AsyncRelayCommand(AddLibraryFolderAsync);
        RemoveLibraryFolderCommand = new AsyncRelayCommand(RemoveLibraryFolderAsync, () => SelectedLibraryFolder != null);
        ClearSecurityAuditCommand = new RelayCommand(() => SecurityAuditFeed.Clear());
        CopyDiagnosticsSnapshotCommand = new AsyncRelayCommand(CopyDiagnosticsSnapshotAsync);
        RefreshShareNowCommand = new AsyncRelayCommand(async () =>
        {
            try
            {
                await _soulseek.RefreshShareStateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Manual share refresh failed. This is non-fatal and can happen while reconnecting.");
            }
        });

        // Soulseek Connection Commands
        SoulseekConnectCommand = new AsyncRelayCommand(SoulseekConnectAsync, () => !_soulseek.IsConnected);
        SoulseekDisconnectCommand = new RelayCommand(SoulseekDisconnect, () => _soulseek.IsConnected);
        SoulseekReconnectCommand = new AsyncRelayCommand(SoulseekReconnectAsync, () => _soulseek.IsConnected);

        // Initialize connected state and subscribe to lifecycle events
        SoulseekIsConnected = _soulseek.IsConnected;
        _soulseekLifecycleSubscription = _eventBus
            .GetEvent<ConnectionLifecycleStateChangedEvent>()
            .Subscribe(OnSoulseekLifecycleChanged);

        // Subscribe to live share health updates
        _shareHealthSubscription = _eventBus.GetEvent<ShareHealthUpdatedEvent>().Subscribe(OnShareHealthUpdated);

        // Explicitly initialize IsAuthenticating to false
        IsAuthenticating = false;

        // Fix: Subscribe to authentication changes from the service
        _spotifyAuthService.AuthenticationChanged += OnSpotifyAuthenticationChanged;

        // Set initial display based on current auth state
        UpdateSpotifyUIState(_spotifyAuthService.IsAuthenticated);

        SelectStrategyCommand = new RelayCommand<RankingStrategyViewModel?>(ExecuteSelectStrategy);
        InitializeStrategies();

        _libraryFoldersSubscription = _eventBus.GetEvent<LibraryFoldersChangedEvent>().Subscribe(e => { _ = LoadLibraryFoldersAsync(); });

        // Phase 6: Security Audit Feed subscription
        _securityAuditSubscription = _eventBus.GetEvent<SecurityAuditEvent>().Subscribe(OnSecurityAuditEvent);

        // Network Activity Monitor: seed from existing history (Subject<T> doesn't replay), then subscribe for live updates
        if (_networkActivityMonitor != null)
        {
            foreach (var entry in _networkActivityMonitor.GetRecentActivity(NetworkActivityFeedMaxEntries))
                NetworkActivityFeed.Add(new NetworkActivityEntryViewModel(entry));
            RefreshNetworkActivityRates();
        }
        _networkActivitySubscription = _eventBus.GetEvent<NetworkActivityEvent>().Subscribe(OnNetworkActivityEvent);
        _engineDiagnosticsSubscription = _eventBus.GetEvent<EngineDiagnosticEvent>().Subscribe(OnEngineDiagnosticEvent);
        _adaptiveLaneStatusSubscription = _eventBus.GetEvent<AdaptiveLaneStatusEvent>().Subscribe(OnAdaptiveLaneStatusEvent);
        _searchPressureSubscription = _eventBus.GetEvent<SearchPressureStatusEvent>().Subscribe(OnSearchPressureStatusEvent);
        
        // Force update of derived properties to ensure UI booleans are in sync with SpotifyState
        UpdateDerivedProperties(SpotifyState);
    }

    /// <summary>
    /// Runs the device-enumeration/FFmpeg/library-folder/removal-candidate/AI-engine checks that
    /// used to fire unconditionally from the constructor. SettingsViewModel is a DI singleton
    /// constructed eagerly at app startup (it's a MainViewModel constructor parameter), so doing
    /// this work there blocked the UI thread before the main window ever appeared. Call this once,
    /// from SettingsPage's constructor, so it only runs on first navigation to Settings instead.
    /// </summary>
    public void EnsureInitialized()
    {
        if (_isInitialized) return;
        _isInitialized = true;


        try { RefreshAvailableAudioOutputDevices(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to enumerate audio output devices on Settings load"); }

        _ = CheckFfmpegAsync(); // Phase 8: Check FFmpeg on startup
        _ = LoadLibraryFoldersAsync(); // Phase 0.10
        _ = LoadRemovalCandidatesAsync();
        _ = RefreshEngineDiagnosticsFeedAsync();
    }

    /// <summary>
    /// Synchronizes the ViewModel state with the SpotifyAuthService authentication state.
    /// Uses the UI thread dispatcher to ensure thread safety during background updates.
    /// </summary>
    private void OnSpotifyAuthenticationChanged(object? sender, bool isAuthenticated)
    {
        // Ensure this always runs on the UI thread
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            // CRITICAL FIX: Ensure we clear the 'IsAuthenticating' lock when we get a definitive state update.
            // This prevents the UI from being stuck in a disabled state if a previous attempt hung.
            if (IsAuthenticating)
            {
                 _logger.LogInformation("Authentication state changed to {State} while IsAuthenticating was true - clearing lock.", isAuthenticated);
                 IsAuthenticating = false;
            }

            UpdateSpotifyUIState(isAuthenticated);
            _logger.LogInformation("Spotify UI state synchronized via event: {State}", 
                isAuthenticated ? "Connected" : "Disconnected");
        });
    }


    /// <summary>
    private void UpdateSpotifyUIState(bool isAuthenticated)
    {
        // Source of True Truth
        SpotifyState = isAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;
        
        SpotifyDisplayName = isAuthenticated ? "Connected" : "Not Connected";
        
        if (isAuthenticated)
        {
            SpotifyUseApi = true;
        }
    }

    /// <summary>
    /// Phase 8: Enhanced FFmpeg dependency checker with timeout, stderr capture, and fallback paths.
    /// </summary>
    /// <summary>Also used by the setup wizard's Audio &amp; analysis step.</summary>
    public async Task CheckFfmpegAsync()
    {
        try
        {
            FfmpegStatus = "Checking...";
            
            // Try standard PATH lookup first
            var (success, version) = await TryFfmpegCommandAsync("ffmpeg");
            
            if (!success)
            {
                // Fallback: Check common install directories (Windows-specific)
                if (OperatingSystem.IsWindows())
                {
                    var commonPaths = new[]
                    {
                        // ORBIT's own managed install (FfmpegInstallerService), checked first.
                        SLSKDONET.Services.Platform.FfmpegInstallerService.ManagedFfmpegPath,
                        @"C:\ffmpeg\bin\ffmpeg.exe",
                        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", "ffmpeg.exe")
                    };
                    
                    foreach (var path in commonPaths)
                    {
                        if (File.Exists(path))
                        {
                            (success, version) = await TryFfmpegCommandAsync(path);
                            if (success)
                            {
                                _logger.LogInformation("FFmpeg found via fallback path: {Path}", path);
                                break;
                            }
                        }
                    }
                }
            }
            
            if (success)
            {
                IsFfmpegInstalled = true;
                FfmpegVersion = version;
                FfmpegStatus = $"✅ Installed (v{version})";
                
                // Update global config
                _config.IsFfmpegAvailable = true;
                _config.FfmpegVersion = version;
                _configManager.Save(_config);
                
                _logger.LogInformation("FFmpeg validation successful: v{Version}", version);
            }
            else
            {
                IsFfmpegInstalled = false;
                FfmpegStatus = "❌ Not Found in PATH";
                
                // Update global config
                _config.IsFfmpegAvailable = false;
                _config.FfmpegVersion = "";
                _configManager.Save(_config);
                
                _logger.LogWarning("FFmpeg not found. Sonic Integrity features will be disabled.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg validation failed unexpectedly");
            IsFfmpegInstalled = false;
            FfmpegStatus = "❌ Check Failed";
        }
        
        OnPropertyChanged(nameof(IsFfmpegInstalled));
        OnPropertyChanged(nameof(FfmpegStatus));
        OnPropertyChanged(nameof(FfmpegVersion));
    }

    /// <summary>
    /// Attempts to run ffmpeg -version with timeout and captures stderr (where FFmpeg prints version info).
    /// </summary>
    private async Task<(bool success, string version)> TryFfmpegCommandAsync(string ffmpegPath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5-second timeout
        
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, // FFmpeg writes to stderr!
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };
            
            var outputBuilder = new System.Text.StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            
            process.EnableRaisingEvents = true;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            
            await process.WaitForExitAsync(cts.Token);
            
            if (process.ExitCode == 0)
            {
                var output = outputBuilder.ToString();
                
                // Parse version: "ffmpeg version 6.0.1-full_build-www.gyan.dev" or "ffmpeg version N-109688-g5...github.com/BtbN/FFmpeg-Builds"
                var match = System.Text.RegularExpressions.Regex.Match(output, @"ffmpeg version (\d+(\.\d+)+)");
                var version = match.Success ? match.Groups[1].Value : "unknown";
                
                return (true, version);
            }
            
            return (false, "");
        }
        catch (System.ComponentModel.Win32Exception) // File not found
        {
            return (false, "");
        }
        catch (OperationCanceledException) // Timeout
        {
            _logger.LogWarning("FFmpeg command timed out after 5 seconds at path: {Path}", ffmpegPath);
            return (false, "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute FFmpeg at path: {Path}", ffmpegPath);
            return (false, "");
        }
    }
    /// <summary>Spotify OAuth sign-in (browser). Also used by the setup wizard's Connections step.</summary>
    public async Task ConnectSpotifyAsync()
    {
        // Cancel any previous attempts to free up the port
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        try
        {
            IsAuthenticating = true;
            
            // Ensure config is saved first so the service uses the correct Client ID
            _configManager.Save(_config);

            var success = await _spotifyAuthService.StartAuthorizationAsync(_connectCts.Token);
            
            if (success)
            {
                // Update display based on new auth state
                SpotifyState = _spotifyAuthService.IsAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;
                SpotifyDisplayName = IsSpotifyConnected ? "Connected" : "Not Connected";
                SpotifyUseApi = true; // Auto-enable API usage on success
                _config.SpotifyUseApi = true; // Ensure backing field is also set
                _configManager.Save(_config); // Save the enabled state
            }
        }

        catch (SLSKDONET.Services.ImportProviders.SpotifyAccountNotAllowlistedException)
        {
            // Already logged with the full explanation by SpotifyAuthService.
            SpotifyDisplayName = "This Spotify account isn't allowlisted for this Client ID";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Spotify connection flow cancelled");
            SpotifyDisplayName = "Cancelled";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "Spotify connection timed out");
            SpotifyDisplayName = "Timeout - Try again";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Port") || ex.Message.Contains("port"))
        {
            _logger.LogError(ex, "Port conflict during Spotify connection");
            SpotifyDisplayName = "Port conflict - Restart app";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify connection failed");
            SpotifyDisplayName = $"Error: {ex.Message.Substring(0, Math.Min(30, ex.Message.Length))}...";
            SpotifyState = SpotifyAuthStatus.Disconnected;
        }
        finally
        {
            IsAuthenticating = false;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    private async Task DisconnectSpotifyAsync()
    {
        await _spotifyAuthService.SignOutAsync();
        SpotifyState = SpotifyAuthStatus.Disconnected;
        SpotifyDisplayName = "Not Connected";
        SpotifyUseApi = false; // Optional: Auto-disable? Maybe let user decide.
    }

    private async Task TestSpotifyConnectionAsync()
    {
        try
        {
            IsAuthenticating = true;
            _logger.LogInformation("Testing Spotify connection...");

            await _spotifyAuthService.VerifyConnectionAsync();
            var stillAuthenticated = _spotifyAuthService.IsAuthenticated;

            SpotifyState = stillAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;
            SpotifyDisplayName = stillAuthenticated ? "Connected" : "Not Connected";

            if (!stillAuthenticated)
            {
                _logger.LogWarning("Spotify test failed; clearing cached credentials for a clean retry");
                await _spotifyAuthService.ClearCachedCredentialsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify connection test failed");
        }
        finally
        {
            IsAuthenticating = false;
            (TestSpotifyConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Restores every setting on this page to its compiled default and saves immediately. Only
    /// narrowly-scoped resets existed before this (DB schema reset, Spotify full re-auth) — there
    /// was no single action to undo general customization.
    /// Mutates the existing AppConfig instance in place (reflection over its public settable
    /// properties) rather than swapping the reference, since other services/ViewModels hold the
    /// same shared instance and should see the reset immediately too.
    /// </summary>
    private async Task ResetToDefaultsAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(
            "Reset to Defaults",
            "This resets every setting on this page back to its default value and saves immediately. This cannot be undone. Continue?",
            confirmLabel: "Reset",
            cancelLabel: "Cancel");

        if (!confirmed) return;

        var defaults = new AppConfig();
        foreach (var prop in typeof(AppConfig).GetProperties())
        {
            if (prop.CanWrite && prop.CanRead)
            {
                prop.SetValue(_config, prop.GetValue(defaults));
            }
        }

        SaveSettings();
        OnPropertyChanged(string.Empty); // Refresh every binding on the page — not just one property.
        _logger.LogInformation("Settings reset to defaults");
    }

    // Every setting auto-saves on change (60+ call sites), so only the explicit Save button confirms
    // with a toast; a failure is reported from any path, since a silently unsaved setting is worse.
    private bool SaveSettings()
    {
        try
        {
            _configManager.Save(_config);
            _ = ApplySoulseekRuntimeConfigurationAsync();
            _logger.LogInformation("Settings saved");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            _eventBus.Publish(new ToastRequestedEvent(
                "Settings not saved", $"Could not write the settings file: {ex.Message}", NotificationType.Error, TimeSpan.FromSeconds(8)));
            return false;
        }
    }

    private void SaveSettingsWithConfirmation()
    {
        if (SaveSettings())
            _eventBus.Publish(new ToastRequestedEvent("Settings saved", "Your settings were saved.", NotificationType.Success, TimeSpan.FromSeconds(3)));
    }

    private async Task ApplySoulseekRuntimeConfigurationAsync()
    {
        try
        {
            await _soulseek.ApplyRuntimeNetworkConfigurationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply Soulseek runtime network configuration after settings save.");
        }
    }

    public void OpenFfmpegDownloadPage()
    {
        const string url = "https://ffmpeg.org/download.html";
        if (!SLSKDONET.Utils.ExternalUrlHelper.TryOpen(url))
        {
            _logger.LogWarning("Could not open the FFmpeg download page");
            _eventBus.Publish(new ToastRequestedEvent("Couldn't open browser", $"Open {url} manually to download FFmpeg.", NotificationType.Warning, TimeSpan.FromSeconds(6)));
        }
    }

    private async Task BrowseDownloadPathAsync()
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Download Folder");
        if (!string.IsNullOrEmpty(path))
        {
            DownloadPath = path; // Setter triggers SaveSettings
        }
    }

    private async Task BrowseSharedFolderAsync()
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Shared Folder");
        if (!string.IsNullOrEmpty(path))
        {
            SharedFolderPath = path; // Setter triggers SaveSettings
        }
    }

    private async Task BrowseFrequentSourcesStagingPathAsync()
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Frequent Sources Staging Folder");
        if (!string.IsNullOrWhiteSpace(path))
        {
            FrequentSourcesStagingPath = path; // Setter triggers SaveSettings
        }
    }

    private async Task ClearSpotifyCacheAsync()
    {
        try
        {
            await _spotifyMetadataService.ClearCacheAsync();
            // Optional: NotificationService usage here if available, for now just log
            _logger.LogInformation("Cache cleared via Settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache via Settings");
        }
    }

    /// <summary>
    /// "Enrich library metadata": queues every LibraryEntries and download track still missing cover
    /// art / album / label / year / genre into the background enrichment pipeline (file tags,
    /// Deezer, MusicBrainz, … per Settings → Connection → Metadata sources). Replaces the old
    /// 100-PlaylistTracks-per-click Spotify loop that never touched folder-imported tracks.
    /// </summary>
    private async Task EnrichMissingMetadataAsync()
    {
        if (_enrichment == null)
        {
            EnrichStatus = "Library enrichment is unavailable.";
            _eventBus.Publish(new ToastRequestedEvent("Enrich library", EnrichStatus, NotificationType.Error, null));
            return;
        }
        try
        {
            await _enrichment.StartAsync();
            EnrichStatus = _enrichment.IsRunning ? "Running in the background · progress on the Analysis page" : _enrichment.NoticeText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start library enrichment");
            EnrichStatus = "Could not start: " + ex.Message;
            _eventBus.Publish(new ToastRequestedEvent("Enrich library", EnrichStatus, NotificationType.Error, null));
        }
        finally
        {
            OnPropertyChanged(nameof(CanEnrich));
            ((AsyncRelayCommand)EnrichMissingMetadataCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// "Scan library": registers the download/shared folders, then runs the import sheet over every
    /// Library Source (phases, counters and a summary live there, not in this status line).
    /// </summary>
    private async Task ScanLibraryAsync()
    {
        if (IsScanning) return;
        try
        {
            IsScanning = true;
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            ScanStatus = "Scanning — see the import sheet";

            await EnsureConfiguredFoldersRegisteredAsync();
            var result = await RunImportSheetAsync("Scanning your library folders");
            ScanStatus = DescribeImport(result);
            await LoadRemovalCandidatesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual scan failed");
            ScanStatus = "Scan failed — see logs.";
        }
        finally
        {
            IsScanning = false;
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The download directory and Soulseek shared folder are always Library Sources.</summary>
    private async Task EnsureConfiguredFoldersRegisteredAsync()
    {
        if (!string.IsNullOrEmpty(_config.DownloadDirectory))
            await _libraryFolderScannerService.EnsureDefaultFolderAsync(_config.DownloadDirectory);
        else
            _logger.LogWarning("No download directory configured for scanning.");

        if (!string.IsNullOrEmpty(_config.SharedFolderPath) &&
            !string.Equals(_config.SharedFolderPath, _config.DownloadDirectory, StringComparison.OrdinalIgnoreCase))
            await _libraryFolderScannerService.EnsureDefaultFolderAsync(_config.SharedFolderPath);
    }

    /// <summary>Runs the import sheet when the shell provides it; tests and headless hosts import directly.</summary>
    private async Task<LibraryImportResult?> RunImportSheetAsync(string title)
    {
        if (_importLauncher != null)
            return await _importLauncher.RunImportAsync(null, title);
        return await _libraryFolderScannerService.ImportFoldersAsync(new LibraryImportRequest { Trigger = LibraryImportTrigger.Maintenance });
    }

    internal static string DescribeImport(LibraryImportResult? result) => result switch
    {
        null => "Another import is running. Its progress is in the import sheet.",
        { Error: not null } => $"Scan stopped: {result.Error}",
        { WasCancelled: true } => $"Scan cancelled. {SLSKDONET.Utils.CountFormat.N(result.Added)} new tracks kept.",
        _ => $"Scan complete: {SLSKDONET.Utils.CountFormat.N(result.Added)} new, {SLSKDONET.Utils.CountFormat.N(result.AlreadyInLibrary + result.Duplicates)} already known, {SLSKDONET.Utils.CountFormat.N(result.ProblemCount)} not added.",
    };

    private async Task ReconcileLibraryAsync()
    {
        if (IsReconciling || _libraryService == null) return;
        try
        {
            IsReconciling = true;
            ReconcileStatus = "Checking...";
            (ReconcileLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            var (reset, checked_, relinked) = await _libraryService.ReconcileLibraryAsync();

            ReconcileStatus = (reset, relinked) switch
            {
                (0, 0) => $"All {checked_} files verified present.",
                (0, > 0) => $"Relinked {relinked} moved/renamed file(s) (checked {checked_}).",
                (> 0, 0) => $"Reset {reset} missing file(s) to re-download queue (checked {checked_}).",
                _ => $"Relinked {relinked} moved file(s), reset {reset} truly missing to re-download queue (checked {checked_})."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library reconciliation failed");
            ReconcileStatus = "Reconciliation failed — see logs.";
        }
        finally
        {
            IsReconciling = false;
            (ReconcileLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task FullLibrarySyncAsync()
    {
        if (IsFullSyncing || IsScanning || IsReconciling || _libraryService == null) return;
        try
        {
            IsFullSyncing = true;
            FullSyncStatus = "Step 1/2: Scanning all folders...";
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReconcileLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

            await EnsureConfiguredFoldersRegisteredAsync();
            var import = await RunImportSheetAsync("Full library sync: scanning folders");
            if (import == null)
            {
                FullSyncStatus = "Another import is running. Try again when it finishes.";
                return;
            }
            int totalImported = import.Added;
            int totalUpgraded = import.Upgraded;

            FullSyncStatus = "Step 2/2: Reconciling file paths...";
            var (reset, checked_, relinked) = await _libraryService.ReconcileLibraryAsync();

            await LoadRemovalCandidatesAsync();

            var reconcileSummary = (reset, relinked) switch
            {
                (0, 0) => $"All {checked_} files verified.",
                (0, > 0) => $"Relinked {relinked} moved file(s).",
                (> 0, 0) => $"Reset {reset} missing file(s).",
                _ => $"Relinked {relinked}, reset {reset} missing file(s)."
            };
            FullSyncStatus = $"Done. Imported {totalImported} | Upgraded {totalUpgraded} | {reconcileSummary}";

            _logger.LogInformation("Full library sync complete. Imported: {Imported}, Upgraded: {Upgraded}, Reset: {Reset}", totalImported, totalUpgraded, reset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Full library sync failed");
            FullSyncStatus = "Full sync failed — see logs.";
        }
        finally
        {
            IsFullSyncing = false;
            (FullLibrarySyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReconcileLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task LoadRemovalCandidatesAsync()
    {
        try
        {
            await using var context = _dbFactory != null ? _dbFactory.CreateDbContext() : new AppDbContext();
            var logs = await context.LibraryActionLogs
                .AsNoTracking()
                .Where(l => l.ActionType == LibraryActionType.Consolidate)
                .OrderByDescending(l => l.Timestamp)
                .Take(500)
                .ToListAsync();

            var latestBySource = new Dictionary<string, LibraryActionLogEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var log in logs)
            {
                if (string.IsNullOrWhiteSpace(log.SourcePath) || string.IsNullOrWhiteSpace(log.DestinationPath))
                {
                    continue;
                }

                if (!latestBySource.ContainsKey(log.SourcePath))
                {
                    latestBySource[log.SourcePath] = log;
                }
            }

            var items = latestBySource.Values
                .OrderByDescending(v => v.Timestamp)
                .Take(200)
                .Select(v => new RemovalCandidateItemViewModel(
                    sourcePath: v.SourcePath,
                    preferredPath: v.DestinationPath,
                    trackLabel: BuildTrackLabel(v.TrackArtist, v.TrackTitle),
                    timestampUtc: v.Timestamp,
                    openPathAction: OpenPathInExplorer))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RemovalCandidates.Clear();
                foreach (var item in items)
                {
                    RemovalCandidates.Add(item);
                }

                RemovalCandidatesStatus = items.Count == 0
                    ? "No removal candidates yet. Run a scan to detect lower-quality duplicates."
                    : $"{items.Count} removal candidate(s) ready for review.";

                OnPropertyChanged(nameof(HasRemovalCandidates));
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load removal candidates");
            RemovalCandidatesStatus = "Failed to load removal candidates.";
        }
    }

    private static string BuildTrackLabel(string? artist, string? title)
    {
        var artistPart = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist.Trim();
        var titlePart = string.IsNullOrWhiteSpace(title) ? "Unknown Title" : title.Trim();
        return $"{artistPart} - {titlePart}";
    }

    private void OpenPathInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }

            if (System.IO.Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open path in explorer: {Path}", path);
        }
    }

    /// <summary>
    /// Allows restarting a stuck authentication flow while UI shows "Authentication Active".
    /// Enabled only when IsAuthenticating is true.
    /// </summary>
    private async Task RestartSpotifyAuthAsync()
    {
        try
        {
            _logger.LogInformation("Restarting Spotify authentication flow...");
            
            // Forcefully cancel any ongoing attempt
            _connectCts?.Cancel();
            
            // Clear the authenticating flag to re-enable connect logic (triggers cancellation logic in setter too)
            IsAuthenticating = false;
            
            // Immediately start a fresh connect attempt
            await ConnectSpotifyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart Spotify authentication");
            IsAuthenticating = false;
        }
        finally
        {
            (RestartSpotifyAuthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _spotifyAuthService.AuthenticationChanged -= OnSpotifyAuthenticationChanged;
        
        _libraryFoldersSubscription?.Dispose();
        _libraryFoldersSubscription = null;

        // Phase 6: Security audit subscription
        _securityAuditSubscription?.Dispose();
        _securityAuditSubscription = null;

        _networkActivitySubscription?.Dispose();
        _networkActivitySubscription = null;

        _engineDiagnosticsSubscription?.Dispose();
        _engineDiagnosticsSubscription = null;

        _adaptiveLaneStatusSubscription?.Dispose();
        _adaptiveLaneStatusSubscription = null;

        _searchPressureSubscription?.Dispose();
        _searchPressureSubscription = null;

        _shareHealthSubscription?.Dispose();
        _shareHealthSubscription = null;

        _soulseekLifecycleSubscription?.Dispose();
        _soulseekLifecycleSubscription = null;
        
        _authWatchdogCts?.Cancel();
        _authWatchdogCts?.Dispose();
        _authWatchdogCts = null;

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;

        _isDisposed = true;
    }

    // Phase E3: Diagnostics snapshot
    private async Task CopyDiagnosticsSnapshotAsync()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== ORBIT Diagnostics Snapshot — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine();

            // Search pressure
            if (_lastSearchPressure is { } p)
            {
                sb.AppendLine("[Search Pressure]");
                sb.AppendLine($"  Level         : {p.PressureLevel}");
                sb.AppendLine($"  Response cap  : {p.ResponseLimit}");
                sb.AppendLine($"  File cap      : {p.FileLimit}");
                sb.AppendLine($"  Variation cap : {p.VariationCap}");
                sb.AppendLine($"  Extra delay   : {p.AdditionalDelayMs} ms");
            }
            else
            {
                sb.AppendLine("[Search Pressure]");
                sb.AppendLine("  No pressure data recorded yet (no search performed).");
            }
            sb.AppendLine();

            // Adaptive lane decisions
            sb.AppendLine("[Adaptive Lane Decisions (newest first)]");
            if (AdaptiveLaneDecisionHistory.Count == 0)
            {
                sb.AppendLine("  None recorded.");
            }
            else
            {
                foreach (var entry in AdaptiveLaneDecisionHistory)
                    sb.AppendLine($"  {entry.TimeLabel}  {entry.LaneLabel,-12}  {entry.Reason}");
            }
            sb.AppendLine();

            // Share health
            sb.AppendLine("[Share Health]");
            sb.AppendLine($"  {ShareStatusSummary}");
            sb.AppendLine();

            // FFmpeg
            sb.AppendLine("[FFmpeg]");
            sb.AppendLine($"  {FfmpegStatus}{(!string.IsNullOrWhiteSpace(FfmpegVersion) ? " — " + FfmpegVersion : "")}");

            var snapshot = sb.ToString();

            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(snapshot);
                DiagnosticsSnapshotStatus = "✅ Copied to clipboard";
            }
            else
            {
                DiagnosticsSnapshotStatus = "⚠️ Clipboard unavailable";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy diagnostics snapshot.");
            DiagnosticsSnapshotStatus = "❌ Copy failed";
        }

        // Auto-clear status after 3 s
        await Task.Delay(3000);
        DiagnosticsSnapshotStatus = "";
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }


    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Diagnostic method: Clears cached credentials and re-authenticates.
    /// Useful for testing if the app has a "poisoned" token cache.
    /// </summary>
    private async Task RevokeAndReAuthAsync()

    {
        // Cancel any previous attempts
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        try
        {
            IsAuthenticating = true;
            _logger.LogInformation("Revoking cached credentials and re-authenticating...");
            
            await _spotifyAuthService.ClearCachedCredentialsAsync();
            SpotifyState = SpotifyAuthStatus.Disconnected;
            SpotifyDisplayName = "Not Connected";
            
            _logger.LogInformation("Credentials cleared. Starting fresh authentication...");
            
            // Step 2: Start fresh authentication (WITH CANCELLATION TOKEN)
            var success = await _spotifyAuthService.StartAuthorizationAsync(_connectCts.Token);
            
            if (success)
            {
                // Update display based on new auth state
                SpotifyState = _spotifyAuthService.IsAuthenticated ? SpotifyAuthStatus.Connected : SpotifyAuthStatus.Disconnected;
                SpotifyDisplayName = IsSpotifyConnected ? "Connected" : "Not Connected";
                SpotifyUseApi = true;
                _configManager.Save(_config);
                _logger.LogInformation("✓ Revoke & Re-auth completed successfully");
            }
            else
            {
                _logger.LogWarning("Revoke & Re-auth failed - user cancelled or error occurred");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Revoke & Re-auth cancelled");
            SpotifyDisplayName = "Cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revoke & Re-auth failed");
            SpotifyDisplayName = "Error during re-auth";
        }
        finally
        {
            IsAuthenticating = false;
            _connectCts?.Dispose();
            _connectCts = null;
            (RevokeAndReAuthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    /// <summary>
    /// "Back up now" (backlog B6): a consistent snapshot of library.db into the Backups folder.
    /// Manual backups use their own prefix, so the startup rotation (newest 5) never prunes them.
    /// </summary>
    private async Task BackupLibraryNowAsync()
    {
        try
        {
            var path = await Task.Run(() => SLSKDONET.Services.LibraryDatabaseMaintenance.CreateBackup());
            LastLibraryBackupText = $"Last backup: {System.IO.Path.GetFileName(path)}";
            _eventBus.Publish(new ToastRequestedEvent("Library backed up", $"Saved {System.IO.Path.GetFileName(path)} to the Backups folder.", NotificationType.Success, TimeSpan.FromSeconds(5)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual library backup failed");
            _eventBus.Publish(new ToastRequestedEvent("Backup failed", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(8)));
        }
    }

    private void OpenBackupsFolder()
    {
        var dir = SLSKDONET.Services.LibraryDatabaseMaintenance.GetBackupDirectory();
        try { System.IO.Directory.CreateDirectory(dir); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not create backups folder {Dir}", dir); }
        OpenPathInExplorer(dir);
    }

    private string _lastLibraryBackupText = "Backups are taken automatically every launch (newest 5 kept).";
    public string LastLibraryBackupText
    {
        get => _lastLibraryBackupText;
        private set => SetProperty(ref _lastLibraryBackupText, value);
    }

    private async Task ResetDatabaseAsync()
    {
        // This deletes library.db on the next launch and rebuilds it from the newest startup
        // backup. It used to fire on a single click with no confirmation; anything changed since
        // the last launch would be gone.
        if (_dialogService != null)
        {
            var confirmed = await _dialogService.ConfirmAsync(
                "Refresh Database Schema",
                "ORBIT will restart, delete the library database and rebuild it from the most recent startup backup. " +
                "Changes made since ORBIT last started may be lost. A safety backup of the current database is taken first. Continue?",
                confirmLabel: "Restart and Rebuild",
                cancelLabel: "Cancel");
            if (!confirmed) return;
        }

        try
        {
            // Safety net first: if this fails, don't delete anything.
            var safety = await Task.Run(() => SLSKDONET.Services.LibraryDatabaseMaintenance.CreateBackup());
            _logger.LogWarning("Pre-reset safety backup written to {Path}", safety);

            var markerPath = SchemaMigratorService.ForceResetMarkerPath();

            await System.IO.File.WriteAllTextAsync(markerPath, DateTime.Now.ToString());
            _logger.LogWarning("Force Reset Marker created at {Path}", markerPath);
            
            // Restart Application
            var processPath = Environment.ProcessPath; 
            _logger.LogInformation("Attempting to restart application from: {Path}", processPath);
            
            if (!string.IsNullOrEmpty(processPath))
            {
                System.Diagnostics.Process.Start(processPath);
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate database reset");
            _eventBus.Publish(new ToastRequestedEvent("Schema refresh not started", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(8)));
        }
    }

    private async Task LoadLibraryFoldersAsync()
    {
        try
        {
            await using var context = _dbFactory != null ? _dbFactory.CreateDbContext() : new AppDbContext();
            var folders = await context.LibraryFolders.OrderBy(f => f.FolderPath).ToListAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LibraryFolders.Clear();
                foreach (var folder in folders)
                {
                    LibraryFolders.Add(new LibraryFolderViewModel(folder));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load library folders in settings");
        }
    }

    /// <summary>Settings → Library "Add folder": the same picker + import sheet as Library folders.</summary>
    private async Task AddLibraryFolderAsync()
    {
        try
        {
            if (_importLauncher != null)
            {
                await _importLauncher.PickFolderAndStartImportAsync();
                return;
            }

            var path = await _fileInteractionService.OpenFolderDialogAsync("Select Music Library Folder");
            if (string.IsNullOrEmpty(path)) return;
            await _libraryFolderScannerService.RegisterFolderAsync(path);
            _eventBus.Publish(new LibraryFoldersChangedEvent());
            _logger.LogInformation("Added library folder: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add library folder");
            _eventBus.Publish(new ToastRequestedEvent("Couldn't add the folder", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(6)));
        }
    }

    private async Task RemoveLibraryFolderAsync()
    {
        if (SelectedLibraryFolder == null) return;

        try
        {
            await using var context = _dbFactory != null ? _dbFactory.CreateDbContext() : new AppDbContext();
            var folder = await context.LibraryFolders.FindAsync(SelectedLibraryFolder.Id);
            
            if (folder != null)
            {
                context.LibraryFolders.Remove(folder);
                await context.SaveChangesAsync();

                _eventBus.Publish(new LibraryFoldersChangedEvent());
                _logger.LogInformation("Removed library folder: {Path}", folder.FolderPath);
                
                // Clear selection
                SelectedLibraryFolder = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove library folder");
        }
    }

    private void OnSoulseekLifecycleChanged(ConnectionLifecycleStateChangedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            static string ToFriendlyFailureMessage(string reason)
            {
                if (reason.StartsWith("login rejected:", StringComparison.OrdinalIgnoreCase))
                    return $"Sign-in failed: {reason["login rejected:".Length..].Trim()}";

                if (reason.StartsWith("connect failed:", StringComparison.OrdinalIgnoreCase))
                    return $"Connection failed: {reason["connect failed:".Length..].Trim()}";

                return "Disconnected";
            }

            SoulseekIsConnected = evt.Current == "LoggedIn";
            SoulseekConnectionStatusText = evt.Current switch
            {
                "LoggedIn"     => $"Connected as {SoulseekUsername}",
                "Connecting"   => "Connecting…",
                "LoggingIn"    => "Logging in…",
                "CoolingDown"  => "Cooling down before reconnect…",
                "Disconnecting" => "Disconnecting…",
                "Disconnected" => ToFriendlyFailureMessage(evt.Reason),
                _              => evt.Current
            };

            // Re-evaluate button enabled state
            ((AsyncRelayCommand)SoulseekConnectCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SoulseekDisconnectCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)SoulseekReconnectCommand).RaiseCanExecuteChanged();
        });
    }

    // Soulseek Connection Methods
    private async Task SoulseekConnectAsync()
    {
        try
        {
            // Load stored credentials if available
            var creds = await _credentialService.LoadCredentialsAsync();
            if (!string.IsNullOrEmpty(creds.Password) && !string.IsNullOrEmpty(creds.Username))
            {
                SoulseekConnectionStatusText = "Connecting…";
                await _lifecycle.RequestConnectAsync(creds.Password);
            }
            else
            {
                SoulseekConnectionStatusText = "No stored credentials — use the Sign In overlay to connect first.";
                _logger.LogWarning("No stored credentials available for Soulseek connection");
            }
        }
        catch (Exception ex)
        {
            SoulseekConnectionStatusText = $"Connection failed: {ex.Message}";
            _logger.LogError(ex, "Failed to connect to Soulseek from settings");
        }
    }

    private void SoulseekDisconnect()
    {
        try
        {
            _lifecycle.NotifyManualDisconnect();
            _ = _lifecycle.RequestDisconnectAsync("manual settings disconnect");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect from Soulseek");
        }
    }

    private async Task SoulseekReconnectAsync()
    {
        try
        {
            SoulseekDisconnect();
            await Task.Delay(1000); // Brief pause before reconnecting
            await SoulseekConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconnect to Soulseek");
        }
    }
}

public sealed class RemovalCandidateItemViewModel
{
    public RemovalCandidateItemViewModel(
        string sourcePath,
        string preferredPath,
        string trackLabel,
        DateTime timestampUtc,
        Action<string> openPathAction)
    {
        SourcePath = sourcePath;
        PreferredPath = preferredPath;
        TrackLabel = trackLabel;
        TimestampLabel = timestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        OpenCandidateCommand = new RelayCommand(() => openPathAction(SourcePath));
        OpenPreferredCommand = new RelayCommand(() => openPathAction(PreferredPath));
    }

    public string SourcePath { get; }
    public string PreferredPath { get; }
    public string TrackLabel { get; }
    public string TimestampLabel { get; }
    public string SourceFileName => System.IO.Path.GetFileName(SourcePath);
    public string PreferredFileName => System.IO.Path.GetFileName(PreferredPath);

    public ICommand OpenCandidateCommand { get; }
    public ICommand OpenPreferredCommand { get; }
}

