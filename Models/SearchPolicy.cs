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
    /// Returns the standard QualityFirst search policy (Epic #543 / #546 SQ2).
    /// </summary>
    public static SearchPolicy QualityFirst() => new()
    {
        Priority = SearchPriority.QualityFirst,
        PreferSpeedOverQuality = false,
        EnforceFileIntegrity = true,
        EnforceStrictTitleMatch = true,
        EnforceDurationMatch = true
    };
}

public enum SearchPriority
{
    /// <summary>
    /// Prioritize Bitrate, Format (FLAC > MP3), and Integrity.
    /// </summary>
    QualityFirst
}
