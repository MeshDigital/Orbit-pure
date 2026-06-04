using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Similarity;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

public class MatchingScoringConstantsTests
{
    [Fact]
    public void MatchingConstants_StayNormalizedAcrossPipelines()
    {
        var segmentRoleTotal =
            ScoringConstants.Matching.SegmentIntroWeight +
            ScoringConstants.Matching.SegmentBuildWeight +
            ScoringConstants.Matching.SegmentDropWeight +
            ScoringConstants.Matching.SegmentBreakdownWeight +
            ScoringConstants.Matching.SegmentOutroWeight;

        var blendTotal =
            ScoringConstants.Matching.WholeTrackBlendWeight +
            ScoringConstants.Matching.SegmentBlendWeight;

        var trackMatchTotal =
            ScoringConstants.Matching.OverallSoundWeight +
            ScoringConstants.Matching.OverallHarmonyWeight +
            ScoringConstants.Matching.OverallBeatWeight +
            ScoringConstants.Matching.OverallDropSonicWeight +
            ScoringConstants.Matching.OverallOutroIntroWeight;

        var transitionTotal =
            ScoringConstants.Matching.TransitionStructuralWeight +
            ScoringConstants.Matching.TransitionEnergyWeight;

        Assert.Equal(1.0, segmentRoleTotal, 6);
        Assert.Equal(1.0, blendTotal, 6);
        Assert.Equal(1.0f, trackMatchTotal, 6);
        Assert.Equal(1.0f, transitionTotal, 6);
    }

    [Fact]
    public void ComputeTransitionScore_UsesConfiguredBlendWeights()
    {
        var outro = new SectionFeatureVector
        {
            SectionType = PhraseType.Outro,
            EnergyLevel = 0.82f,
            Arousal = 0.70f,
            Danceability = 0.66f,
            SpectralBrightness = 0.58f,
        };

        var intro = new SectionFeatureVector
        {
            SectionType = PhraseType.Intro,
            EnergyLevel = 0.77f,
            Arousal = 0.68f,
            Danceability = 0.64f,
            SpectralBrightness = 0.55f,
        };

        var structuralFlow = outro.TransitionScore(intro);
        var energyFlow = TrackMatchScorer.ComputeEnergyCompatibility(outro.EnergyLevel, intro.EnergyLevel);
        var expected =
            (structuralFlow * ScoringConstants.Matching.TransitionStructuralWeight) +
            (energyFlow * ScoringConstants.Matching.TransitionEnergyWeight);

        var actual = TrackMatchScorer.ComputeTransitionScore(outro, intro, 0.5f);

        Assert.Equal(expected, actual, 6);
    }
}
