using Microsoft.Extensions.Configuration;
using System.IO;

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
                PreferredFormats = config["Search:PreferredFormats"]?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? new List<string> { "mp3", "flac" },
                PreferredMinBitrate = int.TryParse(config["Search:PreferredMinBitrate"], out var pmb) ? pmb : 96,
                PreferredMaxBitrate = int.TryParse(config["Search:PreferredMaxBitrate"], out var pmaxb) ? pmaxb : 0,

                // [Library] & Upgrade Scout
                LibraryColumnOrder = config["Library:ColumnOrder"] ?? "",
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
                WindowMaximized = bool.TryParse(config["Window:Maximized"], out var wm) && wm
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

        iniContent.AppendLine();
        iniContent.AppendLine("[Search]");
        iniContent.AppendLine($"EnableFuzzyNormalization = {config.EnableFuzzyNormalization}");
        iniContent.AppendLine($"EnableRelaxationStrategy = {config.EnableRelaxationStrategy}");
        iniContent.AppendLine($"EnableVbrFraudDetection = {config.EnableVbrFraudDetection}");
        iniContent.AppendLine($"RelaxationTimeoutSeconds = {config.RelaxationTimeoutSeconds}");
        iniContent.AppendLine($"PreferredFormats = {(config.PreferredFormats != null ? string.Join(",", config.PreferredFormats) : "mp3,flac")}");
        iniContent.AppendLine($"PreferredMinBitrate = {config.PreferredMinBitrate}");
        iniContent.AppendLine($"PreferredMaxBitrate = {config.PreferredMaxBitrate}");

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

        File.WriteAllText(_configPath, iniContent.ToString());
        _config = config;
    }

    public AppConfig GetCurrent() => _config;

    public async Task SaveAsync(AppConfig config)
    {
        await Task.Run(() => Save(config));
    }
}
