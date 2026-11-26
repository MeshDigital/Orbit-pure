using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Passive playback log of pairs that were actually mixed during playback (Epic #517 / #534).
/// Deliberately stored outside <see cref="TransitionEntity"/> (<c>Transitions</c>) so listening to
/// an Auto transition never pollutes lookup Step 2 ("From your earlier mix").
/// </summary>
[Table("TransitionPlays")]
public class TransitionPlayEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string OutgoingTrackHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string IncomingTrackHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Technique { get; set; } = "Auto";

    /// <summary>True when the user skipped the incoming track shortly after the transition finished.</summary>
    public bool WasSkipped { get; set; }

    public DateTime PlayedAtUtc { get; set; } = DateTime.UtcNow;
}
