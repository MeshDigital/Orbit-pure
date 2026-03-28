using Microsoft.Extensions.Configuration;
using System.IO;
using System.Text.Json;

namespace SLSKDONET.Configuration;

/// <summary>
/// Manages configuration loading and saving.
/// </summary>
public class ConfigManager
{
    private readonly string _configPath;
    private AppConfig _config = null!;

    public ConfigManager(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
    }

    /// <summary>
    /// Gets the default configuration file path.
    /// </summary>
    public static string GetDefaultConfigPath()
    {
        // Optional dev override: look for config.ini in C:\temp first.
        var devPath = "C:/temp/config.ini";
        if (File.Exists(devPath))
        {
            return devPath;
        }

        // Prioritize config.ini in the application's root directory for portability.
        var localPath = Path.Combine(AppContext.BaseDirectory, "config.ini");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        // Fallback to AppData for a more traditional installation.
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appDataPath, "ORBIT");
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, "config.ini");
    }

    /// <summary>
    /// Loads configuration from file or creates default.
    /// </summary>
    public AppConfig Load()
    {
        if (File.Exists(_configPath))
        {
            var config = new ConfigurationBuilder()
                .AddIniFile(_configPath, optional: true, reloadOnChange: false)
                .Build();

            _config = new AppConfig
            {
                // [Soulseek]
                SoulseekServer = config["Soulseek:Server"] ?? "vps.slsknet.org",
                SoulseekPort = int.TryParse(config["Soulseek:Port"], out var sPort) ? sPort : 2242,
                Username = config["Soulseek:Username"] ?? "",
                // Password is no longer stored in config.ini for security
                ListenPort = int.TryParse(config["Soulseek:ListenPort"], out var port) ? port : 49998,
                UseUPnP = bool.TryParse(config["Soulseek:UseUPnP"], out var upnp) && upnp,
                ConnectTimeout = int.TryParse(config["Soulseek:ConnectTimeout"], out var ct) ? ct : 60000,
                SearchTimeout = int.TryParse(config["Soulseek:SearchTimeout"], out var st) ? st : 6000,
                RememberPassword = bool.TryParse(config["Soulseek:RememberPassword"], out var remember) && remember,
                AutoConnectEnabled = bool.TryParse(config["Soulseek:AutoConnectEnabled"], out var ace) && ace,

                // [Download]
                DownloadDirectory = config["Download:Directory"],
                SharedFolderPath = config["Download:SharedFolder"],
                MaxConcurrentDownloads = int.TryParse(config["Download:MaxConcurrentDownloads"], out var mcd) ? mcd : 2,
                NameFormat = config["Download:NameFormat"] ?? "{artist|filename} - {title}",
                CheckForDuplicates = !bool.TryParse(config["Download:CheckForDuplicates"], out var check) || check, // Default to true
                SearchLengthToleranceSeconds = int.TryParse(config["Download:SearchLengthToleranceSeconds"], out var tol) ? tol : 3,
                FuzzyMatchEnabled = !bool.TryParse(config["Download:FuzzyMatchEnabled"], out var fz) || fz, // Default true
                MaxSearchAttempts = int.TryParse(config["Download:MaxSearchAttempts"], out var msa) ? msa : 3,
                AutoRetryFailedDownloads = !bool.TryParse(config["Download:AutoRetryFailedDownloads"], out var arf) || arf, // Default true
                MaxDownloadRetries = int.TryParse(config["Download:MaxDownloadRetries"], out var mdr) ? mdr : 2,
                EnableMp3Fallback = !bool.TryParse(config["Download:EnableMp3Fallback"], out var emf) || emf, // Default true


                // [Spotify]
                SpotifyUsePublicOnly = !bool.TryParse(config["Spotify:SpotifyUsePublicOnly"], out var supo) || supo, // Default true
                SpotifyClientId = config["Spotify:SpotifyClientId"],
                SpotifyClientSecret = config["Spotify:SpotifyClientSecret"],
                SpotifyUseApi = !bool.TryParse(config["Spotify:MetadataEnrichmentEnabled"], out var sua) || sua,
                SpotifyRememberAuth = !bool.TryParse(config["Spotify:SpotifyRememberAuth"], out var sra) || sra, // Default true
                SpotifyCallbackPort = int.TryParse(config["Spotify:SpotifyCallbackPort"], out var scp) ? scp : 5000,
                SpotifyRedirectUri = config["Spotify:SpotifyRedirectUri"] ?? "http://127.0.0.1:5000/callback",
                ClearSpotifyOnExit = bool.TryParse(config["Spotify:ClearSpotifyOnExit"], out var csoe) && csoe,

                // [Search] & Brain 2.0
                RankingProfile = config["Search:RankingProfile"] ?? "Balanced",
                EnableFuzzyNormalization = !bool.TryParse(config["Search:EnableFuzzyNormalization"], out var efn) || efn, // Default true
                EnableRelaxationStrategy = !bool.TryParse(config["Search:EnableRelaxationStrategy"], out var ers) || ers, // Default true
                EnableVbrFraudDetection = !bool.TryParse(config["Search:EnableVbrFraudDetection"], out var evfd) || evfd, // Default true
                RelaxationTimeoutSeconds = int.TryParse(config["Search:RelaxationTimeoutSeconds"], out var rts) ? rts : 10,
                PreferredFormats = config["Search:PreferredFormats"]?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? new List<string> { "aiff", "aif", "flac", "wav" },
                PreferredMinBitrate = int.TryParse(config["Search:PreferredMinBitrate"], out var pmb) ? pmb : 701,
                PreferredMaxBitrate = int.TryParse(config["Search:PreferredMaxBitrate"], out var pmaxb) ? pmaxb : 0,
                SearchResponseLimit = int.TryParse(config["Search:SearchResponseLimit"], out var srl) ? srl : 100,
                SearchFileLimit = int.TryParse(config["Search:SearchFileLimit"], out var sfl) ? sfl : 100,
                MaxPeerQueueLength = int.TryParse(config["Search:MaxPeerQueueLength"], out var mpql) ? mpql : 50,
                MaxConcurrentSearches = int.TryParse(config["Search:MaxConcurrentSearches"], out var mcs) ? mcs : 3,
                MaxDiscoveryLanes = int.TryParse(config["Search:MaxDiscoveryLanes"], out var mdl) ? mdl : 5,
                MaxSearchVariations = int.TryParse(config["Search:MaxSearchVariations"], out var msv) ? msv : 2,
                StrictSearchSufficientResultCount = int.TryParse(config["Search:StrictSearchSufficientResultCount"], out var ssrc) ? ssrc : 5,
                EnableStrictHighConfidenceShortCircuit = !bool.TryParse(config["Search:EnableStrictHighConfidenceShortCircuit"], out var eshcs) || eshcs,
                EnableSearchLoadShedding = !bool.TryParse(config["Search:EnableSearchLoadShedding"], out var esls) || esls,
                ElevatedSearchPressureActiveSearches = int.TryParse(config["Search:ElevatedSearchPressureActiveSearches"], out var espas) ? espas : 3,
                CriticalSearchPressureActiveSearches = int.TryParse(config["Search:CriticalSearchPressureActiveSearches"], out var cspas) ? cspas : 5,
                ElevatedSearchResponseLimitPercent = int.TryParse(config["Search:ElevatedSearchResponseLimitPercent"], out var esrlp) ? esrlp : 75,
                CriticalSearchResponseLimitPercent = int.TryParse(config["Search:CriticalSearchResponseLimitPercent"], out var csrlp) ? csrlp : 50,
                ElevatedSearchFileLimitPercent = int.TryParse(config["Search:ElevatedSearchFileLimitPercent"], out var esflp) ? esflp : 75,
                CriticalSearchFileLimitPercent = int.TryParse(config["Search:CriticalSearchFileLimitPercent"], out var csflp) ? csflp : 50,
                ElevatedSearchExtraDelayMs = int.TryParse(config["Search:ElevatedSearchExtraDelayMs"], out var esed) ? esed : 75,
                CriticalSearchExtraDelayMs = int.TryParse(config["Search:CriticalSearchExtraDelayMs"], out var csed) ? csed : 200,

                // [Library] & Upgrade Scout
                LibraryColumnOrder = config["Library:ColumnOrder"] ?? "",
                LibraryNavigationCollapsed = bool.TryParse(config["Library:NavigationCollapsed"], out var navCollapsed) && navCollapsed,
                LibraryNavigationAutoHideEnabled = bool.TryParse(config["Library:NavigationAutoHideEnabled"], out var navAutoHideEnabled) && navAutoHideEnabled,
                LibraryNavigationAutoHideActivationToggleCount = int.TryParse(config["Library:NavigationAutoHideActivationToggleCount"], out var navAutoHideActivationCount)
                    ? Math.Max(2, navAutoHideActivationCount)
                    : 3,
                UpgradeScoutEnabled = bool.TryParse(config["Library:UpgradeScoutEnabled"], out var use) && use,
                UpgradeMinBitrateThreshold = int.TryParse(config["Library:UpgradeMinBitrateThreshold"], out var umbt) ? umbt : 320,
                UpgradeMinGainKbps = int.TryParse(config["Library:UpgradeMinGainKbps"], out var umgk) ? umgk : 128,
                UpgradeAutoQueueEnabled = bool.TryParse(config["Library:UpgradeAutoQueueEnabled"], out var uaqe) && uaqe,
                
                // [Dependencies]
                IsFfmpegAvailable = bool.TryParse(config["Dependencies:IsFfmpegAvailable"], out var ifa) && ifa,
                FfmpegVersion = config["Dependencies:FfmpegVersion"] ?? "",
                
                // [Analysis]
                MaxConcurrentAnalyses = int.TryParse(config["Analysis:MaxConcurrentAnalyses"], out var mca) ? mca : 0,
                
                // [Window]
                WindowWidth = double.TryParse(config["Window:Width"], out var ww) ? ww : 1400,
                WindowHeight = double.TryParse(config["Window:Height"], out var wh) ? wh : 900,
                WindowX = double.TryParse(config["Window:X"], out var wx) ? wx : double.NaN,
                WindowY = double.TryParse(config["Window:Y"], out var wy) ? wy : double.NaN,
                WindowMaximized = bool.TryParse(config["Window:Maximized"], out var wm) && wm,

                // [Dashboard]
                DashboardRightPanelWidth = double.TryParse(config["Dashboard:RightPanelWidth"], out var rpw) ? rpw : 320,
                DashboardIsNavigationCollapsed = bool.TryParse(config["Dashboard:IsNavigationCollapsed"], out var dnc) && dnc,
                DashboardIsRightPanelOpen = !bool.TryParse(config["Dashboard:IsRightPanelOpen"], out var drpo) || drpo,

                // [Import]
                ImportWebShortcuts = ParseImportWebShortcuts(config["Import:WebShortcutsJson"])
            };
            
            // Apply defaults if loaded values are empty (for backward compatibility with old configs)
            if (string.IsNullOrEmpty(_config.SoulseekServer)) _config.SoulseekServer = "server.slsknet.org";
        }
        else
        {
            _config = new AppConfig();
        }

        return _config;
    }

    /// <summary>
    /// Saves configuration to file.
    /// </summary>
    public void Save(AppConfig config)
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (directory != null)
            Directory.CreateDirectory(directory);

        var iniContent = new System.Text.StringBuilder();
        iniContent.AppendLine("[Soulseek]");
        iniContent.AppendLine($"Server = {config.SoulseekServer}");
        iniContent.AppendLine($"Port = {config.SoulseekPort}");
        iniContent.AppendLine($"Username = {config.Username}");
        iniContent.AppendLine($"ListenPort = {config.ListenPort}");
        iniContent.AppendLine($"UseUPnP = {config.UseUPnP}");
        iniContent.AppendLine($"ConnectTimeout = {config.ConnectTimeout}");
        iniContent.AppendLine($"SearchTimeout = {config.SearchTimeout}");
        iniContent.AppendLine($"RememberPassword = {config.RememberPassword}");
        iniContent.AppendLine($"AutoConnectEnabled = {config.AutoConnectEnabled}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Download]");
        iniContent.AppendLine($"Directory = {config.DownloadDirectory}");
        iniContent.AppendLine($"SharedFolder = {config.SharedFolderPath}");
        iniContent.AppendLine($"MaxConcurrentDownloads = {config.MaxConcurrentDownloads}");
        iniContent.AppendLine($"NameFormat = {config.NameFormat}");
        iniContent.AppendLine($"CheckForDuplicates = {config.CheckForDuplicates}");
        iniContent.AppendLine($"SearchLengthToleranceSeconds = {config.SearchLengthToleranceSeconds}");
        iniContent.AppendLine($"FuzzyMatchEnabled = {config.FuzzyMatchEnabled}");
        iniContent.AppendLine($"MaxSearchAttempts = {config.MaxSearchAttempts}");
        iniContent.AppendLine($"AutoRetryFailedDownloads = {config.AutoRetryFailedDownloads}");
        iniContent.AppendLine($"MaxDownloadRetries = {config.MaxDownloadRetries}");
        iniContent.AppendLine($"EnableMp3Fallback = {config.EnableMp3Fallback}");


        iniContent.AppendLine();
        iniContent.AppendLine("[Search]");
        iniContent.AppendLine($"EnableFuzzyNormalization = {config.EnableFuzzyNormalization}");
        iniContent.AppendLine($"EnableRelaxationStrategy = {config.EnableRelaxationStrategy}");
        iniContent.AppendLine($"EnableVbrFraudDetection = {config.EnableVbrFraudDetection}");
        iniContent.AppendLine($"RelaxationTimeoutSeconds = {config.RelaxationTimeoutSeconds}");
        iniContent.AppendLine($"PreferredFormats = {(config.PreferredFormats != null ? string.Join(",", config.PreferredFormats) : "aiff,aif,flac,wav")}");
        iniContent.AppendLine($"PreferredMinBitrate = {config.PreferredMinBitrate}");
        iniContent.AppendLine($"PreferredMaxBitrate = {config.PreferredMaxBitrate}");
        iniContent.AppendLine($"SearchResponseLimit = {config.SearchResponseLimit}");
        iniContent.AppendLine($"SearchFileLimit = {config.SearchFileLimit}");
        iniContent.AppendLine($"MaxPeerQueueLength = {config.MaxPeerQueueLength}");
        iniContent.AppendLine($"MaxConcurrentSearches = {config.MaxConcurrentSearches}");
        iniContent.AppendLine($"MaxDiscoveryLanes = {config.MaxDiscoveryLanes}");
        iniContent.AppendLine($"MaxSearchVariations = {config.MaxSearchVariations}");
        iniContent.AppendLine($"StrictSearchSufficientResultCount = {Math.Max(1, config.StrictSearchSufficientResultCount)}");
        iniContent.AppendLine($"EnableStrictHighConfidenceShortCircuit = {config.EnableStrictHighConfidenceShortCircuit}");
        iniContent.AppendLine($"EnableSearchLoadShedding = {config.EnableSearchLoadShedding}");
        iniContent.AppendLine($"ElevatedSearchPressureActiveSearches = {Math.Max(1, config.ElevatedSearchPressureActiveSearches)}");
        iniContent.AppendLine($"CriticalSearchPressureActiveSearches = {Math.Max(config.ElevatedSearchPressureActiveSearches, config.CriticalSearchPressureActiveSearches)}");
        iniContent.AppendLine($"ElevatedSearchResponseLimitPercent = {Math.Clamp(config.ElevatedSearchResponseLimitPercent, 10, 100)}");
        iniContent.AppendLine($"CriticalSearchResponseLimitPercent = {Math.Clamp(config.CriticalSearchResponseLimitPercent, 10, 100)}");
        iniContent.AppendLine($"ElevatedSearchFileLimitPercent = {Math.Clamp(config.ElevatedSearchFileLimitPercent, 10, 100)}");
        iniContent.AppendLine($"CriticalSearchFileLimitPercent = {Math.Clamp(config.CriticalSearchFileLimitPercent, 10, 100)}");
        iniContent.AppendLine($"ElevatedSearchExtraDelayMs = {Math.Max(0, config.ElevatedSearchExtraDelayMs)}");
        iniContent.AppendLine($"CriticalSearchExtraDelayMs = {Math.Max(0, config.CriticalSearchExtraDelayMs)}");

        iniContent.AppendLine();
        iniContent.AppendLine("[MusicalIntelligence]");
        iniContent.AppendLine($"RankingProfile = {config.RankingProfile}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Spotify]");
        iniContent.AppendLine($"SpotifyClientId = {config.SpotifyClientId}");
        iniContent.AppendLine($"SpotifyClientSecret = {config.SpotifyClientSecret}");
        iniContent.AppendLine($"SpotifyUsePublicOnly = {config.SpotifyUsePublicOnly}");
        iniContent.AppendLine($"SpotifyCallbackPort = {config.SpotifyCallbackPort}");
        iniContent.AppendLine($"SpotifyRedirectUri = {config.SpotifyRedirectUri}");
        iniContent.AppendLine($"SpotifyRememberAuth = {config.SpotifyRememberAuth}");
        iniContent.AppendLine($"MetadataEnrichmentEnabled = {config.SpotifyUseApi}");
        iniContent.AppendLine($"ClearSpotifyOnExit = {config.ClearSpotifyOnExit}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Library]");
        iniContent.AppendLine($"ColumnOrder = {config.LibraryColumnOrder}");
        iniContent.AppendLine($"NavigationCollapsed = {config.LibraryNavigationCollapsed}");
        iniContent.AppendLine($"NavigationAutoHideEnabled = {config.LibraryNavigationAutoHideEnabled}");
        iniContent.AppendLine($"NavigationAutoHideActivationToggleCount = {Math.Max(2, config.LibraryNavigationAutoHideActivationToggleCount)}");
        iniContent.AppendLine($"UpgradeScoutEnabled = {config.UpgradeScoutEnabled}");
        iniContent.AppendLine($"UpgradeMinBitrateThreshold = {config.UpgradeMinBitrateThreshold}");
        iniContent.AppendLine($"UpgradeMinGainKbps = {config.UpgradeMinGainKbps}");
        iniContent.AppendLine($"UpgradeAutoQueueEnabled = {config.UpgradeAutoQueueEnabled}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Dependencies]");
        iniContent.AppendLine($"IsFfmpegAvailable = {config.IsFfmpegAvailable}");
        iniContent.AppendLine($"FfmpegVersion = {config.FfmpegVersion}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Analysis]");
        iniContent.AppendLine($"MaxConcurrentAnalyses = {config.MaxConcurrentAnalyses}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Window]");
        iniContent.AppendLine($"Width = {config.WindowWidth}");
        iniContent.AppendLine($"Height = {config.WindowHeight}");
        iniContent.AppendLine($"X = {config.WindowX}");
        iniContent.AppendLine($"Y = {config.WindowY}");
        iniContent.AppendLine($"Maximized = {config.WindowMaximized}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Dashboard]");
        iniContent.AppendLine($"RightPanelWidth = {config.DashboardRightPanelWidth}");
        iniContent.AppendLine($"IsNavigationCollapsed = {config.DashboardIsNavigationCollapsed}");
        iniContent.AppendLine($"IsRightPanelOpen = {config.DashboardIsRightPanelOpen}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Import]");
        iniContent.AppendLine($"WebShortcutsJson = {SerializeImportWebShortcuts(config.ImportWebShortcuts)}");

        File.WriteAllText(_configPath, iniContent.ToString());
        _config = config;
    }

    public AppConfig GetCurrent() => _config;

    public async Task SaveAsync(AppConfig config)
    {
        await Task.Run(() => Save(config));
    }

    private static List<string> ParseImportWebShortcuts(string? json)
    {
        var fallback = new List<string>
        {
            "1001Tracklists|https://www.1001tracklists.com/",
            "Beatport|https://www.beatport.com/",
            "SoundCloud|https://soundcloud.com/"
        };

        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            return parsed == null || parsed.Count == 0 ? fallback : parsed;
        }
        catch
        {
            return fallback;
        }
    }

    private static string SerializeImportWebShortcuts(List<string>? items)
    {
        var value = items?.Where(i => !string.IsNullOrWhiteSpace(i)).ToList() ?? new List<string>();
        return JsonSerializer.Serialize(value);
    }
}
