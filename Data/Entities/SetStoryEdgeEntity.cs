using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 1: a branching edge between two SetSlots in a SetProject's Story Graph. Enables adaptive
/// live performance (A can branch to B or C depending on crowd energy), distinct from the linear
/// <see cref="SetTrackEntity.Position"/> spine.
/// </summary>
[Table("SetStoryEdges")]
public class SetStoryEdgeEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SetProjectId { get; set; }   // FK -> SetLists
    public Guid FromSlotId { get; set; }     // FK -> SetTracks
    public Guid ToSlotId { get; set; }       // FK -> SetTracks

    /// <summary>Soft reference to the knowledge edge backing this branch.</summary>
    public Guid? TransitionKnowledgeId { get; set; }

    /// <summary>Prior likelihood of taking this branch (0-1).</summary>
    public double Probability { get; set; } = 0.5;

    /// <summary>Human-readable branch condition, e.g. "if crowd energy < 60 take the Reset path".</summary>
    public string? ConditionText { get; set; }
}

/// <summary>A reusable, named branching pattern (starting track + possible next tracks).</summary>
[Table("MixRecipes")]
public class MixRecipeEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string StartingTrackId { get; set; } = string.Empty;   // TrackUniqueHash

    /// <summary>JSON array of { toTrackId, weight, condition }.</summary>
    public string PossiblePathsJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
