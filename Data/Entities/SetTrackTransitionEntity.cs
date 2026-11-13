using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// A saved transition between two adjacent slots of a set (rebuild plan P3.2): the mirror of
/// <see cref="PlaylistTrackTransitionEntity"/> keyed by set slots instead of playlist tracks, so
/// a set's transitions never collide with a playlist's. Same columns minus the playlist table's
/// always-null EQ gain fields. Read and written through <c>SetTransitionRepository</c>, which
/// speaks the shared <c>PlaylistTrackTransition</c> model the transition editor uses.
/// </summary>
[Table("SetTrackTransitions")]
public class SetTrackTransitionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SetId { get; set; }            // FK → SetLists (cascade)
    public Guid OutgoingSlotId { get; set; }   // FK → SetTracks (cascade)
    public Guid IncomingSlotId { get; set; }   // FK → SetTracks (cascade)

    [Required] public string PresetName { get; set; } = "Auto";
    [Required] public string TransitionType { get; set; } = "Crossfade";
    public int DurationBars { get; set; } = 16;

    public float? EchoDecayFactor { get; set; }
    public float? FilterStartFrequency { get; set; }
    public float? FilterEndFrequency { get; set; }
    public float? WaveDuckDepth { get; set; }
    public bool? FilterSweepRising { get; set; }
    public bool? EqSwapLow { get; set; }
    public bool? EqSwapMid { get; set; }
    public bool? EqSwapHigh { get; set; }
    public float? EqLowCrossoverHz { get; set; }
    public float? EqHighCrossoverHz { get; set; }
    public int? LoopBars { get; set; }
    public int? LoopRepeats { get; set; }
    public double? SourceTriggerSeconds { get; set; }
    public double? TargetTriggerSeconds { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
