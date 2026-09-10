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

        return model;
    }
}
