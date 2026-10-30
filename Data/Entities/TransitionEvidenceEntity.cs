using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): an atomic, immutable claim about a
/// <see cref="TransitionKnowledgeEntity"/> edge.
/// <para>
/// <strong>SOURCE OF TRUTH</strong> — the knowledge edge's derived scores are recomputed from these
/// rows. Evidence is append-only and never edited after the fact.
/// </para>
/// </summary>
[Table("TransitionEvidence")]
public class TransitionEvidenceEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TransitionKnowledgeId { get; set; }   // FK -> TransitionKnowledge

    public TransitionEvidenceSource Source { get; set; }

    /// <summary>0-100, normalised per source.</summary>
    public double Score { get; set; }

    /// <summary>Confidence in this evidence, 0.0-1.0 (a live crowd rating is more certain than a harmonic heuristic).</summary>
    public double Confidence { get; set; } = 0.5;

    /// <summary>Optional typed link back to the performance session that produced this evidence.</summary>
    public Guid? PerformanceSessionId { get; set; }

    /// <summary>Event profile id, venue, session id, and any other provenance.</summary>
    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
