using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Platform;
using SLSKDONET.Views; // For AsyncRelayCommand
using Avalonia.Threading;

using System.Collections.ObjectModel; // Added
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
    private IDisposable? _libraryFoldersSubscription;

    private readonly ILogger<SettingsViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly SpotifyAuthService _spotifyAuthService;
    private readonly ISpotifyMetadataService _spotifyMetadataService;
    private readonly DatabaseService _databaseService;
    private readonly LibraryFolderScannerService _libraryFolderScannerService;
    private readonly IEventBus _eventBus;
    private readonly ISoulseekAdapter _soulseek;
    private readonly ISoulseekCredentialService _credentialService;

    // Hardcoded public client ID provided by user/project
    // Ideally this would be in a secured config, but for this desktop app scenario it's acceptable as a default.
    private const string DefaultSpotifyClientId = "67842a599c6f45edbf3de3d84231deb4";

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
                OnPropertyChanged(nameof(ShareStatusSummary));
                OnPropertyChanged(nameof(ShareStatusColor));
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
            if (_config.PreferredMinBitrate != value)
            {
                _config.PreferredMinBitrate = value;
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

    public string SearchProfileModeText => SearchProfileStricter
        ? "STRICTER overwrite: FLAC-only + 701kbps floor"
        : SearchProfileStrict
            ? "STRICT overwrite: FLAC/WAV/AIFF/AIF + 320kbps floor"
            : "NON-STRICT overwrite: expanded formats + 192kbps floor";

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

    public string SpotifyClientId
    {
        get => _config.SpotifyClientId ?? "";
        set { _config.SpotifyClientId = value; OnPropertyChanged(); SaveSettings(); }
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

    private void InitializeStrategies()
    {
        Strategies.Add(new RankingStrategyViewModel
        {
            Id = "Quality First",
            Title = "Audiophile",
            Description = "Prioritizes lossless, high-bitrate, and perfect rips. No compromises.",
            Icon = "🎧",
            IsSelected = _config.RankingProfile == "Quality First"
        });

        Strategies.Add(new RankingStrategyViewModel
        {
            Id = "Balanced",
            Title = "Balanced",
            Description = "The best mix of quality, speed, and metadata accuracy. Recommended.",
            Icon = "⚖️",
            IsSelected = _config.RankingProfile == "Balanced" || string.IsNullOrEmpty(_config.RankingProfile)
        });

        Strategies.Add(new RankingStrategyViewModel
        {
            Id = "DJ Mode",
            Title = "DJ Ready",
            Description = "Prioritizes BPM, Key, and mix-friendly files (Extended Mixes).",
            Icon = "🎛️",
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
                PreferredMinBitrate = 192,
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
        
        OnPropertyChanged(nameof(SpotifyStatusColor));
        OnPropertyChanged(nameof(SpotifyStatusIcon));
        
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

    // SSO State (Legacy compat where needed, but driven by State now)
    public string SpotifyStatusColor => IsSpotifyConnected ? "#1DB954" : (IsSpotifyConnecting ? "#FFB900" : "#333333");
    public string SpotifyStatusIcon => IsSpotifyConnected ? "✓" : (IsSpotifyConnecting ? "⏳" : "🚫");



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
            _logger.LogInformation("IsAuthenticating changing from {Old} to {New} (StackTrace: {Trace})", 
                _isAuthenticating, value, Environment.StackTrace);
                
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
    public ICommand ConnectSpotifyCommand { get; }
    public ICommand DisconnectSpotifyCommand { get; }
    public ICommand TestSpotifyConnectionCommand { get; }
    public ICommand ClearSpotifyCacheCommand { get; }
    public ICommand RevokeAndReAuthCommand { get; }
    public ICommand RestartSpotifyAuthCommand { get; }
    public ICommand CheckFfmpegCommand { get; } // Phase 8: Dependency validation
    public ICommand ResetDatabaseCommand { get; }
    public ICommand ScanLibraryCommand { get; } // [NEW] Manual Scan
    public ICommand AddLibraryFolderCommand { get; }
    public ICommand RemoveLibraryFolderCommand { get; }

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
    
    // Phase 8: FFmpeg Dependency State
    private bool _isFfmpegInstalled;
    public bool IsFfmpegInstalled
    {
        get => _isFfmpegInstalled;
        set
        {
            if (SetProperty(ref _isFfmpegInstalled, value))
            {
                OnPropertyChanged(nameof(FfmpegBorderColor));
            }
        }
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

    public string FfmpegBorderColor => IsFfmpegInstalled ? "#1DB954" : "#FFA500";

    // Phase 6: Security Audit Feed
    private const int AuditFeedMaxEntries = 100;
    public ObservableCollection<SecurityAuditEntryViewModel> SecurityAuditFeed { get; } = new();
    private IDisposable? _securityAuditSubscription;
    private void OnSecurityAuditEvent(SecurityAuditEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SecurityAuditFeed.Insert(0, new SecurityAuditEntryViewModel(e));
            while (SecurityAuditFeed.Count > AuditFeedMaxEntries)
                SecurityAuditFeed.RemoveAt(SecurityAuditFeed.Count - 1);
        });
    }
    public ICommand ClearSecurityAuditCommand { get; private set; } = null!;

    // Phase 6: Live Share Status (bound in Settings page)
    private int    _shareFileCount;
    private string _shareReputationLabel = "Unknown";
    private IDisposable? _shareHealthSubscription;

    public int ShareFileCount
    {
        get => _shareFileCount;
        private set { _shareFileCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShareStatusSummary)); OnPropertyChanged(nameof(ShareStatusColor)); }
    }

    public string ShareStatusColor => !EnableLibrarySharing ? "#666666" :
        _shareFileCount == 0 ? "#F44336" :
        _shareFileCount <  500 ? "#FFA500" : "#1DB954";

    public string ShareStatusSummary => !EnableLibrarySharing
        ? "Sharing is disabled"
        : _shareFileCount == 0
            ? "No shared files detected — check the folder path"
            : $"{_shareFileCount:N0} files shared · {_shareReputationLabel}";

    public ICommand RefreshShareNowCommand { get; private set; } = null!;

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
        ISoulseekCredentialService credentialService)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _fileInteractionService = fileInteractionService;
        _spotifyAuthService = spotifyAuthService;
        _spotifyMetadataService = spotifyMetadataService;
        _databaseService = databaseService;
        _libraryFolderScannerService = libraryFolderScannerService;
        _eventBus = eventBus;
        _soulseek = soulseek;
        _credentialService = credentialService;

        // Ensure default Client ID is set if empty
        if (string.IsNullOrEmpty(_config.SpotifyClientId))
        {
            _config.SpotifyClientId = DefaultSpotifyClientId;
            // Clear secret if we are setting the public ID, as PKCE doesn't use it
            _config.SpotifyClientSecret = ""; 
        }

        SaveSettingsCommand = new RelayCommand(SaveSettings);
        BrowseDownloadPathCommand = new AsyncRelayCommand(BrowseDownloadPathAsync);
        BrowseSharedFolderCommand = new AsyncRelayCommand(BrowseSharedFolderAsync);

        ConnectSpotifyCommand = new AsyncRelayCommand(ConnectSpotifyAsync, () => IsSpotifyDisconnected);
        DisconnectSpotifyCommand = new AsyncRelayCommand(DisconnectSpotifyAsync, () => IsSpotifyConnected);
        TestSpotifyConnectionCommand = new AsyncRelayCommand(TestSpotifyConnectionAsync); // Always allow testing if user expands advanced
        ClearSpotifyCacheCommand = new AsyncRelayCommand(ClearSpotifyCacheAsync);
        RevokeAndReAuthCommand = new AsyncRelayCommand(RevokeAndReAuthAsync);
        CheckFfmpegCommand = new AsyncRelayCommand(CheckFfmpegAsync); // Phase 8
        RestartSpotifyAuthCommand = new AsyncRelayCommand(RestartSpotifyAuthAsync, () => IsSpotifyConnecting);
        ResetDatabaseCommand = new AsyncRelayCommand(ResetDatabaseAsync);
        ScanLibraryCommand = new AsyncRelayCommand(ScanLibraryAsync, () => !IsScanning);
        AddLibraryFolderCommand = new AsyncRelayCommand(AddLibraryFolderAsync);
        RemoveLibraryFolderCommand = new AsyncRelayCommand(RemoveLibraryFolderAsync, () => SelectedLibraryFolder != null);
        ClearSecurityAuditCommand = new RelayCommand(() => SecurityAuditFeed.Clear());
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
        
        _ = CheckFfmpegAsync(); // Phase 8: Check FFmpeg on startup
        _ = LoadLibraryFoldersAsync(); // Phase 0.10

        _libraryFoldersSubscription = _eventBus.GetEvent<LibraryFoldersChangedEvent>().Subscribe(e => { _ = LoadLibraryFoldersAsync(); });

        // Phase 6: Security Audit Feed subscription
        _securityAuditSubscription = _eventBus.GetEvent<SecurityAuditEvent>().Subscribe(OnSecurityAuditEvent);
        
        // Force update of derived properties to ensure UI booleans are in sync with SpotifyState
        UpdateDerivedProperties(SpotifyState);
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
    private async Task CheckFfmpegAsync()
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
    private async Task ConnectSpotifyAsync()
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

    private void SaveSettings()
    {
        try
        {
            _configManager.Save(_config);
            // TODO: Show toast notification?
            _logger.LogInformation("Settings saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
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


    
    private async Task ScanLibraryAsync()
    {
        try
        {
            if (IsScanning) return;
            IsScanning = true;
            ScanStatus = "Preparing to scan...";
            
            _logger.LogInformation("Starting manual library scan...");
            
            // 1. Ensure download folder is registered
            if (!string.IsNullOrEmpty(_config.DownloadDirectory))
            {
                await _libraryFolderScannerService.EnsureDefaultFolderAsync(_config.DownloadDirectory);
            }
            else
            {
                 // Fallback or warning?
                 _logger.LogWarning("No download directory configured for scanning.");
            }
            
            // 2. Run Scan
            var progress = new Progress<ScanProgress>(p =>
            {
                // Throttle updates or just show simplified status
                ScanStatus = $"Scanning: Found {p.FilesDiscovered}, Imported {p.FilesImported}...";
            });
            
            var results = await _libraryFolderScannerService.ScanAllFoldersAsync(progress);
            
            // 3. Summarize
            int totalImported = results.Values.Sum(r => r.FilesImported);
            int totalSkipped = results.Values.Sum(r => r.FilesSkipped);
            
            ScanStatus = $"Done. Imported {totalImported} new tracks.";
            _logger.LogInformation("Manual scan complete. Imported: {Imported}, Skipped: {Skipped}", totalImported, totalSkipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual scan failed");
            ScanStatus = "Scan Failed";
        }
        finally
        {
            IsScanning = false;
            (ScanLibraryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

        _shareHealthSubscription?.Dispose();
        _shareHealthSubscription = null;
        
        _authWatchdogCts?.Cancel();
        _authWatchdogCts?.Dispose();
        _authWatchdogCts = null;

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;

        _isDisposed = true;
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
    private async Task ResetDatabaseAsync()
    {
        try 
        {
            // Create marker file
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var markerPath = System.IO.Path.Combine(appData, "ORBIT", ".force_schema_reset");
            
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
        }
    }

    private async Task LoadLibraryFoldersAsync()
    {
        try
        {
            using var context = new AppDbContext();
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

    private async Task AddLibraryFolderAsync()
    {
        try
        {
            var path = await _fileInteractionService.OpenFolderDialogAsync("Select Music Library Folder");
            if (string.IsNullOrEmpty(path)) return;

            using var context = new AppDbContext();
            
            // Check duplicates
            if (await context.LibraryFolders.AnyAsync(f => f.FolderPath == path))
            {
                _logger.LogWarning("Folder already exists: {Path}", path);
                return;
            }

            var folder = new LibraryFolderEntity
            {
                Id = Guid.NewGuid(),
                FolderPath = path,
                IsEnabled = true,
                AddedAt = DateTime.UtcNow
            };

            context.LibraryFolders.Add(folder);
            await context.SaveChangesAsync();

            _eventBus.Publish(new LibraryFoldersChangedEvent());
            _logger.LogInformation("Added library folder: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add library folder");
        }
    }

    private async Task RemoveLibraryFolderAsync()
    {
        if (SelectedLibraryFolder == null) return;

        try
        {
            using var context = new AppDbContext();
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

    // Soulseek Connection Methods
    private async Task SoulseekConnectAsync()
    {
        try
        {
            // Load stored credentials if available
            var creds = await _credentialService.LoadCredentialsAsync();
            if (!string.IsNullOrEmpty(creds.Password) && !string.IsNullOrEmpty(creds.Username))
            {
                await _soulseek.ConnectAsync(creds.Password);
            }
            else
            {
                _logger.LogWarning("No stored credentials available for Soulseek connection");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Soulseek from settings");
        }
    }

    private void SoulseekDisconnect()
    {
        try
        {
            _soulseek.Disconnect();
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

