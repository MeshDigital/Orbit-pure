using SLSKDONET.Models;

namespace SLSKDONET.Configuration;


/// <summary>
/// Application configuration settings.
/// </summary>
public class AppConfig
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool RememberPassword { get; set; }
    public bool AutoConnectEnabled { get; set; }
    public int ListenPort { get; set; } = 49998;
    public bool UseUPnP { get; set; } = false;
    public int ConnectTimeout { get; set; } = 20000; // ms
    public int SearchTimeout { get; set; } = 6000; // ms
    public int MaxConcurrentSearches { get; set; } = 4; // Throttling to prevent bans
    public string? DownloadDirectory { get; set; }
    public string? SharedFolderPath { get; set; }
    public int MaxConcurrentDownloads { get; set; } = 5; // Optimized: was 2, increased for better throughput
    public string? NameFormat { get; set; } = "{artist|filename} - {title}";
    public bool CheckForDuplicates { get; set; } = true;

    // Soulseek Network Settings (matches Soulseek.NET library defaults)
    public string SoulseekServer { get; set; } = "server.slsknet.org"; 
    public int SoulseekPort { get; set; } = 2242;
    public int SoulseekMinorVersion { get; set; } = 2026;
    
    // File preference conditions
    public List<string>? PreferredFormats { get; set; } = new() { "mp3", "flac" };
    public int PreferredMinBitrate { get; set; } = 96; // Lowered from 128 to reduce rejections
    public int PreferredMaxBitrate { get; set; } = 0; // 0 = no limit
    public int PreferredMaxSampleRate { get; set; } = 48000; // Hz
    public string? PreferredLengthTolerance { get; set; } = "3"; // seconds
    
    // Spotify integration
    public string? SpotifyClientId { get; set; }
    public string? SpotifyClientSecret { get; set; }
    public bool SpotifyUseApi { get; set; } = true; // Circuit Breaker handles 403s gracefully
    public bool SpotifyUsePublicOnly { get; set; } = false; // Use authenticated API for accurate searches
    
    // Spotify OAuth settings
    public string SpotifyRedirectUri { get; set; } = "http://127.0.0.1:5000/callback";
    public int SpotifyCallbackPort { get; set; } = 5000;
    public bool SpotifyRememberAuth { get; set; } = true; // Store refresh token by default
        public bool ClearSpotifyOnExit { get; set; } = false; // Diagnostic: Clear cached tokens on app close
    
    // Search and download preferences
    public int SearchLengthToleranceSeconds { get; set; } = 3; // Allow +/- 3 seconds duration mismatch
    public bool FuzzyMatchEnabled { get; set; } = true; // Enable fuzzy matching for search results
    public int MaxSearchAttempts { get; set; } = 3; // Max progressive search attempts per track
    public bool AutoRetryFailedDownloads { get; set; } = true;
    public int MaxDownloadRetries { get; set; } = 2;
    // Brain 2.0 & Quality Guard
    public SearchPolicy SearchPolicy { get; set; } = SearchPolicy.QualityFirst(); // [NEW] The "Biggers App" Search Policy

    public bool EnableFuzzyNormalization { get; set; } = true; // Strip special chars, normalize feat.
    public bool EnableRelaxationStrategy { get; set; } = true; // Progressive threshold widening
    public bool EnableVbrFraudDetection { get; set; } = true; // Upscale protection
    public int RelaxationTimeoutSeconds { get; set; } = 5; // Optimized: was 10s, reduced for faster fallback
    public bool IsAutoEnrichEnabled { get; set; } = true; // Phase 8: Auto-Enrich on completion
    
    // Phase 10: Profile Persistence
    public string? RankingProfile { get; set; } = "Balanced"; // Persists "Quality First", "DJ Mode", etc.

    [Obsolete("Use SearchPolicy instead. Kept for migration stability.")]
    public ScoringWeights CustomWeights { get; set; } = ScoringWeights.Balanced; // DEPRECATED
    
    // Window state persistence
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public double WindowX { get; set; } = double.NaN; // NaN means center
    public double WindowY { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = false;

    // Library Management
    public List<string> LibraryRootPaths { get; set; } = new(); // Root directories to scan for music files
    public bool EnableFilePathResolution { get; set; } = true; // Enable automatic resolution of moved files
    public double FuzzyMatchThreshold { get; set; } = 0.70; // Minimum similarity score (0.0-1.0) for fuzzy matching
    
    // Gatekeeper
    public List<string> BlacklistedUsers { get; set; } = new(); // Users to strictly ignore (e.g. ad-bots, fakes)
    
    // Library UI - Column Order Persistence
    public string LibraryColumnOrder { get; set; } = ""; // Comma-separated column IDs (empty = use default)

    // Phase 8: Upgrade Scout (Self-Healing Library)
    public bool UpgradeScoutEnabled { get; set; } = false; // Background upgrading
    public int UpgradeMinBitrateThreshold { get; set; } = 320; // Upgrade everything below this
    public int UpgradeMinGainKbps { get; set; } = 128; // Only upgrade if gain is significant
    public bool UpgradeAutoQueueEnabled { get; set; } = false; // Auto-queue vs just notify

    // Phase 8: Dependency Management
    public bool IsFfmpegAvailable { get; set; } = false; // Updated on startup and manual checks
    public string FfmpegVersion { get; set; } = ""; // Detected version (e.g., "6.0.1")

    // Audio Analysis Parallelism
    public int MaxConcurrentAnalyses { get; set; } = 0; // 0 = auto-detect based on CPU/RAM, 1 = sequential, >1 = parallel

    public override string ToString()
    {
        return $"AppConfig(User={Username}, Port={ListenPort}, Downloads={DownloadDirectory})";
    }
}
