using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

public class GenreFamilyStrategyWeightsTests
{
    private static BreakbeatAnalysisStrategy Breakbeat() =>
        new(new DnBTransientDetectionService(NullLogger<DnBTransientDetectionService>.Instance));

    [Fact]
    public void ScoringWeights_SumToOne_ForEveryStrategy()
    {
        foreach (var weights in new[]
        {
            Breakbeat().ScoringWeights,
            new FourOnTheFloorAnalysisStrategy().ScoringWeights,
            new BassMusicAnalysisStrategy().ScoringWeights,
        })
        {
            Assert.Equal(1.0, weights.Total, 6);
        }
    }

    [Fact]
    public void ScoringWeights_MatchTheGenreTables()
    {
        var dnb = Breakbeat().ScoringWeights;
        Assert.Equal(0.30, dnb.SubBassReturn, 6);
        Assert.Equal(0.15, dnb.SpectralFluxNovelty, 6);
        Assert.Equal(0.15, dnb.SustainedEnergy, 6);
        Assert.Equal(0.15, dnb.PhraseConfidence, 6);
        Assert.Equal(0.15, dnb.SectionLength, 6);
        Assert.Equal(0.10, dnb.CueDetrAgreement, 6);

        var house = new FourOnTheFloorAnalysisStrategy().ScoringWeights;
        Assert.Equal(0.35, house.SustainedEnergy, 6);
        Assert.Equal(0.15, house.SubBassReturn, 6);

        var bass = new BassMusicAnalysisStrategy().ScoringWeights;
        Assert.Equal(0.30, bass.SpectralFluxNovelty, 6);
        Assert.Equal(0.05, bass.SustainedEnergy, 6);
    }

    [Fact]
    public void WithoutCueDetr_RedistributesItsShare_AndStillSumsToOne()
    {
        var weights = new BassMusicAnalysisStrategy().ScoringWeights.WithoutCueDetr();

        Assert.Equal(0, weights.CueDetrAgreement);
        Assert.Equal(1.0, weights.Total, 6);
        Assert.True(weights.SpectralFluxNovelty > new BassMusicAnalysisStrategy().ScoringWeights.SpectralFluxNovelty);
    }
}
