using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Playlist;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): pure, stateless pre-filter for the Transition Agent. Bounds the
/// candidate search space to ±<see cref="DefaultBpmBand"/> BPM and adjacent Camelot keys, so a
/// full-library O(n²) scan never happens. See
/// DOCS/workstation/KNOWLEDGE_LIFECYCLE_ARCHITECTURE.md §5.
/// </summary>
public static class TransitionCandidateFilter
{
    public const double DefaultBpmBand = 0.08;             // ±8%
    public const double DefaultMaxCamelotDistance = 1.0;   // same key, ±1 number, or same-number A/B
    public const int DefaultMaxCandidates = 16;            // hard cap per analyzed track

    /// <summary>Relative BPM band check: |seed − candidate| ≤ seed × band. Zero/unknown BPM never passes.</summary>
    public static bool IsWithinBpmBand(double seedBpm, double candidateBpm, double band = DefaultBpmBand)
    {
        if (seedBpm <= 0 || candidateBpm <= 0)
            return false;
        return Math.Abs(seedBpm - candidateBpm) <= seedBpm * band;
    }

    /// <summary>Adjacent on the Camelot wheel (distance ≤ <paramref name="maxDistance"/>).</summary>
    public static bool IsAdjacentCamelot(string? seedKey, string? candidateKey, double maxDistance = DefaultMaxCamelotDistance)
        => PlaylistOptimizer.CamelotDistancePublic(seedKey, candidateKey) <= maxDistance;

    /// <summary>Queue/DB dedupe key: {SourceTrackId}_{TargetTrackId}_{AnalysisVersion}.</summary>
    public static string IdempotencyKey(string sourceTrackId, string targetTrackId, string analysisVersion)
        => $"{sourceTrackId}_{targetTrackId}_{analysisVersion}";

    /// <summary>
    /// Selects the bounded, relevance-ranked candidate set for a seed track: excludes self, applies
    /// the BPM band and Camelot adjacency gates, orders by BPM closeness, and caps at
    /// <paramref name="maxCandidates"/>.
    /// </summary>
    public static IReadOnlyList<AudioFeaturesEntity> SelectCandidates(
        AudioFeaturesEntity seed,
        IEnumerable<AudioFeaturesEntity> library,
        int maxCandidates = DefaultMaxCandidates,
        double bpmBand = DefaultBpmBand,
        double maxCamelotDistance = DefaultMaxCamelotDistance)
    {
        return library
            .Where(c => !string.Equals(c.TrackUniqueHash, seed.TrackUniqueHash, StringComparison.Ordinal))
            .Where(c => IsWithinBpmBand(seed.Bpm, c.Bpm, bpmBand))
            .Where(c => IsAdjacentCamelot(seed.CamelotKey, c.CamelotKey, maxCamelotDistance))
            .OrderBy(c => Math.Abs(c.Bpm - seed.Bpm))
            .Take(maxCandidates)
            .ToList();
    }
}
