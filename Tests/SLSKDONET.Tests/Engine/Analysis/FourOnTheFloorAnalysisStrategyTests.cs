using System.Collections.Generic;
using SLSKDONET.Engine.Analysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

public class FourOnTheFloorAnalysisStrategyTests
{
    private static GenreFamilyResult HouseClassification()
        => new(GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.House, 118, 128);

    [Fact]
    public void RefineBpm_IsANoOp_NeverCorrectsBpmValue()
    {
        var strategy = new FourOnTheFloorAnalysisStrategy();

        var (corrected, penalty, note) = strategy.RefineBpm(HouseClassification(), rawBpm: 61f, confidence: 0.8f);

        // 61 is nowhere near the House bracket, but four-on-the-floor beat tracking is trusted
        // not to need octave correction — this must never flip the BPM value.
        Assert.Equal(61f, corrected);
        Assert.Equal(1f, penalty);
        Assert.Null(note);
    }

    [Theory]
    [InlineData(FourOnTheFloorSubgenre.House, "House")]
    [InlineData(FourOnTheFloorSubgenre.TechHouseTechno, "TechHouse")]
    [InlineData(FourOnTheFloorSubgenre.Trance, "Trance")]
    public void ResolvePreset_ReturnsMatchingSubgenrePreset(FourOnTheFloorSubgenre subgenre, string expectedGenreName)
    {
        var strategy = new FourOnTheFloorAnalysisStrategy();

        var preset = strategy.ResolvePreset(subgenre);

        Assert.Equal(expectedGenreName, preset.Genre);
    }

    [Fact]
    public void GetSignalWeights_IsSpectralFluxDominant()
    {
        var strategy = new FourOnTheFloorAnalysisStrategy();

        var weights = strategy.GetSignalWeights();

        Assert.True(weights.SpectralFluxWeight > weights.SubBassWeight);
        Assert.True(weights.SpectralFluxWeight > weights.EnergyJumpWeight);
    }

    [Fact]
    public void GetFamilySpecificDropCandidates_MapsStructuralStrippingReturnsToScoredCandidates()
    {
        var strategy = new FourOnTheFloorAnalysisStrategy();
        var analysis = new AnalysisPipelineResult
        {
            StructuralStrippingReturnTimestamps = new List<double> { 88.0, 176.0 }
        };

        var candidates = strategy.GetFamilySpecificDropCandidates(analysis, bpm: 128, downbeatAnchor: 0.1);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.Time == 88.0);
        Assert.All(candidates, c => Assert.True(c.Score > 0));
    }

    [Fact]
    public void GetFamilySpecificBreakdownCandidates_ReturnsStructuralStrippingStarts()
    {
        var strategy = new FourOnTheFloorAnalysisStrategy();
        var analysis = new AnalysisPipelineResult
        {
            StructuralStrippingStartTimestamps = new List<double> { 60.0 }
        };

        var candidates = strategy.GetFamilySpecificBreakdownCandidates(analysis, bpm: 128, downbeatAnchor: 0.1);

        Assert.Single(candidates);
        Assert.Equal(60.0, candidates[0]);
    }
}
