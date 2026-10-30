using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services.Copilot;

/// <summary>
/// Slice K7b: greedy-lookahead Set Builder. Constructs a primary spine of tracks to a target
/// duration by repeatedly selecting the in-band, lowest-cost next track. Pure and unit-testable —
/// edge confidence is supplied via a lookup function so the caller can back it with
/// <c>TransitionKnowledge.ConfidenceScore</c>.
/// </summary>
public static class SetBuilder
{
    /// <summary>
    /// Builds a spine. <paramref name="edgeConfidence"/> maps (source, target) → confidence 0–1
    /// (missing edges default to 0.5, i.e. a neutral theoretical guess).
    /// </summary>
    public static IReadOnlyList<string> BuildSpine(
        IReadOnlyList<SpineCandidate> candidates,
        IReadOnlyDictionary<(string Source, string Target), double> edgeConfidence,
        double targetMinutes,
        double maxBpmVariance,
        IReadOnlyList<double>? targetEnergyCurve = null,
        string? seedHash = null)
    {
        if (candidates.Count == 0)
            return Array.Empty<string>();

        var targetSeconds = targetMinutes * 60.0;
        var spine = new List<string>();
        var used = new HashSet<string>();

        var current = seedHash is not null && candidates.Any(c => c.TrackUniqueHash == seedHash)
            ? candidates.First(c => c.TrackUniqueHash == seedHash)
            : candidates[0];
        var anchorBpm = current.Bpm;

        spine.Add(current.TrackUniqueHash);
        used.Add(current.TrackUniqueHash);

        var elapsed = current.DurationSeconds;
        var fromEnergy = current.Energy;

        while (elapsed < targetSeconds && used.Count < candidates.Count)
        {
            var targetEnergy = TargetEnergyAt(elapsed, targetSeconds, targetEnergyCurve, fromEnergy);

            var best = candidates
                .Where(c => !used.Contains(c.TrackUniqueHash))
                .Select(c =>
                {
                    var confidence = edgeConfidence.TryGetValue((current.TrackUniqueHash, c.TrackUniqueHash), out var conf)
                        ? conf
                        : 0.5;
                    return (Candidate: c, Cost: SetBuilderScoring.ComputeTransitionCost(
                        fromEnergy, c.Energy, targetEnergy, c.Bpm, anchorBpm, maxBpmVariance, confidence));
                })
                .Where(x => !double.IsInfinity(x.Cost))
                .OrderBy(x => x.Cost)
                .FirstOrDefault();

            if (best.Candidate is null)
                break; // no valid in-band candidate left

            spine.Add(best.Candidate.TrackUniqueHash);
            used.Add(best.Candidate.TrackUniqueHash);
            elapsed += best.Candidate.DurationSeconds;
            fromEnergy = best.Candidate.Energy;
            current = best.Candidate;
        }

        return spine;
    }

    private static double TargetEnergyAt(double elapsedSeconds, double totalSeconds, IReadOnlyList<double>? curve, double fallback)
    {
        if (curve is null || curve.Count == 0)
            return fallback;

        var idx = (int)Math.Clamp(Math.Floor(elapsedSeconds / totalSeconds * curve.Count), 0, curve.Count - 1);
        return Math.Clamp(curve[idx], 0.0, 100.0);
    }
}
