using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Where a piece of transition evidence came from (source weights in
/// DOCS/workstation/KNOWLEDGE_LIFECYCLE_ARCHITECTURE.md §6.1).
/// </summary>
public enum TransitionEvidenceSource
{
    HarmonicMatch = 0,      // algorithmic: key/camelot distance
    PhraseMatch = 1,        // algorithmic: phrase/energy alignment
    CrowdSuccess = 2,       // real-world: rated crowd reaction
    ManualRating = 3,       // explicit DJ rating in Transition Lab
    PerformanceHistory = 4, // real-world: actually played live
}

/// <summary>
/// Phase 2 (Knowledge Lifecycle): an atomic, immutable claim about a
/// <see cref="TransitionKnowledgeEntity"/> edge. Aggregation over these rows is what produces the
/// edge's scores, confidence and freshness — evidence is append-only, aggregates are recomputed.
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

    /// <summary>Source credibility weight (see §6.1).</summary>
    public double Weight { get; set; }

    /// <summary>Event profile id, venue, session id, etc.</summary>
    public string? ContextJson { get; set; }

    public Guid? PerformanceSessionId { get; set; }

    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}
