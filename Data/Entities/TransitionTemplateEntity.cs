using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 1: a reusable, named recipe describing HOW a transition is executed — cue anchors, mix-in
/// timing, loop and effect settings — independent of any specific track pair. Exportable to
/// Rekordbox/DJ software. Distinct from <see cref="TransitionKnowledgeEntity"/> (the *edge*) and
/// <see cref="PlaylistTrackTransitionEntity"/> (the *local application*).
/// </summary>
[Table("TransitionTemplates")]
public class TransitionTemplateEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Cue role on the outgoing track (int over CueRole); null = auto.</summary>
    public int? SourceCueRole { get; set; }

    /// <summary>Cue role on the incoming track (int over CueRole); null = auto.</summary>
    public int? TargetCueRole { get; set; }

    /// <summary>Bars before the mix-out point (e.g. -16, -8, -4).</summary>
    public int BarsBeforeMix { get; set; } = 16;

    /// <summary>Loop settings JSON ({ bars, repeats }).</summary>
    public string? LoopSettingsJson { get; set; }

    /// <summary>Effect settings JSON (EQ swap, filter sweep, echo, etc.).</summary>
    public string? EffectsJson { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
