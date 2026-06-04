using System.Collections.Generic;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Similarity;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

public class TransitionStyleClassifierTests
{
    private readonly TransitionStyleClassifier _sut = new();

    [Fact]
    public void Classify_ReturnsSmoothBlend_ForHighFitStableEnergy()
    {
        var result = _sut.Classify(
            CreateFingerprint("left", 0.54f),
            CreateFingerprint("right", 0.60f),
            CreateSimilarity(0.84, 0.82, 0.79),
            CreateSections(0.56f, 0.76f, 0.30f),
            CreateSections(0.58f, 0.78f, 0.32f));

        Assert.Equal(TransitionStyle.SmoothBlend, result.Style);
        Assert.Equal("Smooth Blend", result.Label);
        Assert.Equal("High similarity, strong harmonic fit, and a stable energy profile support a seamless handoff.", result.Reason);
    }

    [Fact]
    public void Classify_ReturnsEnergyLift_ForIntentionalEnergyRise()
    {
        var result = _sut.Classify(
            CreateFingerprint("left", 0.40f),
            CreateFingerprint("right", 0.62f),
            CreateSimilarity(0.63, 0.62, 0.67),
            CreateSections(0.34f, 0.54f, 0.28f),
            CreateSections(0.50f, 0.76f, 0.34f));

        Assert.Equal(TransitionStyle.EnergyLift, result.Style);
        Assert.Equal("Energy Lift", result.Label);
        Assert.Equal("The incoming track lifts energy while staying harmonically steady enough to feel intentional.", result.Reason);
    }

    [Fact]
    public void Classify_ReturnsDropSwap_ForImpactHandOff()
    {
        var result = _sut.Classify(
            CreateFingerprint("left", 0.56f),
            CreateFingerprint("right", 0.76f),
            CreateSimilarity(0.68, 0.64, 0.69, drop: 0.72),
            CreateSections(0.52f, 0.84f, 0.30f),
            CreateSections(0.42f, 0.90f, 0.34f));

        Assert.Equal(TransitionStyle.DropSwap, result.Style);
        Assert.Equal("Drop Swap", result.Label);
        Assert.Equal("The outgoing track hands off with impact and the incoming track answers with a strong drop.", result.Reason);
    }

    [Fact]
    public void Classify_ReturnsBreakdownReset_ForLowEnergyReEntry()
    {
        var result = _sut.Classify(
            CreateFingerprint("left", 0.58f),
            CreateFingerprint("right", 0.34f),
            CreateSimilarity(0.60, 0.58, 0.54),
            CreateSections(0.48f, 0.72f, 0.20f),
            CreateSections(0.30f, 0.58f, 0.28f));

        Assert.Equal(TransitionStyle.BreakdownReset, result.Style);
        Assert.Equal("Breakdown Reset", result.Label);
        Assert.Equal("The mix eases into a lower-energy entry after a breakdown, creating a deliberate reset.", result.Reason);
    }

    [Fact]
    public void Classify_ReturnsTensionBridge_ForControlledSectionContrast()
    {
        var result = _sut.Classify(
            CreateFingerprint("left", 0.52f),
            CreateFingerprint("right", 0.58f),
            CreateSimilarity(0.58, 0.54, 0.58),
            CreateSections(0.70f, 0.84f, 0.32f),
            CreateSections(0.10f, 0.32f, 0.30f));

        Assert.Equal(TransitionStyle.TensionBridge, result.Style);
        Assert.Equal("Tension Bridge", result.Label);
        Assert.Equal("Moderate fit with a sectional mismatch creates controlled tension rather than a clean melt.", result.Reason);
    }

    [Fact]
    public void Classify_ReturnsRiskyClash_ForLowFitExtremeJump()
    {
        var result = _sut.Classify(
            CreateFingerprint("left", 0.24f),
            CreateFingerprint("right", 0.62f),
            CreateSimilarity(0.28, 0.30, 0.34),
            CreateSections(0.22f, 0.38f, 0.18f),
            CreateSections(0.66f, 0.92f, 0.40f));

        Assert.Equal(TransitionStyle.RiskyClash, result.Style);
        Assert.Equal("Risky Clash", result.Label);
        Assert.Equal("Low harmonic fit, weak similarity, and a sharp energy jump make this transition fragile.", result.Reason);
    }

    private static TrackFingerprint CreateFingerprint(string hash, float globalEnergy)
    {
        return new TrackFingerprint
        {
            TrackUniqueHash = hash,
            Harmonic = new HarmonicVector
            {
                PrimaryKey = "8A",
                PrimaryConfidence = 0.9f,
                SecondaryKeys = ["9A"],
                SecondaryConfidences = [0.6f],
                PrimaryKeyPositionNormalized = 0.61f,
                ModulationScore = 0.12f,
                StabilityScore = 0.85f,
            },
            Energy = new EnergyVector
            {
                GlobalEnergy = globalEnergy,
                IntroEnergy = globalEnergy - 0.10f,
                BuildEnergy = globalEnergy + 0.02f,
                DropEnergy = globalEnergy + 0.18f,
                BreakdownEnergy = globalEnergy - 0.18f,
                OutroEnergy = globalEnergy - 0.12f,
                DropIntensity = 0.72f,
                BreakdownDepth = 0.62f,
                Confidence = 0.92f,
            },
            Rhythm = new RhythmVector { TempoNormalized = 0.6f, SwingGrooveScore = 0.55f, BeatHistogramSignature = 0.7f, PercussiveDensity = 0.7f, Confidence = 0.92f },
            Timbre = new TimbreVector { MfccTextureProxy = 0.68f, SpectralCentroidProfile = 0.52f, BrightnessWarmthBalance = 0.6f, SpectralComplexity = 0.58f, Confidence = 0.9f },
            Structure = new StructureVector { PhraseMapDensity = 0.66f, IntroLengthRatio = 0.12f, BuildLengthRatio = 0.18f, DropLengthRatio = 0.22f, BreakdownLengthRatio = 0.14f, OutroLengthRatio = 0.11f, BuildUpSlope = 0.7f, Confidence = 0.88f },
            Mood = new MoodVector { Danceability = 0.75f, Aggressiveness = 0.56f, Acousticness = 0.08f, TonalVsPercussiveBalance = 0.64f, Confidence = 0.9f },
        };
    }

    private static TrackSimilarityResult CreateSimilarity(double final, double harmonic, double overall, double drop = 0.62)
    {
        return new TrackSimilarityResult
        {
            FinalSimilarity = final,
            WholeTrackSimilarity = overall,
            SegmentSimilarity = final,
            VectorScores = new SimilarityVectorScores
            {
                Harmonic = harmonic,
                Energy = 0.6,
                Rhythm = 0.6,
                Timbre = 0.6,
                Structure = 0.6,
                Mood = 0.6,
            },
            SegmentScores = new SegmentSimilarityScores
            {
                Intro = 0.6,
                Build = 0.6,
                Drop = drop,
                Breakdown = 0.6,
                Outro = 0.6,
            },
        };
    }

    private static IReadOnlyList<SectionFeatureVector> CreateSections(float introEnergy, float dropEnergy, float breakdownEnergy)
    {
        return new List<SectionFeatureVector>
        {
            new() { SectionType = PhraseType.Intro, EnergyLevel = introEnergy, StartRatio = 0f, DurationRatio = 0.12f, Arousal = 0.42f, Danceability = 0.66f, SpectralBrightness = 0.38f, Confidence = 0.95f },
            new() { SectionType = PhraseType.Build, EnergyLevel = introEnergy + 0.12f, StartRatio = 0.2f, DurationRatio = 0.15f, Arousal = 0.61f, Danceability = 0.71f, SpectralBrightness = 0.52f, Confidence = 0.91f },
            new() { SectionType = PhraseType.Drop, EnergyLevel = dropEnergy, StartRatio = 0.38f, DurationRatio = 0.18f, Arousal = 0.86f, Danceability = 0.84f, SpectralBrightness = 0.69f, Confidence = 0.93f },
            new() { SectionType = PhraseType.Breakdown, EnergyLevel = breakdownEnergy, StartRatio = 0.58f, DurationRatio = 0.14f, Arousal = 0.34f, Danceability = 0.56f, SpectralBrightness = 0.28f, Confidence = 0.88f },
            new() { SectionType = PhraseType.Outro, EnergyLevel = introEnergy, StartRatio = 0.82f, DurationRatio = 0.12f, Arousal = 0.41f, Danceability = 0.61f, SpectralBrightness = 0.36f, Confidence = 0.9f },
        };
    }
}
