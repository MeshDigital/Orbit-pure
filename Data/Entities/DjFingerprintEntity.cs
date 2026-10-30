using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): profiles <em>how this DJ plays</em> — preferred transition
/// types, energy curves, BPM movement, genres, and avoidance patterns — so recommendations can be
/// filtered through it (the <c>djFit</c> multiplier).
/// <para>
/// <strong>DERIVED / MATERIALIZED</strong> — recomputed by the Performance Analyst Agent from
/// observed behaviour in <see cref="PerformanceSessionEntity"/> rows, not a hand-edited preference.
/// </para>
/// </summary>
[Table("DjFingerprints")]
public class DjFingerprintEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Weighted preference over transition types, e.g. { "LongBlend": 0.9, "DropSwap": 0.2 }.</summary>
    public string? PreferredTransitionTypesJson { get; set; }

    /// <summary>Preferred energy arc, e.g. { "Open": 30, "Build": 55, "Peak": 90, "Reset": 40, "Close": 70 }.</summary>
    public string? PreferredEnergyCurvesJson { get; set; }

    /// <summary>Preferred BPM movement, e.g. { "maxRisePct": 8, "prefersHalfTime": false }.</summary>
    public string? PreferredBpmMovementJson { get; set; }

    /// <summary>Learned genre preferences.</summary>
    public string? PreferredGenresJson { get; set; }

    /// <summary>Avoidance patterns, e.g. ["vocal->vocal", "bpm-drop>8%", "key-jump>2"].</summary>
    public string? AvoidancePatternsJson { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
