namespace SLSKDONET.Configuration;

/// <summary>
/// User-configurable weights for the ranking algorithm.
/// These values multiply the raw scores from ScoringConstants.
/// </summary>
public class ScoringWeights
{
    public double AvailabilityWeight { get; set; } = 1.0;
    public double QualityWeight { get; set; } = 1.0;
    public double MusicalWeight { get; set; } = 1.0;
    public double MetadataWeight { get; set; } = 1.0;
    public double StringWeight { get; set; } = 1.0;
    public double ConditionsWeight { get; set; } = 1.0;

    /// <summary>
    /// Creates a default weight set for the "Balanced" preset.
    /// </summary>
    public static ScoringWeights Balanced => new()
    {
        AvailabilityWeight = 1.0,
        QualityWeight = 1.0,
        MusicalWeight = 1.0,
        MetadataWeight = 1.0,
        StringWeight = 1.0,
        ConditionsWeight = 1.0
    };

    /// <summary>
    /// Creates a weight set that prioritizes audio quality.
    /// </summary>
    public static ScoringWeights QualityFirst => new()
    {
        AvailabilityWeight = 1.0,
        QualityWeight = 2.0,
        MusicalWeight = 0.5,
        MetadataWeight = 1.0,
        StringWeight = 1.0,
        ConditionsWeight = 1.0
    };

    /// <summary>
    /// Creates a weight set that prioritizes musical alignment (BPM/Key).
    /// </summary>
    public static ScoringWeights DjMode => new()
    {
        AvailabilityWeight = 1.0,
        QualityWeight = 0.5,
        MusicalWeight = 2.0,
        MetadataWeight = 1.0,
        StringWeight = 1.5,
        ConditionsWeight = 1.0
    };
}
