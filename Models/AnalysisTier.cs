namespace SLSKDONET.Models;

/// <summary>
/// Defines the level of depth for audio analysis.
/// </summary>
public enum AnalysisTier
{
    /// <summary>
    /// Tier 1: Basic analysis (BPM, Key, Basic Mood, Danceability). Fast.
    /// </summary>
    Tier1 = 1,

    /// <summary>
    /// Tier 2: Detailed analysis (Vocal/Instrumental, Tonal/Atonal, Advanced Mood). Medium.
    /// </summary>
    Tier2 = 2,

    /// <summary>
    /// Tier 3: Specialized analysis (Pitch/CREPE, Source Separation/Spleeter). Slow/Heavy.
    /// </summary>
    Tier3 = 3
}
