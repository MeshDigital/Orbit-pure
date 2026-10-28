using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// A saved Mix-style transition between two adjacent tracks in a playlist (Spotify Mix
/// parity — see <see cref="SLSKDONET.Models.Timeline.TransitionModel"/> for the DSP-facing
/// shape this maps onto). Keyed by the outgoing/incoming <c>PlaylistTracks.Id</c> pair rather
/// than track hash, so the same two tracks can carry different transitions in different
/// playlists (or at different adjacent positions within one playlist, if reordered).
/// </summary>
[Table("PlaylistTrackTransitions")]
public class PlaylistTrackTransitionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid PlaylistId { get; set; }

    [Required]
    public Guid OutgoingPlaylistTrackId { get; set; }

    [Required]
    public Guid IncomingPlaylistTrackId { get; set; }

    /// <summary>"Auto" | "Fade" | "Rise" | "Blend" | "Wave" | "Melt" | "Custom".</summary>
    [Required]
    public string PresetName { get; set; } = "Auto";

    /// <summary>Mirrors <see cref="SLSKDONET.Models.Timeline.TransitionType"/> by name (Cut/Crossfade/EchoOut/FilterSweep).</summary>
    [Required]
    public string TransitionType { get; set; } = "Crossfade";

    /// <summary>Transition window length in bars (4/8/16), converted to beats (×4) when built into a TransitionModel.</summary>
    public int DurationBars { get; set; } = 16;

    /// <summary>
    /// Where in the OUTGOING track (seconds) the mix-out begins — from
    /// <see cref="SLSKDONET.Engine.Transitions.TransitionEngine.OptimizeTransition"/>'s cue/tempo/key/vocal-aware
    /// suggestion (falls back to duration-30s when no cue data exists), not simply "near the end of the file".
    /// </summary>
    public double? SourceTriggerSeconds { get; set; }

    /// <summary>
    /// Where in the INCOMING track (seconds) playback starts — same suggestion engine; may land on a
    /// Mix-In cue, the track's intro, or its first Drop cue (tempo jump / harmonic clash / vocal overlap cases).
    /// </summary>
    public double? TargetTriggerSeconds { get; set; }

    /// <summary>Custom-mode override for <see cref="SLSKDONET.Models.Timeline.TransitionModel.EchoDecayFactor"/>; null = preset default.</summary>
    public float? EchoDecayFactor { get; set; }

    /// <summary>Custom-mode override for <see cref="SLSKDONET.Models.Timeline.TransitionModel.FilterStartFrequency"/>; null = preset default.</summary>
    public float? FilterStartFrequency { get; set; }

    /// <summary>Custom-mode override for <see cref="SLSKDONET.Models.Timeline.TransitionModel.FilterEndFrequency"/>; null = preset default.</summary>
    public float? FilterEndFrequency { get; set; }

    /// <summary>Dead weight — shaped as gain overrides (0-1), which never matched what the live
    /// engine's EQ Custom config actually needs (EqBandSwapConfig: SwapLow/SwapMid/SwapHigh
    /// booleans + crossover Hz, not gains). Never set, never read. Left in place rather than
    /// dropped to avoid a destructive migration for a few always-null columns — see
    /// EqSwapLow/Mid/High + EqLowCrossoverHz/EqHighCrossoverHz below for the real thing.</summary>
    public float? EqLowGain { get; set; }
    public float? EqMidGain { get; set; }
    public float? EqHighGain { get; set; }

    /// <summary>Custom-mode override for <see cref="SLSKDONET.Models.Timeline.TransitionModel.WaveDuckDepth"/>; null = preset default.</summary>
    public float? WaveDuckDepth { get; set; }

    /// <summary>Custom-mode override for <see cref="SLSKDONET.Models.Timeline.TransitionModel.FilterSweepRising"/>; null = preset default.</summary>
    public bool? FilterSweepRising { get; set; }

    /// <summary>Custom-mode overrides for the "Blend" preset's EQ band swap — which bands swap
    /// from outgoing to incoming (bass-handover technique) and where the crossovers sit. Null =
    /// preset default (Low only, 250Hz/4000Hz crossovers — see TransitionModel's own defaults).</summary>
    public bool? EqSwapLow { get; set; }
    public bool? EqSwapMid { get; set; }
    public bool? EqSwapHigh { get; set; }
    public float? EqLowCrossoverHz { get; set; }
    public float? EqHighCrossoverHz { get; set; }

    /// <summary>"Double Drop" loop config — see <see cref="SLSKDONET.Models.Timeline.TransitionModel.LoopBars"/>/
    /// <see cref="SLSKDONET.Models.Timeline.TransitionModel.LoopRepeats"/>. Whether looping is
    /// enabled isn't a separate column — <see cref="TransitionType"/> itself is "DoubleDrop" when
    /// it is, same as every other preset here.</summary>
    public int? LoopBars { get; set; }
    public int? LoopRepeats { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
