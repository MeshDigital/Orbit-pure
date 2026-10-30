using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): profiles <em>how this DJ plays</em> — preferred transition
/// types, energy arc, and avoidance patterns — so recommendations can be filtered through it
/// (the <c>djFit</c> multiplier). Updated by the Performance Analyst Agent from observed behaviour.
/// </summary>
[Table("DjFingerprints")]
public class DjFingerprintEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Weighted preference over transition types, e.g. { "LongBlend": 0.9, "DropSwap": 0.2 }.</summary>
    public string? PreferredTransitionTypesJson { get; set; }

    /// <summary>Preferred energy arc, e.g. { "Open": 30, "Build": 55, "Peak": 90, "Reset": 40, "Close": 70 }.</summary>
    public string? EnergyCurveProfileJson { get; set; }

    /// <summary>Avoidance patterns, e.g. ["vocal->vocal", "bpm-drop>8%", "key-jump>2"].</summary>
    public string? AvoidancePatternsJson { get; set; }

    /// <summary>Learned genre preferences.</summary>
    public string? GenreAffinityJson { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
