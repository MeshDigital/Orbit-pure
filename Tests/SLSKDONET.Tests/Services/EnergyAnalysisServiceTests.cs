using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Similarity;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class EnergyAnalysisServiceTests
{
    private readonly EnergyAnalysisService _service =
        new(NullLogger<EnergyAnalysisService>.Instance);

    [Fact]
    public void BuildEnergyProfile_WithPhraseWindows_ProducesSegmentScores()
    {
        var windows = new List<float>();
        windows.AddRange(new[] { 0.15f, 0.18f, 0.20f, 0.22f, 0.19f, 0.21f, 0.20f, 0.23f });
        windows.AddRange(new[] { 0.72f, 0.78f, 0.84f, 0.88f, 0.92f, 0.90f, 0.86f, 0.82f });

        var phrases = new List<TrackPhraseEntity>
        {
            new() { Label = "Intro", Type = PhraseType.Intro, StartTimeSeconds = 0f, EndTimeSeconds = 8f, OrderIndex = 0 },
            new() { Label = "Drop 1", Type = PhraseType.Drop, StartTimeSeconds = 8f, EndTimeSeconds = 16f, OrderIndex = 1 }
        };

        var profile = _service.BuildEnergyProfile(windows, 1.0, phrases);

        Assert.Equal(2, profile.Segments.Count);
        Assert.True(profile.Segments[1].AverageEnergy > profile.Segments[0].AverageEnergy);
        Assert.True(profile.Segments[1].EnergyScore > profile.Segments[0].EnergyScore);
        Assert.InRange(profile.OverallEnergy, 0.0f, 1.0f);
        Assert.InRange(profile.OverallEnergyScore, 1, 10);
    }

    [Fact]
    public void BuildEnergyProfile_EmptyCurve_ReturnsNeutralProfile()
    {
        var profile = _service.BuildEnergyProfile(new List<float>(), 1.0, null);

        Assert.Equal(0.5f, profile.OverallEnergy);
        Assert.Equal(5, profile.OverallEnergyScore);
        Assert.Empty(profile.Segments);
    }

    [Fact]
    public async Task ComputeTrackEnergyAsync_MissingFile_ReturnsNeutralProfile()
    {
        var profile = await _service.ComputeTrackEnergyAsync(@"Z:\missing\orbit-test-track.wav");

        Assert.Equal(EnergyProfile.Neutral, profile);
    }

    [Fact]
    public void TrackPhraseEntity_PhraseEnergy_AliasesEnergyLevel()
    {
        var phrase = new TrackPhraseEntity { EnergyLevel = 0.72f };

        Assert.Equal(0.72f, phrase.PhraseEnergy, 3);

        phrase.PhraseEnergy = 0.61f;

        Assert.Equal(0.61f, phrase.EnergyLevel, 3);
    }

    [Fact]
    public void ComputeTransitionScore_PrefersCloserEnergyMatch()
    {
        var outro = new SectionFeatureVector
        {
            SectionType = PhraseType.Outro,
            EnergyLevel = 0.80f,
            Arousal = 0.75f,
            Danceability = 0.70f,
            SpectralBrightness = 0.55f
        };

        var compatibleIntro = new SectionFeatureVector
        {
            SectionType = PhraseType.Intro,
            EnergyLevel = 0.76f,
            Arousal = 0.74f,
            Danceability = 0.68f,
            SpectralBrightness = 0.57f
        };

        var mismatchedIntro = new SectionFeatureVector
        {
            SectionType = PhraseType.Intro,
            EnergyLevel = 0.15f,
            Arousal = 0.74f,
            Danceability = 0.68f,
            SpectralBrightness = 0.57f
        };

        var compatible = TrackMatchScorer.ComputeTransitionScore(outro, compatibleIntro, 0.50f);
        var mismatched = TrackMatchScorer.ComputeTransitionScore(outro, mismatchedIntro, 0.50f);

        Assert.True(compatible > mismatched);
        Assert.InRange(compatible, 0.75f, 1.0f);
        Assert.InRange(mismatched, 0.0f, 0.70f);
    }
}
