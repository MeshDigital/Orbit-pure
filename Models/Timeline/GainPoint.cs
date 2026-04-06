namespace SLSKDONET.Models.Timeline;

/// <summary>
/// A single automation point on a clip's gain envelope.
/// Beat position is project-BPM-relative, GainDb applies at that beat.
/// Values between adjacent points are linearly interpolated.
/// </summary>
public class GainPoint
{
    public double BeatPosition { get; set; }
    public float GainDb { get; set; }
}
