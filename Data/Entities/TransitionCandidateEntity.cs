using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): an automatically-discovered potential transition (a hypothesis).
/// <para>
/// Candidates are produced by background scans and carry only theoretical scores. They must be
/// promoted to a <see cref="TransitionKnowledgeEntity"/> (on a score threshold or on a first
/// manual / live signal) before they are surfaced as knowledge.
/// </para>
/// </summary>
[Table("TransitionCandidates")]
public class TransitionCandidateEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string SourceTrackId { get; set; } = string.Empty;   // TrackUniqueHash
    public string TargetTrackId { get; set; } = string.Empty;   // TrackUniqueHash

    /// <summary>Dedupe key: {SourceTrackId}_{TargetTrackId}_{AnalysisVersion}; unique per scan version.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    // Theoretical signals produced by the scanner (all 0-100).
    public double CompatibilityScore { get; set; }
    public double HarmonicScore { get; set; }
    public double BpmScore { get; set; }
    public double EnergyScore { get; set; }
    public double PhraseScore { get; set; }

    /// <summary>"HarmonicScan" | "PhraseScan" | "EmbeddingScan".</summary>
    public string DiscoverySource { get; set; } = string.Empty;

    public DateTime DiscoveredAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsPromoted { get; set; }
    public Guid? PromotedKnowledgeId { get; set; }
}
