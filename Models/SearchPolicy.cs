namespace SLSKDONET.Models;

/// <summary>
/// Defines the high-level strategy for searching and ranking tracks.
/// Replaces the legacy "ScoringWeights" system with clear, intent-based policies.
/// </summary>
public class SearchPolicy
{
    /// <summary>
    /// The primary goal of the search strategy.
    /// </summary>
    public SearchPriority Priority { get; set; } = SearchPriority.QualityFirst;

    // Behavior Modifiers
    
    /// <summary>
    /// If true, widens acceptance criteria (e.g. bitrate, duration tolerance) to find matches faster.
    /// Useful for casual listening or when specific versions aren't critical.
    /// </summary>
    public bool PreferSpeedOverQuality { get; set; } = false;

    // Safety Gates (The Gatekeeper)

    /// <summary>
    /// REJECT if file integrity check fails (e.g., fake FLAC, upscaled MP3).
    /// </summary>
    public bool EnforceFileIntegrity { get; set; } = true;

    /// <summary>
    /// REJECT if filename does not contain ALL query tokens.
    /// Prevents "Artist A - Title B" matching "Artist A - Title C".
    /// </summary>
    public bool EnforceStrictTitleMatch { get; set; } = true;

    /// <summary>
    /// REJECT if duration differs significantly from the target metadata.
    /// </summary>
    public bool EnforceDurationMatch { get; set; } = true;

    // Thresholds & Tolerances

    /// <summary>
    /// Maximum allowed difference in seconds between search result and target metadata.
    /// </summary>
    public int DurationToleranceSeconds { get; set; } = 4; // Slightly lenient by default

    /// <summary>
    /// Bitrate difference considered "significant" enough to override other factors.
    /// e.g., 320kbps vs 192kbps diff is 128, which is > 64.
    /// </summary>
    public int SignificantBitrateGap { get; set; } = 64;

    /// <summary>
    /// Queue length difference considered "significant" enough to prefer the faster peer.
    /// </summary>
    public int SignificantQueueGap { get; set; } = 50;

    /// <summary>
    /// Returns a default policy for "Audiophile" users (Quality First).
    /// </summary>
    public static SearchPolicy QualityFirst() => new()
    {
        Priority = SearchPriority.QualityFirst,
        PreferSpeedOverQuality = false,
        EnforceFileIntegrity = true,
        EnforceStrictTitleMatch = true,
        EnforceDurationMatch = true
    };

    /// <summary>
    /// Returns a default policy for "DJ Mode" users (Key/BPM Metadata First).
    /// </summary>
    public static SearchPolicy DjReady() => new()
    {
        Priority = SearchPriority.DjReady,
        PreferSpeedOverQuality = false,
        EnforceFileIntegrity = true,
        EnforceStrictTitleMatch = true,
        EnforceDurationMatch = true,
        DurationToleranceSeconds = 15 // Allow for extended mixes vs radio edits
    };
    
    /// <summary>
    /// Returns a default policy for "Data Saver" users (Speed/Size First).
    /// </summary>
    public static SearchPolicy DataSaver() => new()
    {
        Priority = SearchPriority.QualityFirst, // Still want decent quality
        PreferSpeedOverQuality = true,
        EnforceFileIntegrity = false, // Less strict about perfect transcodes
        EnforceStrictTitleMatch = true,
        EnforceDurationMatch = true
    };



    /// <summary>
    /// Preferred minimum bitrate for this policy.
    /// </summary>
    public int PreferredMinBitrate { get; set; } = 320;

    /// <summary>
    /// Configuration for the progressive relaxation strategy.
    /// </summary>
    public RelaxationSettings RelaxationParams { get; set; } = new();
}

public class RelaxationSettings
{
    public bool Enabled { get; set; } = true;
    public int InitialTimeoutSeconds { get; set; } = 5;
    public int FallbackBitrate { get; set; } = 192;
}

public enum SearchPriority
{
    /// <summary>
    /// Prioritize Bitrate, Format (FLAC > MP3), and Integrity.
    /// </summary>
    QualityFirst,

    /// <summary>
    /// Prioritize tracks with valid BPM/Key metadata, then Quality.
    /// </summary>
    DjReady
}
