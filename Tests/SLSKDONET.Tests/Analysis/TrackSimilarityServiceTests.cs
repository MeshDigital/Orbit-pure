using System;
using System.Collections.Generic;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Similarity;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

public class TrackSimilarityServiceTests
{
    private readonly TrackSimilarityService _sut = new(new HarmonicCompatibilityService(new HarmonicAnalysisService()));

    [Fact]
    public void Score_IsDeterministic_ForFixedInputs()
    {
        var left = CreateFingerprint("left", "8A");
        var right = CreateFingerprint("right", "8A");
        var leftSections = CreateSections(0.72f, 0.84f);
        var rightSections = CreateSections(0.72f, 0.84f);

        var a = _sut.Score(left, right, leftSections, rightSections, TrackSimilarityProfile.BlendSafe);
        var b = _sut.Score(left, right, leftSections, rightSections, TrackSimilarityProfile.BlendSafe);

        Assert.Equal(a.FinalSimilarity, b.FinalSimilarity, 6);
        Assert.Equal(a.WholeTrackSimilarity, b.WholeTrackSimilarity, 6);
        Assert.Equal(a.SegmentSimilarity, b.SegmentSimilarity, 6);
    }

    [Fact]
    public void Score_IdenticalFingerprintsAndSections_IsHigh()
    {
        var left = CreateFingerprint("left", "8A");
        var right = CreateFingerprint("right", "8A");
        var sections = CreateSections(0.72f, 0.84f);

        var result = _sut.Score(left, right, sections, sections, TrackSimilarityProfile.BlendSafe);

        Assert.True(result.FinalSimilarity >= 0.95);
        Assert.Contains("Strong harmonic alignment", result.ReasonTags);
    }

    [Fact]
    public void Score_EnergyDriveProfile_PrefersEnergyRhythmOverBlendSafe()
    {
        var left = CreateFingerprint("left", "8A");
        var right = CreateFingerprint("right", "2B");
        right.Energy = new EnergyVector
        {
            GlobalEnergy = left.Energy.GlobalEnergy,
            IntroEnergy = left.Energy.IntroEnergy,
            BuildEnergy = left.Energy.BuildEnergy,
            DropEnergy = left.Energy.DropEnergy,
            BreakdownEnergy = left.Energy.BreakdownEnergy,
            OutroEnergy = left.Energy.OutroEnergy,
            DropIntensity = left.Energy.DropIntensity,
            BreakdownDepth = left.Energy.BreakdownDepth,
            Confidence = 0.95f,
        };
        right.Rhythm = left.Rhythm;
        right.Timbre = new TimbreVector { MfccTextureProxy = 0.2f, SpectralCentroidProfile = 0.2f, BrightnessWarmthBalance = 0.2f, SpectralComplexity = 0.2f, Confidence = 0.9f };
        right.Mood = new MoodVector { Danceability = 0.2f, Aggressiveness = 0.2f, Acousticness = 0.2f, TonalVsPercussiveBalance = 0.2f, Confidence = 0.9f };

        var blendSafe = _sut.Score(left, right, profile: TrackSimilarityProfile.BlendSafe);
        var energyDrive = _sut.Score(left, right, profile: TrackSimilarityProfile.EnergyDrive);

        Assert.True(energyDrive.FinalSimilarity > blendSafe.FinalSimilarity);
    }

    [Fact]
    public void Score_GoodSegmentFit_LiftsFinalSimilarityAboveWholeTrack()
    {
        var left = CreateFingerprint("left", "8A");
        var right = CreateFingerprint("right", "9A");
        right.Energy.GlobalEnergy = 0.45f;
        right.Timbre.MfccTextureProxy = 0.42f;

        var weakSections = CreateSections(0.15f, 0.25f);
        var strongSections = CreateSections(0.72f, 0.84f);

        var result = _sut.Score(left, right, strongSections, strongSections, TrackSimilarityProfile.BlendSafe);
        var weaker = _sut.Score(left, right, weakSections, strongSections, TrackSimilarityProfile.BlendSafe);

        Assert.True(result.FinalSimilarity > result.WholeTrackSimilarity);
        Assert.True(result.FinalSimilarity > weaker.FinalSimilarity);
        Assert.Contains("Section fit lifts the overall match", result.ReasonTags);
    }

    private static TrackFingerprint CreateFingerprint(string hash, string key)
    {
        return new TrackFingerprint
        {
            TrackUniqueHash = hash,
            BuilderVersion = "A10.3-test",
            Harmonic = new HarmonicVector
            {
                PrimaryKey = key,
                PrimaryConfidence = 0.9f,
                SecondaryKeys = ["9A", "8B"],
                SecondaryConfidences = [0.6f, 0.55f],
                PrimaryKeyPositionNormalized = 0.61f,
                ModulationScore = 0.12f,
                StabilityScore = 0.85f,
            },
            Energy = new EnergyVector
            {
                GlobalEnergy = 0.74f,
                IntroEnergy = 0.42f,
                BuildEnergy = 0.66f,
                DropEnergy = 0.88f,
                BreakdownEnergy = 0.37f,
                OutroEnergy = 0.45f,
                DropIntensity = 0.83f,
                BreakdownDepth = 0.63f,
                Confidence = 0.92f,
            },
            Rhythm = new RhythmVector
            {
                TempoNormalized = 0.63f,
                SwingGrooveScore = 0.58f,
                BeatHistogramSignature = 0.71f,
                PercussiveDensity = 0.76f,
                Confidence = 0.93f,
            },
            Timbre = new TimbreVector
            {
                MfccTextureProxy = 0.69f,
                SpectralCentroidProfile = 0.52f,
                BrightnessWarmthBalance = 0.61f,
                SpectralComplexity = 0.57f,
                Confidence = 0.9f,
            },
            Structure = new StructureVector
            {
                PhraseMapDensity = 0.68f,
                IntroLengthRatio = 0.12f,
                BuildLengthRatio = 0.18f,
                DropLengthRatio = 0.22f,
                BreakdownLengthRatio = 0.14f,
                OutroLengthRatio = 0.11f,
                BuildUpSlope = 0.74f,
                Confidence = 0.88f,
            },
            Mood = new MoodVector
            {
                Danceability = 0.77f,
                Aggressiveness = 0.54f,
                Acousticness = 0.09f,
                TonalVsPercussiveBalance = 0.63f,
                Confidence = 0.91f,
            },
        };
    }

    private static IReadOnlyList<SectionFeatureVector> CreateSections(float introEnergy, float dropEnergy)
    {
        return new List<SectionFeatureVector>
        {
            new() { SectionType = PhraseType.Intro, EnergyLevel = introEnergy, StartRatio = 0f, DurationRatio = 0.12f, Arousal = 0.42f, Danceability = 0.66f, SpectralBrightness = 0.38f, Confidence = 0.95f },
            new() { SectionType = PhraseType.Build, EnergyLevel = 0.66f, StartRatio = 0.2f, DurationRatio = 0.15f, Arousal = 0.61f, Danceability = 0.71f, SpectralBrightness = 0.52f, Confidence = 0.91f },
            new() { SectionType = PhraseType.Drop, EnergyLevel = dropEnergy, StartRatio = 0.38f, DurationRatio = 0.18f, Arousal = 0.86f, Danceability = 0.84f, SpectralBrightness = 0.69f, Confidence = 0.93f },
            new() { SectionType = PhraseType.Breakdown, EnergyLevel = 0.31f, StartRatio = 0.58f, DurationRatio = 0.14f, Arousal = 0.34f, Danceability = 0.56f, SpectralBrightness = 0.28f, Confidence = 0.88f },
            new() { SectionType = PhraseType.Outro, EnergyLevel = 0.45f, StartRatio = 0.82f, DurationRatio = 0.12f, Arousal = 0.41f, Danceability = 0.61f, SpectralBrightness = 0.36f, Confidence = 0.9f },
        };
    }
}