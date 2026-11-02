using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// A "Plan B" track for a set slot (rebuild plan D3/P2.6): if the planned track doesn't fit the
/// room, play this instead. Alternates live in their own table so they never appear in the set's
/// spine order, which the Copilot, Set Health and export read from <see cref="SetTrackEntity"/>.
/// </summary>
[Table("SetSlotAlternates")]
public class SetSlotAlternateEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The slot this is an alternate for (FK → SetTracks, cascade).</summary>
    public Guid SlotId { get; set; }

    public string TrackUniqueHash { get; set; } = string.Empty;

    /// <summary>Order among the slot's alternates (0 = first choice).</summary>
    public int Position { get; set; }

    /// <summary>Optional reminder of when to take it, e.g. "if the floor is still cold".</summary>
    public string? ConditionText { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
