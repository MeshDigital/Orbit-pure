namespace SLSKDONET.Models.Timeline;

/// <summary>
/// DJ-style transition type applied at a clip boundary.
/// </summary>
public enum TransitionType
{
    /// <summary>Hard cut — no overlap, signal switches instantly.</summary>
    Cut,

    /// <summary>Equal-power crossfade between outgoing and incoming clips.</summary>
    Crossfade,

    /// <summary>Decaying echo on the outgoing clip while incoming fades in.</summary>
    EchoOut,

    /// <summary>Low-pass filter sweeps down on outgoing clip over the transition window.</summary>
    FilterSweep
}

/// <summary>
/// Describes the transition applied at the <em>end</em> of a <see cref="TimelineClip"/>.
/// The <see cref="DurationBeats"/> window is shared between the two clips (overlap zone).
/// </summary>
public class TransitionModel
{
    public TransitionType Type { get; set; } = TransitionType.Crossfade;

    /// <summary>Transition window length in beats.</summary>
    public double DurationBeats { get; set; } = 4.0;

    /// <summary>
    /// Echo decay factor for <see cref="TransitionType.EchoOut"/>.
    /// 0 = instant silence; 1 = no decay (infinite sustain).
    /// Typical value: 0.5–0.7.
    /// </summary>
    public float EchoDecayFactor { get; set; } = 0.55f;

    /// <summary>
    /// Starting frequency (Hz) for <see cref="TransitionType.FilterSweep"/>.
    /// Sweep runs from this frequency down to <see cref="FilterEndFrequency"/>.
    /// </summary>
    public float FilterStartFrequency { get; set; } = 20_000f;

    /// <summary>
    /// Ending frequency (Hz) for <see cref="TransitionType.FilterSweep"/>.
    /// </summary>
    public float FilterEndFrequency { get; set; } = 200f;
}
