namespace SLSKDONET.Models.Stem;

public class StemSettings
{
    /// <summary>
    /// Volume multiplier (0.0 to 1.0+). Default is 1.0.
    /// </summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>
    /// Pan value from -1.0 (Left) to 1.0 (Right). Default is 0.0.
    /// </summary>
    public float Pan { get; set; } = 0.0f;

    public bool IsMuted { get; set; }
    public bool IsSolo { get; set; }

    // Placeholder for EQ or Effect references
    // public EqSettings Eq { get; set; }
}
