using System.Text.Json;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Covers CueForgeViewModel.BuildAnalysisResultFromFeatures — the bridge between what
/// AudioAnalysisService persists on AudioFeaturesEntity and the AnalysisPipelineResult
/// Engine.Cueing.CueGenerationService actually scores against. This is the exact seam
/// where drop signals used to get collapsed down to a single guessed float; these tests
/// pin down that real multi-candidate data now survives the round trip, and that the
/// legacy fallback still works for tracks analysed before that existed.
/// </summary>
public class CueForgeAnalysisResultBuilderTests
{
    private static AudioFeaturesEntity BaseFeatures() => new()
    {
        TrackUniqueHash = "hash",
        Bpm = 174f,
        TrackDuration = 240,
    };

    [Fact]
    public void UsesRealSubBassAndNoveltySignals_WhenPresent()
    {
        var features = BaseFeatures();
        features.SubBassDropoutTimestampsJson = JsonSerializer.Serialize(new[] { 40.0, 90.0 });
        features.SubBassReturnTimestampsJson = JsonSerializer.Serialize(new[] { 100.0, 200.0 });
        features.NoveltyDropSignaturesJson = JsonSerializer.Serialize(new[]
        {
            new NoveltyDropSignatureDto(100.0, 84.0, 0.9f),
        });
        // Legacy fallback fields are also populated here — real data must win, not these.
        features.DropTimeSeconds = 9999f;
        features.DropConfidence = 0.9f;
        features.CueDrop = 8888f;

        var result = CueForgeViewModel.BuildAnalysisResultFromFeatures(features);

        Assert.Equal(new[] { 40.0, 90.0 }, result.SubBassDropoutTimestamps);
        Assert.Equal(new[] { 100.0, 200.0 }, result.SubBassReturnTimestamps);
        Assert.Single(result.NoveltyDropSignatures);
        Assert.Equal(100.0, result.NoveltyDropSignatures[0].DropSeconds);
        Assert.DoesNotContain(9999.0, result.SubBassReturnTimestamps);
    }

    [Fact]
    public void FallsBackToCollapsedFloat_WhenNoRealSignalsStored()
    {
        var features = BaseFeatures();
        features.DropTimeSeconds = 120f;
        features.DropConfidence = 0.6f;
        features.CueDrop = 118f;
        features.CueBuild = 100f;
        // Real JSON columns left at their "[]" default — simulates a track analysed
        // before AudioAnalysisService started persisting these.

        var result = CueForgeViewModel.BuildAnalysisResultFromFeatures(features);

        Assert.Single(result.SubBassReturnTimestamps);
        Assert.Equal(120.0, result.SubBassReturnTimestamps[0]);
        Assert.Single(result.NoveltyDropSignatures);
        Assert.Equal(118.0, result.NoveltyDropSignatures[0].DropSeconds);
        Assert.Equal(100.0, result.NoveltyDropSignatures[0].BuildStartSeconds);
    }

    [Fact]
    public void LowConfidenceDropTimeSeconds_IsNotUsedAsFallback()
    {
        var features = BaseFeatures();
        features.DropTimeSeconds = 120f;
        features.DropConfidence = 0.1f; // below the 0.4 confidence gate

        var result = CueForgeViewModel.BuildAnalysisResultFromFeatures(features);

        Assert.Empty(result.SubBassReturnTimestamps);
    }

    [Fact]
    public void NoSignalsAtAll_ProducesEmptyListsWithoutThrowing()
    {
        var features = BaseFeatures();

        var result = CueForgeViewModel.BuildAnalysisResultFromFeatures(features);

        Assert.Empty(result.SubBassDropoutTimestamps);
        Assert.Empty(result.SubBassReturnTimestamps);
        Assert.Empty(result.NoveltyDropSignatures);
        Assert.Empty(result.PhraseSegments);
    }

    [Fact]
    public void MalformedJson_FallsBackGracefully_WithoutThrowing()
    {
        var features = BaseFeatures();
        features.SubBassReturnTimestampsJson = "{not valid json";
        features.NoveltyDropSignaturesJson = "{not valid json";
        features.PhraseSegmentsJson = "{not valid json";
        features.DropTimeSeconds = 50f;
        features.DropConfidence = 0.9f;

        var result = CueForgeViewModel.BuildAnalysisResultFromFeatures(features);

        // Malformed real-signal JSON should behave as if it were empty, not crash the
        // whole Auto-Generate flow — and should still fall back to the legacy float.
        Assert.Single(result.SubBassReturnTimestamps);
        Assert.Equal(50.0, result.SubBassReturnTimestamps[0]);
        Assert.Empty(result.PhraseSegments);
    }

    [Fact]
    public void PhraseSegments_DeserializedFromJson()
    {
        var features = BaseFeatures();
        features.PhraseSegmentsJson = JsonSerializer.Serialize(new[]
        {
            new SLSKDONET.Models.PhraseSegment { Label = "Drop", Start = 100f, Duration = 30f, Confidence = 0.9f },
        });

        var result = CueForgeViewModel.BuildAnalysisResultFromFeatures(features);

        Assert.Single(result.PhraseSegments);
        Assert.Equal("Drop", result.PhraseSegments[0].Label);
    }

    [Fact]
    public void CopiesBpmAndDuration()
    {
        var features = BaseFeatures();

        var result = CueForgeViewModel.BuildAnalysisResultFromFeatures(features);

        Assert.Equal(174f, result.Bpm);
        Assert.Equal(240, result.DurationSeconds);
    }
}
