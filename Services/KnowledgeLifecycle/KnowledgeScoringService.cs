using System;
using System.Collections.Generic;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): the pure mathematical core that materializes a
/// <see cref="TransitionKnowledgeEntity"/> (the derived view) from its
/// <see cref="TransitionEvidenceEntity"/> rows (the source of truth).
/// <para>
/// Stateless and deterministic — every function is a pure function of its inputs and the wall
/// clock, so the formulas can be unit-tested without a database. Derivation in
/// DOCS/workstation/KNOWLEDGE_LIFECYCLE_ARCHITECTURE.md §2 and §6.
/// </para>
/// </summary>
public static class KnowledgeScoringService
{
    /// <summary>Knowledge freshness half-life, in years (24 months).</summary>
    public const double HalfLifeYears = 2.0;

    /// <summary>Decay constant λ = ln(2) / half-life, per year.</summary>
    public static readonly double LambdaPerYear = Math.Log(2.0) / HalfLifeYears;

    /// <summary>Confidence-point thresholds (sum of confidence × evidence recency).</summary>
    public const double ExperimentalThreshold = 0.3;
    public const double LikelyThreshold = 1.0;
    public const double ConfirmedThreshold = 2.0;

    /// <summary>Years between a past timestamp and now; 0 when the timestamp is in the future (clock skew).</summary>
    public static double YearsSince(DateTime utc, DateTime now)
    {
        var delta = now - utc;
        return delta <= TimeSpan.Zero ? 0.0 : delta.TotalDays / 365.25;
    }

    /// <summary>
    /// Edge freshness, 0-100: <c>100 · e^(−λ · yearsSinceLastUse)</c>. Classic edges are
    /// decay-exempt and always return 100.
    /// </summary>
    public static double ComputeFreshness(DateTime lastUsedUtc, DateTime now, bool isClassic)
    {
        if (isClassic)
            return 100.0;
        return Math.Clamp(100.0 * Math.Exp(-LambdaPerYear * YearsSince(lastUsedUtc, now)), 0.0, 100.0);
    }

    /// <summary>Recency factor (0-1) used to weight a single evidence row in aggregation.</summary>
    public static double EvidenceRecency(DateTime createdAtUtc, DateTime now)
        => Math.Exp(-LambdaPerYear * YearsSince(createdAtUtc, now));

    /// <summary>Recency- and confidence-weighted aggregate of evidence scores, 0-100; 0 with no evidence.</summary>
    public static double ComputeCompatibilityScore(IReadOnlyList<TransitionEvidenceEntity> evidence, DateTime now)
    {
        double numerator = 0.0;
        double denominator = 0.0;
        foreach (var e in evidence)
        {
            var weight = e.Confidence * EvidenceRecency(e.CreatedAt, now);
            numerator += e.Score * weight;
            denominator += weight;
        }
        return denominator <= 0.0 ? 0.0 : numerator / denominator;
    }

    /// <summary>Sum of confidence × recency over evidence — the "evidence points" that drive the tier.</summary>
    public static double ComputeConfidencePoints(IReadOnlyList<TransitionEvidenceEntity> evidence, DateTime now)
    {
        double points = 0.0;
        foreach (var e in evidence)
            points += e.Confidence * EvidenceRecency(e.CreatedAt, now);
        return points;
    }

    public static KnowledgeConfidenceLevel ToConfidenceLevel(double confidencePoints)
        => confidencePoints >= ConfirmedThreshold ? KnowledgeConfidenceLevel.Confirmed
         : confidencePoints >= LikelyThreshold ? KnowledgeConfidenceLevel.Likely
         : confidencePoints >= ExperimentalThreshold ? KnowledgeConfidenceLevel.Experimental
         : KnowledgeConfidenceLevel.Unknown;

    /// <summary>Normalised 0-1 confidence score; reaches 1.0 exactly at the Confirmed threshold.</summary>
    public static double ComputeConfidenceScore(double confidencePoints)
        => Math.Clamp(confidencePoints / ConfirmedThreshold, 0.0, 1.0);

    /// <summary>Recomputes every derived field of a knowledge edge from its evidence rows.</summary>
    public static void Recompute(
        TransitionKnowledgeEntity knowledge,
        IReadOnlyList<TransitionEvidenceEntity> evidence,
        DateTime now)
    {
        var points = ComputeConfidencePoints(evidence, now);
        knowledge.CompatibilityScore = ComputeCompatibilityScore(evidence, now);
        knowledge.ConfidenceScore = ComputeConfidenceScore(points);
        knowledge.ConfidenceLevel = ToConfidenceLevel(points);
        knowledge.Freshness = ComputeFreshness(knowledge.LastUsedAtUtc, now, knowledge.IsClassic);
        knowledge.EvidenceCount = evidence.Count;
        knowledge.Verified = knowledge.ConfidenceLevel >= KnowledgeConfidenceLevel.Likely;
        knowledge.LastCalculatedUtc = now;
        knowledge.UpdatedAtUtc = now;
    }
}
