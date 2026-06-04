using System;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

public class TrackHarmonicAnalysisTests
{
    private readonly HarmonicAnalysisService _sut = new();

    [Fact]
    public void BuildHarmonicVector_ExtractsPrimaryCamelotKey()
    {
        var output = new EssentiaOutput
        {
            Tonal = new TonalData
            {
                KeyEdma = new KeyData { Key = "C", Scale = "minor", Strength = 0.81f },
            },
        };

        var features = new AudioFeaturesEntity { Key = "C", Scale = "minor", CamelotKey = "5A", KeyConfidence = 0.81f };
        var harmonic = _sut.BuildHarmonicVector(output, features);

        Assert.Equal("5A", harmonic.PrimaryKey);
        Assert.Equal(0.81f, harmonic.PrimaryConfidence, 3);
    }

    [Fact]
    public void BuildHarmonicVector_ExtractsThreeSecondaryCandidates_FromChordStack()
    {
        var chordStack = JsonDocument.Parse("""
        [
          { "value": "Gm", "probability": 0.72 },
          { "value": "Dm", "probability": 0.63 },
          { "value": "Eb", "probability": 0.54 }
        ]
        """).RootElement.Clone();

        var output = new EssentiaOutput
        {
            Tonal = new TonalData
            {
                KeyEdma = new KeyData { Key = "C", Scale = "minor", Strength = 0.81f },
                ChordsKey = chordStack,
            },
        };

        var features = new AudioFeaturesEntity { Key = "C", Scale = "minor", CamelotKey = "5A", KeyConfidence = 0.81f };
        var harmonic = _sut.BuildHarmonicVector(output, features);

        Assert.Equal(3, harmonic.SecondaryKeys.Length);
        Assert.Equal(3, harmonic.SecondaryConfidences.Length);
    }

    [Fact]
    public void BuildHarmonicVector_StableChordStack_YieldsLowModulation()
    {
        var chordStack = JsonDocument.Parse("""
        [
          { "value": "Cm", "probability": 0.82 },
          { "value": "Cm", "probability": 0.79 },
          { "value": "Cm", "probability": 0.75 }
        ]
        """).RootElement.Clone();

        var output = new EssentiaOutput
        {
            Tonal = new TonalData
            {
                KeyEdma = new KeyData { Key = "C", Scale = "minor", Strength = 0.85f },
                ChordsKey = chordStack,
            },
        };

        var features = new AudioFeaturesEntity { CamelotKey = "5A", KeyConfidence = 0.85f };
        var harmonic = _sut.BuildHarmonicVector(output, features);

        Assert.InRange(harmonic.ModulationScore, 0f, 0.2f);
    }

    [Fact]
    public void BuildHarmonicVector_ShiftingChordStack_YieldsHigherModulation()
    {
        var chordStack = JsonDocument.Parse("""
        [
          { "value": "Gm", "probability": 0.72 },
          { "value": "Dm", "probability": 0.63 },
          { "value": "Eb", "probability": 0.54 }
        ]
        """).RootElement.Clone();

        var output = new EssentiaOutput
        {
            Tonal = new TonalData
            {
                KeyEdma = new KeyData { Key = "C", Scale = "minor", Strength = 0.81f },
                ChordsKey = chordStack,
            },
        };

        var features = new AudioFeaturesEntity { CamelotKey = "5A", KeyConfidence = 0.81f };
        var harmonic = _sut.BuildHarmonicVector(output, features);

        Assert.True(harmonic.ModulationScore > 0.2f);
    }

    [Fact]
    public void BuildHarmonicVector_Stability_IsInverseOfModulationBias()
    {
        var output = new EssentiaOutput
        {
            Tonal = new TonalData
            {
                KeyEdma = new KeyData { Key = "A", Scale = "minor", Strength = 0.9f },
            },
        };

        var features = new AudioFeaturesEntity { CamelotKey = "8A", KeyConfidence = 0.9f };
        var harmonic = _sut.BuildHarmonicVector(output, features);

        Assert.InRange(harmonic.StabilityScore, 0f, 1f);
        Assert.True(harmonic.StabilityScore >= 1f - harmonic.ModulationScore - 0.3f);
    }

    [Fact]
    public void ComputeCompatibility_IdenticalKeys_IsHigh()
    {
        var a = new HarmonicVector { PrimaryKey = "8A", PrimaryConfidence = 0.9f, StabilityScore = 0.9f };
        var b = new HarmonicVector { PrimaryKey = "8A", PrimaryConfidence = 0.9f, StabilityScore = 0.9f };

        var score = _sut.ComputeCompatibility(a, b);

        Assert.True(score >= 0.9f);
    }

    [Fact]
    public void ComputeCompatibility_AdjacentCamelot_IsMedium()
    {
        var a = new HarmonicVector { PrimaryKey = "8A", PrimaryConfidence = 0.9f, StabilityScore = 0.85f };
        var b = new HarmonicVector { PrimaryKey = "9A", PrimaryConfidence = 0.85f, StabilityScore = 0.85f };

        var score = _sut.ComputeCompatibility(a, b);

        Assert.InRange(score, 0.65f, 0.9f);
    }

    [Fact]
    public void ComputeCompatibility_DistantKeys_IsLow()
    {
        var a = new HarmonicVector { PrimaryKey = "1A", PrimaryConfidence = 0.9f, StabilityScore = 0.9f };
        var b = new HarmonicVector { PrimaryKey = "7B", PrimaryConfidence = 0.9f, StabilityScore = 0.9f };

        var score = _sut.ComputeCompatibility(a, b);

        Assert.True(score < 0.5f);
    }

    [Fact]
    public void Builder_FailOpen_WhenHarmonicBuildThrows_ReturnsFingerprintWithoutHarmonic()
    {
        var builder = new TrackFingerprintBuilderService(new ThrowingHarmonicAnalysisService());
        var fingerprint = builder.Build(
            "fail_open_hash",
            new AudioFeaturesEntity { TrackUniqueHash = "fail_open_hash", TrackDuration = 120, Bpm = 128, BpmConfidence = 0.8f },
            new EssentiaOutput(),
            Array.Empty<TrackPhraseEntity>(),
            DateTime.UnixEpoch);

        Assert.NotNull(fingerprint);
        Assert.Null(fingerprint.Harmonic);
    }

    private sealed class ThrowingHarmonicAnalysisService : HarmonicAnalysisService
    {
        public override HarmonicVector BuildHarmonicVector(EssentiaOutput? output, AudioFeaturesEntity features)
            => throw new InvalidOperationException("boom");
    }
}