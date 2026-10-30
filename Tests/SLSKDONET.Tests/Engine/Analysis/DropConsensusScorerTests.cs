using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

public class DropConsensusScorerTests
{
    [Fact]
    public void RankAndSelectDrop2_PrefersTheSubstantial122sDrop_OverThe144sReprise()
    {
        // I Just Can't Get Enough: 175 BPM, Drop 1 56.0s, 22s main drop at 122.0s and a 9.6s
        // (7-bar) reprise at 144.0s. The reprise leaves ~20 bars before the end, so the runway
        // gate does NOT reject it; the 16-bar substantial-section rule is what rejects it.
        const double bpm = 175.0;
        const double duration = 171.77;
        const double drop1 = 56.0;
        double bar = 240.0 / bpm;

        var weights = new BreakbeatAnalysisStrategy(
            new DnBTransientDetectionService(NullLogger<DnBTransientDetectionService>.Instance)).ScoringWeights;
        var preset = PhraseAlignmentService.Presets["DnB"];

        var candidates = new List<DropScoreCandidate>
        {
            new()
            {
                TimeSeconds = 122.0,
                SectionDurationBars = 22.0 / bar,
                SubBassReturnStrength = 0.90,
                SpectralFluxNovelty = 0.85,
                SustainedPostEnergy = 0.95,
                PhraseConfidence = 0.81,
                CueDetrAgreement = null,
            },
            new()
            {
                TimeSeconds = 144.0,
                SectionDurationBars = 9.6 / bar,
                SubBassReturnStrength = 0.95,
                SpectralFluxNovelty = 0.90,
                SustainedPostEnergy = 0.80,
                PhraseConfidence = 1.00,
                CueDetrAgreement = null,
            },
        };

        Assert.True((duration - 144.0) / bar >= 16, "fixture must leave >=16 bars so the runway penalty does not decide it");
        Assert.True(candidates[1].SectionDurationBars < preset.MinSubstantialDropBars * 0.5,
            "fixture must trigger the short-section penalty");

        var result = DropConsensusScorer.RankAndSelectDrop2(candidates, drop1, duration, bpm, preset, weights);

        Assert.NotNull(result.Drop2);
        Assert.Equal(122.0, result.Drop2!.TimeSeconds, 6);
        Assert.True(result.Ranked[0].FinalScore > result.Ranked[1].FinalScore);
        Assert.Equal(122.0, result.Ranked[0].TimeSeconds, 6);
    }

    [Fact]
    public void RankAndSelectDrop2_UsesRedistributedWeights_WhenCueDetrIsAbsent()
    {
        var weights = new FourOnTheFloorAnalysisStrategy().ScoringWeights;
        var without = weights.WithoutCueDetr();
        var preset = PhraseAlignmentService.Presets["House"];

        var candidate = new DropScoreCandidate
        {
            TimeSeconds = 80.0,
            SectionDurationBars = 16,
            SubBassReturnStrength = 0.5,
            SpectralFluxNovelty = 0.5,
            SustainedPostEnergy = 0.5,
            PhraseConfidence = 0.5,
        };

        var result = DropConsensusScorer.RankAndSelectDrop2(
            new[] { candidate }, 30, 200, 125, preset, without);

        Assert.NotNull(result.Drop2);
        Assert.InRange(result.Drop2!.FinalScore, 0.0, 1.0);
    }
}
