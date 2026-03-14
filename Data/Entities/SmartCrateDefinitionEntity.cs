using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 23: Smart Crates
/// Defines a dynamic playlist based on rules (Vibe, BPM, Energy, etc.).
/// </summary>
[Table("smart_crate_definitions")]
public class SmartCrateDefinitionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// JSON serialized rules for the crate.
    /// Example: { "Mood": "Aggressive", "MinBpm": 170, "MinEnergy": 0.8 }
    /// </summary>
    public string RulesJson { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
