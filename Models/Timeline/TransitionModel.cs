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

    /// <summary>Low-pass (or, with <see cref="TransitionModel.FilterSweepRising"/>, high-pass) filter sweep over the transition window.</summary>
    FilterSweep,

    /// <summary>Bass handover: low band swaps from outgoing to incoming while mids/highs crossfade normally — avoids muddy bass collision.</summary>
    EqSwap,

    /// <summary>Rhythmic gain ducking on the downbeat of every bar through the window, for a pumping/sidechain-style handover.</summary>
    WaveDuck
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

    /// <summary>
    /// When true, <see cref="TransitionType.FilterSweep"/> runs as a rising high-pass sweep on
    /// the incoming clip (energy build into the drop) instead of a falling low-pass sweep on
    /// the outgoing clip. Used by the "Rise" preset.
    /// </summary>
    public bool FilterSweepRising { get; set; } = false;

    /// <summary>
    /// Duck depth (0-1) for <see cref="TransitionType.WaveDuck"/> — how far gain dips on each
    /// downbeat. 0 = no ducking, 1 = full silence at the dip.
    /// </summary>
    public float WaveDuckDepth { get; set; } = 0.5f;

    // ── EqSwap ("Blend") band config — mirrors Services.Audio.EqBandSwapConfig's defaults ──

    /// <summary>Whether the Low band swaps from outgoing to incoming for <see cref="TransitionType.EqSwap"/>.</summary>
    public bool EqSwapLow { get; set; } = true;

    /// <summary>Whether the Mid band swaps.</summary>
    public bool EqSwapMid { get; set; } = false;

    /// <summary>Whether the High band swaps.</summary>
    public bool EqSwapHigh { get; set; } = false;

    /// <summary>Low/mid crossover frequency (Hz).</summary>
    public float EqLowCrossoverHz { get; set; } = 250f;

    /// <summary>Mid/high crossover frequency (Hz).</summary>
    public float EqHighCrossoverHz { get; set; } = 4000f;
}
