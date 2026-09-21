namespace SLSKDONET.Engine.Snapping;

/// <summary>
/// Model representing a detected transient point with its clustered acoustic class.
/// </summary>
public sealed class TransientDataPoint
{
    public double Timestamp { get; set; }
    public string ClusterClass { get; set; } = string.Empty; // e.g., "Kick", "Snare", "Perc", "FX"
}
