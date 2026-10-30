using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): the reusable, rated knowledge edge between two tracks.
/// <para>
/// <strong>DERIVED / MATERIALIZED</strong> — not a source of truth. Its scores, confidence and
/// freshness are recomputed from <see cref="TransitionEvidenceEntity"/> rows (the source of truth)
/// by the Transition / Performance Analyst agents. Distinct from
/// <see cref="PlaylistTrackTransitionEntity"/>, which is the playlist-local application of a
/// transition template to an adjacent pair.
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

    /// <summary>Optional cue anchor on the source track (part of the uniqueness key).</summary>
    public Guid? SourceCueId { get; set; }

    /// <summary>Optional cue anchor on the target track (part of the uniqueness key).</summary>
    public Guid? TargetCueId { get; set; }

    public KnowledgeTransitionType TransitionType { get; set; }

    // ── Derived scores (materialized from TransitionEvidence; never hand-edited) ──
    public double CompatibilityScore { get; set; }   // 0-100 aggregate
    public double HarmonicScore { get; set; }
    public double BpmScore { get; set; }
    public double EnergyScore { get; set; }
    public double VocalScore { get; set; }
    public double CrowdFitScore { get; set; }

    /// <summary>Derived confidence 0.0-1.0 (see §2.1).</summary>
    public double ConfidenceScore { get; set; }

    public KnowledgeConfidenceLevel ConfidenceLevel { get; set; } = KnowledgeConfidenceLevel.Unknown;

    /// <summary>0-100 freshness decay (see §2.2: <c>100 * e^(-λ * yearsSinceLastUse)</c>).</summary>
    public double Freshness { get; set; } = 100.0;

    public int EvidenceCount { get; set; }

    /// <summary>"Classic" tag — exempt from freshness decay.</summary>
    public bool IsClassic { get; set; }

    /// <summary>Convenience flag equivalent to <c>ConfidenceLevel &gt;= Likely</c>.</summary>
    public bool Verified { get; set; }

    /// <summary>Last time this edge was used in a real performance (drives freshness decay).</summary>
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the derived fields were recomputed from evidence.</summary>
    public DateTime LastCalculatedUtc { get; set; } = DateTime.UtcNow;

    public Guid? DefaultTemplateId { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
