using System.Security.Cryptography;
using System.Text;
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
    public int ConnectTimeout { get; set; } = 60000; // ms
    public int SearchTimeout { get; set; } = 12000; // ms; longer idle window improves late peer discovery for lossless searches
    public int SearchAccumulatorWindowSeconds { get; set; } = 15; // was 30 — cap desperate-lane accumulation; accumulatorShortCircuit handles early exits for normal lanes
    public int MaxConcurrentSearches { get; set; } = 5; // Throttling to prevent bans
    public int MaxDiscoveryLanes { get; set; } = 5; // Concurrent discovery jobs for seeker pipeline
    public int MaxSearchVariations { get; set; } = 3; // Cap cascade fan-out to avoid flooding (3 = Strict/Standard/Desperate)
    public int StrictSearchSufficientResultCount { get; set; } = 5; // Strict-first: skip relaxed variations when enough strict hits were found
    public bool EnableStrictHighConfidenceShortCircuit { get; set; } = true; // Skip relaxed variations when strict lane yields a high-confidence winner
    public bool EnableStrictSufficientResultShortCircuit { get; set; } = true; // Stop after strict variation when enough good results found
    public bool EnableFastClearanceEarlyExit { get; set; } = true; // Fast-clearance yields first idle-peer winner immediately
    public int SearchThrottleDelayMs { get; set; } = 200; // Protocol pacing to prevent flood protection
    public bool EnableSearchLoadShedding { get; set; } = true;
    // Elevated: fires when enough parallel searches are running that we risk Soulseek throttling.
    // With MaxDiscoveryLanes=5 and typical multi-track batches, keep this above the lane count.
    public int ElevatedSearchPressureActiveSearches { get; set; } = 6;   // was 3 — too low, triggered on any modest batch
    public int CriticalSearchPressureActiveSearches { get; set; } = 10;  // was 5 — caused variationCap=1 lockout with only 5 searches
    public int ElevatedSearchResponseLimitPercent { get; set; } = 85;    // was 75
    public int CriticalSearchResponseLimitPercent { get; set; } = 65;    // was 50
    public int ElevatedSearchFileLimitPercent { get; set; } = 85;        // was 75
    public int CriticalSearchFileLimitPercent { get; set; } = 65;        // was 50
    public int ElevatedSearchExtraDelayMs { get; set; } = 75;
    public int CriticalSearchExtraDelayMs { get; set; } = 200;
    public int SearchTokenBucketCapacity { get; set; } = 1;
    public int SearchTokenBucketRefillMs { get; set; } = 3500;
    public int ElevatedSearchTokenBucketRefillMs { get; set; } = 4000;
    public int CriticalSearchTokenBucketRefillMs { get; set; } = 5000;
    public int SearchResponseLimit { get; set; } = 200; // was 100 — at Critical (65%) this gives ~130, enough for meaningful ranking
    public int SearchFileLimit { get; set; } = 200; // was 100
    public int SearchHardResultCap { get; set; } = 10000; // Absolute per-search circuit breaker for accepted candidates
    public int SearchHardFileCap { get; set; } = 50000; // Absolute per-search circuit breaker for inbound files (0 disables)
    public int MaxPeerQueueLength { get; set; } = 50; // Ignore peers with very long queue lengths
    public int MinSearchDurationSeconds { get; set; } = 5; // Brain buffer floor: 5s gives network time to collect results but doesn't make downloads glacially slow
    public int MinLosslessSearchDurationSeconds { get; set; } = 10; // was 20 — accumulator short-circuit handles fast exits; 10s is the fallback for scarce tracks
    public bool EnableSpeculativeEarlyAccept { get; set; } = true; // Accept silver match after minSearchDuration window; avoids waiting full buffer for good-enough results
    public bool EnableAutoAcquireOnImport { get; set; } = false; // Auto-acquire imported Spotify Ghost tracks
    public bool EnableGoldenEarlyExit { get; set; } = true; // Golden FLAC hit (score≥85, >700kbps, queue=0) cancels the lane immediately
    public bool EnableFastLaneEarlyExit { get; set; } = true; // Idle peer winner cancels the lane immediately; avoids waiting for the rest of the stream
    public bool EnableQuickStrikeEarlyExit { get; set; } = true; // Score>95 exits the tier immediately without waiting for full buffer
    public bool EnableAccumulatorPerfectMatchShortCircuit { get; set; } = true; // Breaks collection loop when a perfect candidate arrives; most impactful speed-up
    public bool EnableHedgedSearch { get; set; } = false; // Disabled — causes delays and complexity
    public int HedgedSearchDelaySeconds { get; set; } = 8; // Delay MP3 hedge so FLAC lanes get first chance to settle
    public bool EnableMp3Fallback { get; set; } = true; // Allow MP3 download when lossless is unavailable; set false for strict lossless-only
    public bool EnableHedgedDownloadFailover { get; set; } = false; // Disabled — use single best match only
    public bool IsSoulseekSupporter { get; set; } = false;
    public int SupporterSearchLaneMultiplier { get; set; } = 2;
    public string? DownloadDirectory { get; set; }
    public string? SharedFolderPath { get; set; }
    public bool EnableLibrarySharing { get; set; } = true; // Reciprocal sharing improves reputation
    public int MaxConcurrentDownloads { get; set; } = 5; // Optimized: was 2, increased for better throughput
    public string? NameFormat { get; set; } = "{artist|filename} - {title}";
    public bool CheckForDuplicates { get; set; } = true;
    public List<string> ImportWebShortcuts { get; set; } = new()
    {
        "1001Tracklists|https://www.1001tracklists.com/",
        "Beatport|https://www.beatport.com/",
        "SoundCloud|https://soundcloud.com/"
    };

    // Soulseek Network Settings (matches Soulseek.NET library defaults)
    public string SoulseekServer { get; set; } = "server.slsknet.org"; 
    public int SoulseekPort { get; set; } = 2242;
    public int SoulseekMinorVersion { get; set; } = 2026;
    
    // File preference conditions
    public List<string>? PreferredFormats { get; set; } = new() { "aiff", "aif", "flac", "wav" };
    public int PreferredMinBitrate { get; set; } = 701; // Strict lossless profile: >700kbps
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
    public int PeerConnectFailFastSeconds { get; set; } = 10;
    public int TransferStallTimeoutSeconds { get; set; } = 60;
    public int MaxQueueWaitTimeMinutes { get; set; } = 30; // was 60 — re-discover sooner when a peer's queue stalls
    // Brain 2.0 & Quality Guard
    public SearchPolicy SearchPolicy { get; set; } = SearchPolicy.QualityFirst(); // [NEW] The "Biggers App" Search Policy

    public bool EnableFuzzyNormalization { get; set; } = true; // Strip special chars, normalize feat.
    public bool EnableRelaxationStrategy { get; set; } = false; // Disabled by default — causes delays and fuzzy fallback
    public bool EnableVbrFraudDetection { get; set; } = true; // Upscale protection
    public int RelaxationTimeoutSeconds { get; set; } = 8; // Quality-first default: wait longer before relaxing strict lossless criteria
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

    // Dashboard layout persistence (three-column shell state)
    public double DashboardRightPanelWidth { get; set; } = 320; // Width of the right panel in pixels
    public bool DashboardIsNavigationCollapsed { get; set; } = false; // Whether the left navigation is collapsed
    public bool DashboardIsRightPanelOpen { get; set; } = true; // Whether the right panel is visible

    // Workstation overlay persistence
    public string WorkstationOverlaySizeMode { get; set; } = "Auto"; // Auto | Compact | Comfort | Full | Manual
    public double WorkstationOverlayManualWidth { get; set; } = 0;
    public double WorkstationOverlayManualHeight { get; set; } = 0;
    public bool WorkstationOverlayIsOpen { get; set; } = false;
    public int WorkstationDrawerTabIndex { get; set; } = 0;
    public string? WorkstationActivePlaylistId { get; set; }
    public string WorkstationDensityMode { get; set; } = "Auto"; // Auto | Compact | Normal | Touch

    // Flow Builder persistence
    public string? FlowBuilderSelectedPlaylistId { get; set; }
    public bool FlowBuilderRestoreContentOnStartup { get; set; } = true;
    public bool EnableFlowBuilderSuggestedFlowPreview { get; set; } = true;
    public bool EnableFlowBuilderSuggestedFlowTelemetry { get; set; } = true;
    public int FlowBuilderSuggestedFlowPreviewRolloutPercent { get; set; } = 10;

    // Frequent Sources (privacy-first, local-only, opt-in)
    public bool EnableFrequentSources { get; set; } = false;
    public string FrequentSourcesStagingPath { get; set; } = string.Empty;

    // Five-column desktop layout – Epic 12 (#110)
    public double TimelinePanelWidth { get; set; } = 300;
    public bool IsTimelinePanelOpen { get; set; } = false;
    public double OverlaysPanelWidth { get; set; } = 250;
    public bool IsOverlaysPanelOpen { get; set; } = false;

    // Library Management
    public List<string> LibraryRootPaths { get; set; } = new(); // Root directories to scan for music files
    public bool EnableFilePathResolution { get; set; } = true; // Enable automatic resolution of moved files
    public double FuzzyMatchThreshold { get; set; } = 0.70; // Minimum similarity score (0.0-1.0) for fuzzy matching
    
    // Gatekeeper
    public List<string> BlacklistedUsers { get; set; } = new(); // Users to strictly ignore (e.g. ad-bots, fakes)
    
    // Library UI - Column Order Persistence
    public string LibraryColumnOrder { get; set; } = ""; // Comma-separated column IDs (empty = use default)
    public bool LibraryNavigationCollapsed { get; set; } = false; // Default expanded until user manually collapses
    public bool LibraryNavigationAutoHideEnabled { get; set; } = false; // Disabled by default: explicit user control only
    public int LibraryNavigationAutoHideActivationToggleCount { get; set; } = 3; // Require repeated manual collapses before hover behavior arms
    public bool UseNewPlaylistSurface { get; set; } = false; // Gate 1 rollout flag for ItemsRepeater library surface

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

    // Hyper-Drive: Adaptive lane autotuning
    public bool EnableAdaptiveLanes { get; set; } = true;
    public int MinAdaptiveDownloadLanes { get; set; } = 2;
    public int MaxAdaptiveDownloadLanes { get; set; } = 6;
    public int MinAdaptiveSearchLanes { get; set; } = 3;
    public int MaxAdaptiveSearchLanes { get; set; } = 8;
    public int MinThroughputFloorKbps { get; set; } = 20;
    // StallTimeoutSeconds: how long the heartbeat waits before declaring a DOWNLOADING (not Queued) transfer stalled.
    // 10s was far too aggressive — Soulseek peers often take 15-30s from accepting a slot to first byte.
    // 45s gives enough runway for the peer to start the stream while still catching genuine dead transfers.
    public int StallTimeoutSeconds { get; set; } = 45; // was 10

    // Automatic Downloads Strict Mode (Investigation & Hardening)
    // Opt-in, local-only hardening layer for exact-first, filtered-fallback automatic downloads
    public bool EnableAutoDownloadStrictMode { get; set; } = false; // Disabled by default
    public int AutoDownloadInitialWaitMs { get; set; } = 4000; // Fast window: wait for fast peers (default 3-5s)
    public int AutoDownloadExtendedWaitMs { get; set; } = 20000; // Extended window: fallback search (default 20-30s)
    public List<string>? AutoDownloadAllowedExtensions { get; set; } = new() { "flac", "wav", "aiff", "aif", "ape", "alac" }; // Conservative defaults
    public long AutoDownloadMinFileSizeBytes { get; set; } = 1024 * 500; // 500 KB minimum (anti-stub)
    public int AutoDownloadMinBitrateKbps { get; set; } = 320; // Conservative for fallback tier
    public int AutoDownloadMinMatchScore { get; set; } = 75; // Minimum score required to accept a candidate
    public bool AutoDownloadExactFirstOnly { get; set; } = false; // When true, reject all fuzzy/template matches
    public bool AutoDownloadAllowFuzzyFallback { get; set; } = false; // When false, strict misses do not fall back to legacy fuzzy discovery
    public int AutoDownloadDurationToleranceSeconds { get; set; } = 3; // Duration proximity tolerance for strict candidate filtering/scoring
    public int AutoDownloadMaxCandidatesToScore { get; set; } = 50; // Cap scoring to top 50 for determinism
    public string? AutoDownloadExcludedPhrases { get; set; } = "remix,cover,live,acoustic"; // Comma-separated phrases to exclude
    public bool AutoDownloadDiagnosticsEnabled { get; set; } = false; // Local-only diagnostic logging to PlaylistActivityLogEntity

    // Library smart insert (segment-aware playlist intelligence)
    // Confidence threshold: 0.80 strict, 0.72 normal, 0.65 loose/experimental
    public double LibrarySmartInsertMinConfidence { get; set; } = 0.72;
    // Structure sensitivity slider (0..100): higher values prioritize intro/drop/breakdown/outro continuity
    public int LibrarySmartInsertStructureSensitivity { get; set; } = 55;

    // Cue Forge last session
    public string? CueForgeLastTrackHash { get; set; }
    public string? CueForgeLastTrackTitle { get; set; }
    public string? CueForgeLastTrackArtist { get; set; }

    public override string ToString()
    {
        return $"AppConfig(User={Username}, Port={ListenPort}, Downloads={DownloadDirectory})";
    }

    // ── Automix Configuration (Task 3.4 / #77) ───────────────────────────
    public double AutomixMinBpm { get; set; } = 100;
    public double AutomixMaxBpm { get; set; } = 160;
    public bool   AutomixMatchKey { get; set; } = true;
    public int    AutomixMaxEnergyJump { get; set; } = 3;
    public int    AutomixMaxTracks { get; set; } = 20;
    /// <summary>"None" | "Rising" | "Wave" | "Peak"</summary>
    public string AutomixEnergyCurve { get; set; } = "Wave";
    public double AutomixHarmonicWeight { get; set; } = 3.0;
    public double AutomixTempoWeight    { get; set; } = 1.0;
    public double AutomixEnergyWeight   { get; set; } = 0.5;

    // ── Privacy / Telemetry (Task 19 / Epic #119) ─────────────────────────
    /// <summary>
    /// When true, keyboard action usage is counted locally in LiteDB.
    /// No data is ever sent externally. Default off (opt-in).
    /// </summary>
    public bool EnableKeyboardTelemetry { get; set; } = false;

    public bool IsFlowBuilderPreviewEnabledForThisInstall(string installKey)
    {
        if (!EnableFlowBuilderSuggestedFlowPreview)
            return false;

        if (FlowBuilderSuggestedFlowPreviewRolloutPercent <= 0)
            return false;

        if (FlowBuilderSuggestedFlowPreviewRolloutPercent >= 100)
            return true;

        if (string.IsNullOrWhiteSpace(installKey))
            return false;

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(installKey.Trim()));
        var bucket = hashBytes[0] % 100;
        return bucket < FlowBuilderSuggestedFlowPreviewRolloutPercent;
    }
}
