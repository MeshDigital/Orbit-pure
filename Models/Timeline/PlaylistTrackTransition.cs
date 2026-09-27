using System;

namespace SLSKDONET.Models.Timeline;

/// <summary>
/// Domain-model wrapper around <see cref="SLSKDONET.Data.Entities.PlaylistTrackTransitionEntity"/> —
/// the persisted Mix transition for one adjacent track pair in a playlist.
/// </summary>
public class PlaylistTrackTransition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlaylistId { get; set; }
    public Guid OutgoingPlaylistTrackId { get; set; }
    public Guid IncomingPlaylistTrackId { get; set; }
    public string PresetName { get; set; } = "Auto";
    public TransitionType Type { get; set; } = TransitionType.Crossfade;
    public int DurationBars { get; set; } = 16;
    public float? EchoDecayFactor { get; set; }
    public float? FilterStartFrequency { get; set; }
    public float? FilterEndFrequency { get; set; }
    public float? EqLowGain { get; set; }
    public float? EqMidGain { get; set; }
    public float? EqHighGain { get; set; }
    public float? WaveDuckDepth { get; set; }
    public bool? FilterSweepRising { get; set; }
    public bool? EqSwapLow { get; set; }
    public bool? EqSwapMid { get; set; }
    public bool? EqSwapHigh { get; set; }
    public float? EqLowCrossoverHz { get; set; }
    public float? EqHighCrossoverHz { get; set; }
    public int? LoopBars { get; set; }
    public int? LoopRepeats { get; set; }

    /// <summary>Seconds into the outgoing track where the mix-out begins — see
    /// <see cref="SLSKDONET.Engine.Transitions.TransitionEngine.OptimizeTransition"/>.</summary>
    public double? SourceTriggerSeconds { get; set; }

    /// <summary>Seconds into the incoming track where playback starts.</summary>
    public double? TargetTriggerSeconds { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Bars → beats (4 beats per bar) for the DSP layer, which works in beats.</summary>
    public double DurationBeats => DurationBars * 4.0;

    /// <summary>Builds the DSP-facing model consumed by <see cref="SLSKDONET.Services.Timeline.TransitionDsp"/>.</summary>
    public TransitionModel ToTransitionModel()
    {
        var model = new TransitionModel
        {
            Type = Type,
            DurationBeats = DurationBeats,
        };

        if (EchoDecayFactor.HasValue) model.EchoDecayFactor = EchoDecayFactor.Value;
        if (FilterStartFrequency.HasValue) model.FilterStartFrequency = FilterStartFrequency.Value;
        if (FilterEndFrequency.HasValue) model.FilterEndFrequency = FilterEndFrequency.Value;
        if (WaveDuckDepth.HasValue) model.WaveDuckDepth = WaveDuckDepth.Value;
        if (FilterSweepRising.HasValue) model.FilterSweepRising = FilterSweepRising.Value;
        if (EqSwapLow.HasValue) model.EqSwapLow = EqSwapLow.Value;
        if (EqSwapMid.HasValue) model.EqSwapMid = EqSwapMid.Value;
        if (EqSwapHigh.HasValue) model.EqSwapHigh = EqSwapHigh.Value;
        if (EqLowCrossoverHz.HasValue) model.EqLowCrossoverHz = EqLowCrossoverHz.Value;
        if (EqHighCrossoverHz.HasValue) model.EqHighCrossoverHz = EqHighCrossoverHz.Value;
        if (LoopBars.HasValue) model.LoopBars = LoopBars.Value;
        if (LoopRepeats.HasValue) model.LoopRepeats = LoopRepeats.Value;

        return model;
    }
}
