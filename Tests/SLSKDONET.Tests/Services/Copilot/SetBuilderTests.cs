using System;
using System.Collections.Generic;
using SLSKDONET.Services.Copilot;
using Xunit;

namespace SLSKDONET.Tests.Services.Copilot;

public class SetBuilderTests
{
    [Fact]
    public void ComputeTransitionCost_RejectsOutOfBandBpm()
    {
        // 130 BPM vs 120 anchor = 8.33% > 8% band → hard reject.
        var cost = SetBuilderScoring.ComputeTransitionCost(
            fromEnergy: 50, candidateEnergy: 60, targetEnergy: 60,
            candidateBpm: 130, anchorBpm: 120, maxBpmVariance: 0.08, confidence: 0.9);

        Assert.True(double.IsPositiveInfinity(cost));
    }

    [Fact]
    public void ComputeTransitionCost_PrefersOnEnergyHighConfidence()
    {
        var good = SetBuilderScoring.ComputeTransitionCost(
            fromEnergy: 50, candidateEnergy: 60, targetEnergy: 60,
            candidateBpm: 120, anchorBpm: 120, maxBpmVariance: 0.08, confidence: 0.9);

        var poor = SetBuilderScoring.ComputeTransitionCost(
            fromEnergy: 50, candidateEnergy: 80, targetEnergy: 60,
            candidateBpm: 120, anchorBpm: 120, maxBpmVariance: 0.08, confidence: 0.3);

        Assert.True(good < poor);
    }

    [Fact]
    public void ComputeTransitionCost_PrefersCloserBpmWithinBand()
    {
        // Same energy/confidence; only BPM distance differs (121 vs 128 against a 120 anchor).
        var near = SetBuilderScoring.ComputeTransitionCost(50, 60, 60, 121, 120, 0.08, 0.9);
        var far = SetBuilderScoring.ComputeTransitionCost(50, 60, 60, 128, 120, 0.08, 0.9);

        Assert.True(near < far);
    }

    [Fact]
    public void HarmonicPenalty_StepsByDistance()
    {
        Assert.Equal(0.0, SetBuilderScoring.HarmonicPenalty(8, 8), 6);    // same key
        Assert.Equal(0.0, SetBuilderScoring.HarmonicPenalty(8, 9), 6);    // adjacent
        Assert.Equal(0.4, SetBuilderScoring.HarmonicPenalty(8, 10), 6);   // distance 2
        Assert.Equal(1.0, SetBuilderScoring.HarmonicPenalty(8, 3), 6);    // distance 5
        Assert.Equal(0.0, SetBuilderScoring.HarmonicPenalty(-1, 8), 6);   // unknown key
        Assert.Equal(0.0, SetBuilderScoring.HarmonicPenalty(-1, -1), 6);  // both unknown
    }

    [Fact]
    public void BuildSpine_PicksOnEnergyPathAndSkipsOutOfBand()
    {
        var candidates = new List<SpineCandidate>
        {
            new("A", Bpm: 120, Energy: 40, DurationSeconds: 60),
            new("B", Bpm: 120, Energy: 60, DurationSeconds: 60),
            new("C", Bpm: 120, Energy: 80, DurationSeconds: 60),
            new("OutOfBand", Bpm: 132, Energy: 70, DurationSeconds: 60), // 10% > 8%
        };
        var confidence = new Dictionary<(string, string), double>
        {
            [("A", "B")] = 0.9,
            [("A", "C")] = 0.9,
            [("B", "C")] = 0.9,
        };
        double[] curve = { 40, 60, 80, 100 };

        var spine = SetBuilder.BuildSpine(candidates, confidence, targetMinutes: 3, maxBpmVariance: 0.08, targetEnergyCurve: curve, seedHash: "A");

        Assert.Equal(new[] { "A", "B", "C" }, spine);
    }

    [Fact]
    public void BuildSpine_StopsWhenNoValidInBandCandidateRemains()
    {
        var candidates = new List<SpineCandidate>
        {
            new("A", Bpm: 120, Energy: 40, DurationSeconds: 60),
            new("OutOfBand", Bpm: 140, Energy: 60, DurationSeconds: 60), // 16.7% > 8%
        };
        var confidence = new Dictionary<(string, string), double>
        {
            [("A", "OutOfBand")] = 0.9,
        };

        var spine = SetBuilder.BuildSpine(candidates, confidence, targetMinutes: 3, maxBpmVariance: 0.08, targetEnergyCurve: null, seedHash: "A");

        Assert.Equal(new[] { "A" }, spine); // only the seed; the only candidate is out of band
    }
}
