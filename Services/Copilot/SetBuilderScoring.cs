using System;

namespace SLSKDONET.Services.Copilot;

/// <summary>A candidate track for the Set Builder spine.</summary>
public sealed record SpineCandidate(
    string TrackUniqueHash,
    double Bpm,
    double Energy,
    double DurationSeconds);

/// <summary>
/// Slice K7b: the Set Builder cost function. Pure and unit-testable — a greedy-lookahead penalty
/// that (1) hard-rejects BPM variance beyond <paramref name="maxBpmVariance"/>, (2) penalises
/// deviation from the event's target energy curve, (3) penalises energy-flow jumps, and (4) rewards
/// high-confidence (verified, real-world) edges.
/// </summary>
public static class SetBuilderScoring
{
    // Weights: energy-curve fidelity dominates, then flow continuity and confidence share the rest.
    public const double EnergyCurveWeight = 0.5;
    public const double FlowWeight = 0.25;
    public const double ConfidenceWeight = 0.25;

    /// <summary>
    /// Transition cost from a current slot to a candidate. Returns <see cref="double.PositiveInfinity"/>
    /// when the candidate's BPM is outside the allowed variance band (hard constraint). Lower is better.
    /// </summary>
    public static double ComputeTransitionCost(
        double fromEnergy,
        double candidateEnergy,
        double targetEnergy,
        double candidateBpm,
        double anchorBpm,
        double maxBpmVariance,
        double confidence)
    {
        if (anchorBpm <= 0 || candidateBpm <= 0)
            return double.PositiveInfinity;

        var variance = Math.Abs(candidateBpm - anchorBpm) / anchorBpm;
        if (variance > maxBpmVariance)
            return double.PositiveInfinity; // hard BPM constraint

        var energyError = Math.Abs(candidateEnergy - targetEnergy) / 100.0;
        var flowError = Math.Abs(candidateEnergy - fromEnergy) / 100.0;
        var confidenceCost = 1.0 - Math.Clamp(confidence, 0.0, 1.0);

        return EnergyCurveWeight * energyError
             + FlowWeight * flowError
             + ConfidenceWeight * confidenceCost;
    }
}
