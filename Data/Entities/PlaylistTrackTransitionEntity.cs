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

    /// <summary>Custom-mode EQ band gain overrides (0-1); null = preset default (no override).</summary>
    public float? EqLowGain { get; set; }
    public float? EqMidGain { get; set; }
    public float? EqHighGain { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
