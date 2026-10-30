using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Confidence tier for a knowledge edge, derived from the volume and type of its evidence
/// (see DOCS/workstation/KNOWLEDGE_LIFECYCLE_ARCHITECTURE.md §2.1). Recomputed, never hand-edited.
/// </summary>
public enum KnowledgeConfidence
{
    Unknown = 0,
    Experimental = 1,
    Likely = 2,
    Confirmed = 3,
}

/// <summary>
/// Phase 2 (Knowledge Lifecycle): the reusable, rated knowledge edge between two tracks.
/// <para>
/// This is the <em>aggregate</em> over <see cref="TransitionEvidenceEntity"/> rows — its scores,
/// confidence and freshness are recomputed by the Transition / Performance Analyst agents rather
/// than edited directly. Distinct from <see cref="PlaylistTrackTransitionEntity"/>, which is the
/// playlist-local application of a transition template to an adjacent pair.
/// </para>
/// </summary>
[Table("TransitionKnowledge")]
public class TransitionKnowledgeEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Outgoing track (<c>TrackUniqueHash</c>).</summary>
    public string SourceTrackId { get; set; } = string.Empty;

    /// <summary>Incoming track (<c>TrackUniqueHash</c>).</summary>
    public string TargetTrackId { get; set; } = string.Empty;

    /// <summary>Transition type (int over the transition-type vocabulary; mapped at the service layer).</summary>
    public int TransitionType { get; set; }

    // ── Aggregated scores (recomputed from evidence, never hand-edited) ──
    public double AggregateScore { get; set; }
    public double HarmonicScore { get; set; }
    public double BpmScore { get; set; }
    public double EnergyScore { get; set; }
    public double VocalScore { get; set; }
    public double CrowdFitScore { get; set; }

    public int EvidenceCount { get; set; }
    public KnowledgeConfidence Confidence { get; set; } = KnowledgeConfidence.Unknown;

    /// <summary>0-1 freshness decay factor (see §6.3); 1.0 when <see cref="IsClassic"/>.</summary>
    public double Freshness { get; set; } = 1.0;

    public DateTime LastVerifiedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>"Classic" tag — exempt from freshness decay.</summary>
    public bool IsClassic { get; set; }

    /// <summary>Convenience flag equivalent to <c>Confidence &gt;= Likely</c>.</summary>
    public bool Verified { get; set; }

    public Guid? DefaultTemplateId { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
