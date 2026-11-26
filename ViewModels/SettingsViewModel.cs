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

public partial class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
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
    private readonly Services.Library.Maintenance.MaintenanceUndoService? _maintenanceUndo;

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

    /// <summary>Settings → Maintenance: open the Library Maintenance wizard overlay.</summary>
    private ICommand? _runMaintenanceCommand;
    public ICommand RunMaintenanceCommand => _runMaintenanceCommand ??= new RelayCommand(() => _eventBus.Publish(new OpenLibraryMaintenanceRequestedEvent()));
    // ── Rekordbox play history → "played live" evidence (#207) ─────────────
    private readonly Services.KnowledgeLifecycle.RekordboxHistoryImportService? _historyImport;
    private AsyncRelayCommand? _importRekordboxHistoryCommand;
    public ICommand ImportRekordboxHistoryCommand => _importRekordboxHistoryCommand ??=
        new AsyncRelayCommand(ImportRekordboxHistoryAsync, () => _historyImport is not null && !IsImportingHistory);

    private bool _isImportingHistory;
    public bool IsImportingHistory
    {
        get => _isImportingHistory;
        private set
        {
            if (_isImportingHistory == value) return;
            _isImportingHistory = value;
            OnPropertyChanged();
            _importRekordboxHistoryCommand?.RaiseCanExecuteChanged();
        }
    }

    private string? _historyImportStatus;
    /// <summary>The last import's honest result (sessions, transitions, unmatched tracks, or why it failed).</summary>
    public string? HistoryImportStatus
    {
        get => _historyImportStatus;
        private set { if (_historyImportStatus == value) return; _historyImportStatus = value; OnPropertyChanged(); }
    }

    private async Task ImportRekordboxHistoryAsync()
    {
        if (_historyImport is null) return;
        IsImportingHistory = true;
        HistoryImportStatus = "Reading Rekordbox's play history…";
        try
        {
            var result = await _historyImport.ImportAsync();
            HistoryImportStatus = result.Summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rekordbox history import failed");
            HistoryImportStatus = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImportingHistory = false;
        }
    }

    private ICommand? _findExactDuplicatesCommand;
    public ICommand FindExactDuplicatesCommand => _findExactDuplicatesCommand ??= new RelayCommand(() => _eventBus.Publish(new OpenExactDuplicatesRequestedEvent()));

    private bool _canUndoMaintenance;
    public bool CanUndoMaintenance
    {
        get => _canUndoMaintenance;
        private set
        {
            if (_canUndoMaintenance == value) return;
            _canUndoMaintenance = value;
            OnPropertyChanged(nameof(CanUndoMaintenance));
        }
    }

    private ICommand? _undoLastMaintenanceCommand;
    public ICommand UndoLastMaintenanceCommand => _undoLastMaintenanceCommand ??= new AsyncRelayCommand(UndoLastMaintenanceAsync);

    /// <summary>Re-checks whether a revertable maintenance run exists (called when Settings loads and after undo).</summary>
    public async Task RefreshMaintenanceUndoAsync()
    {
        try
        {
            CanUndoMaintenance = _maintenanceUndo != null && await _maintenanceUndo.GetLastRevertableRunIdAsync() != null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not check for a revertable maintenance run");
            CanUndoMaintenance = false;
        }
    }

    private async Task UndoLastMaintenanceAsync()
    {
        if (_maintenanceUndo == null) return;
        var undone = await _maintenanceUndo.UndoLastAsync();
        _eventBus.Publish(undone
            ? new ToastRequestedEvent("Maintenance undone", "The last maintenance run was reverted.", NotificationType.Success, TimeSpan.FromSeconds(6))
            : new ToastRequestedEvent("Nothing to undo", "There is no completed maintenance run to revert.", NotificationType.Information, TimeSpan.FromSeconds(6)));
        await RefreshMaintenanceUndoAsync();
    }

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

    /// <summary>Settings → Library "Learn from my genre corrections" (GA11 #256); null in unit tests.</summary>
    public Settings.PersonalGenreSettingsViewModel? PersonalGenre { get; }

    public bool HasPersonalGenre => PersonalGenre is not null;

    /// <summary>Epic #517 (#536): enables Labs pages (Sets, Workstation, Edit Lab) and knowledge agents.</summary>
    public bool LabsEnabled
    {
        get => _config.LabsEnabled;
        set
        {
            if (_config.LabsEnabled != value)
            {
                _config.LabsEnabled = value;
                OnPropertyChanged();
                SaveSettings();
                _eventBus.Publish(new LabsEnabledChangedEvent(value));
            }
        }
    }

    /// <summary>Opt-in background knowledge agents (transition-candidate scan). Applies after restart.</summary>
    public bool EnableKnowledgeAgents
    {
        get => _config.EnableKnowledgeAgents;
        set
        {
            if (_config.EnableKnowledgeAgents != value)
            {
                _config.EnableKnowledgeAgents = value;
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
                OnPropertyChanged(nameof(FileNameFormatError));
                OnPropertyChanged(nameof(HasFileNameFormatError));
                OnPropertyChanged(nameof(FileNameFormatPreviewText));
                OnPropertyChanged(nameof(SelectedFileNamePreset));
                if (!HasFileNameFormatError)
                    SaveSettings();
            }
        }
    }

    /// <summary>LF1 (#343): 1-click editable path template presets.</summary>
    public static IReadOnlyList<Services.Library.Files.PathTemplatePreset> AvailableFileNamePresets =>
        Services.Library.Files.PathTemplateEngine.Presets;

    public Services.Library.Files.PathTemplatePreset? SelectedFileNamePreset
    {
        get => AvailableFileNamePresets.FirstOrDefault(p => string.Equals(p.Template, FileNameFormat, StringComparison.Ordinal));
        set
        {
            if (value is not null && FileNameFormat != value.Template)
                FileNameFormat = value.Template;
        }
    }

    public string? FileNameFormatError => Services.Library.Files.PathTemplateEngine.Validate(FileNameFormat);
    public bool HasFileNameFormatError => !string.IsNullOrEmpty(FileNameFormatError);

    /// <summary>LF1 (#343): live preview rendered against 3 representative tracks using PathTemplateEngine.</summary>
    public string FileNameFormatPreviewText
    {
        get
        {
            var lines = new List<string>(Services.Library.Files.PathTemplateEngine.PreviewSamples.Count);
            foreach (var (facts, ext) in Services.Library.Files.PathTemplateEngine.PreviewSamples)
            {
                var rendered = Services.Library.Files.PathTemplateEngine.Render(FileNameFormat, facts);
                lines.Add(string.IsNullOrEmpty(rendered.RelativePath) ? "(empty)" : rendered.RelativePath + ext);
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>LF3 (#345): how verified downloads leave staging — straight to the template path ("Direct") or into Inbox/ for review ("Inbox").</summary>
    public static string[] AvailableDownloadCommitModes { get; } = { "Direct", "Inbox" };

    public string DownloadCommitMode
    {
        get => _config.DownloadCommitMode is "Inbox" ? "Inbox" : "Direct";
        set
        {
            var normalized = value is "Inbox" ? "Inbox" : "Direct";
            if (_config.DownloadCommitMode != normalized)
            {
                _config.DownloadCommitMode = normalized;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>LF3 (#345): re-file unlocked tracks after analysis when the template uses {key}, {bpm}, or {bpm_bracket}.</summary>
    public bool RefileAfterAnalysis
    {
        get => _config.RefileAfterAnalysis;
        set
        {
            if (_config.RefileAfterAnalysis != value)
            {
                _config.RefileAfterAnalysis = value;
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
            var clampedValue = Math.Clamp(value, 128, 1411);
            if (_config.PreferredMinBitrate != clampedValue)
            {
                _config.PreferredMinBitrate = clampedValue;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchProfileNonStrict));
                OnPropertyChanged(nameof(SearchProfileStrict));
                OnPropertyChanged(nameof(SearchProfileStricter));
                OnPropertyChanged(nameof(SearchProfileModeText));
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

    public string SearchProfileModeText => SearchQualityProfiles.Describe(_config);

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
            var kind = string.Equals(mode, "Stricter", StringComparison.OrdinalIgnoreCase)
                ? SearchQualityProfileKind.Stricter
                : string.Equals(mode, "Strict", StringComparison.OrdinalIgnoreCase)
                    ? SearchQualityProfileKind.Strict
                    : SearchQualityProfileKind.NonStrict;

            SearchQualityProfiles.Apply(_config, kind);

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

    private bool IsNonStrictProfileActive() =>
        SearchQualityProfiles.Detect(_config) == SearchQualityProfileKind.NonStrict;

    private bool IsStrictProfileActive() =>
        SearchQualityProfiles.Detect(_config) == SearchQualityProfileKind.Strict;

    private bool IsStricterProfileActive() =>
        SearchQualityProfiles.Detect(_config) == SearchQualityProfileKind.Stricter;

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
    /// Shared "Use your own Spotify app" setup ViewModel (Epic #508 / #514), reused by the Setup Wizard.
    /// </summary>
    public Setup.OwnSpotifyAppViewModel OwnSpotifyApp { get; private set; } = null!;

    /// <summary>
    /// The configured Spotify app Client ID (empty = none configured; public links still work).
    /// </summary>
    public string SpotifyClientId
    {
        get => _config.SpotifyClientId ?? string.Empty;
        set
        {
            var clientId = value?.Trim() ?? string.Empty;
            if (clientId == (_config.SpotifyClientId ?? string.Empty)) return;

            _config.SpotifyClientId = clientId;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUsingBuiltInSpotifyClientId));
            OnPropertyChanged(nameof(IsSpotifyClientIdConfigured));
            OwnSpotifyApp?.RefreshFromConfig();
            SaveSettings();

            // Tokens belong to the app that issued them; the old session can't refresh under the new ID.
            if (_spotifyAuthService.IsAuthenticated)
                _ = SignOutAfterClientIdChangeAsync();
        }
    }

    public bool IsSpotifyClientIdConfigured =>
        !string.IsNullOrWhiteSpace(_config.SpotifyClientId);

    public bool IsUsingBuiltInSpotifyClientId =>
        SpotifyClientIdMigrationService.Classify(_config.SpotifyClientId) == SpotifyClientIdKind.BuiltInTester;

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

    private readonly Services.Platform.FfmpegInstallerService? _ffmpegInstaller;

    /// <summary>Installs the pinned, checksum-verified FFmpeg build (#331; the same installer setup uses).</summary>
    public ICommand InstallFfmpegCommand { get; }

    /// <summary>Offered when FFmpeg is missing and ORBIT can install it on this OS (Windows x64).</summary>
    public bool CanInstallFfmpeg => _ffmpegInstaller != null && !IsFfmpegInstalled
        && Services.Platform.FfmpegInstallerService.CanAutoInstall;

    private bool _isInstallingFfmpeg;
    public bool IsInstallingFfmpeg
    {
        get => _isInstallingFfmpeg;
        private set
        {
            if (SetProperty(ref _isInstallingFfmpeg, value))
                (InstallFfmpegCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

