using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Reconstructs an <see cref="AnalysisPipelineResult"/> from a persisted <see cref="AudioFeaturesEntity"/>
/// (the fast path — no audio re-decode required). Shared by <see cref="SLSKDONET.ViewModels.CueForgeViewModel"/>'s
/// manual Auto-Generate button and <see cref="SLSKDONET.Services.AnalyzeTrackStructureJob"/>'s automatic
/// background pipeline, so both routes score cues against the exact same signals.
/// </summary>
public static class AnalysisPipelineResultBuilder
{
    public static AnalysisPipelineResult Build(AudioFeaturesEntity f)
    {
        var result = new AnalysisPipelineResult
        {
            Bpm = f.Bpm,
            DurationSeconds = f.TrackDuration,
            EnergyCurve = ParseJsonFloatArray(f.EnergyCurveJson) ?? Array.Empty<float>(),
            EssentiaInstrumentalProbability = f.InstrumentalProbability,
            EssentiaAggressiveProbability = 0f,
            EssentiaDanceability = f.Danceability,
            Genre = !string.IsNullOrWhiteSpace(f.DetectedSubGenre) ? f.DetectedSubGenre : f.ElectronicSubgenre,
        };

        // Only genuine EDMFormer ML output is trusted as "phrase segments" for GenerateCues'
        // Path-1 priority gate. The rule-based StructuralAnalysisEngine bridge writes to the same
        // PhraseSegmentsJson field (so Cue Forge's phrase map isn't empty without EDMFormer), but
        // it's the same weak signal the DSP path (sub-bass/novelty) is meant to improve on — if it
        // were allowed through here it would silently outrank and starve that DSP path.
        if (f.PhraseSegmentsSource == "EDMFormer")
        {
            result.PhraseSegments = ParsePhraseSegments(f.PhraseSegmentsJson);
        }

        // Real multi-candidate drop signals (SubBassDropoutEngine / SpectralFluxNoveltyEngine,
        // computed once at analysis time in AudioAnalysisService). Falls back to the old
        // single-collapsed-float reconstruction only for tracks analysed before this existed
        // and not yet re-analysed — GenerateCues' DSP scoring path can compare many real
        // candidates instead of always being handed exactly one guess.
        var dropouts = ParseJsonDoubleList(f.SubBassDropoutTimestampsJson);
        var returns = ParseJsonDoubleList(f.SubBassReturnTimestampsJson);
        var signatures = ParseJsonNoveltySignatures(f.NoveltyDropSignaturesJson);
        var strippingStarts = ParseJsonDoubleList(f.StructuralStrippingStartTimestampsJson);
        var strippingReturns = ParseJsonDoubleList(f.StructuralStrippingReturnTimestampsJson);

        if (strippingStarts.Count > 0) result.StructuralStrippingStartTimestamps = strippingStarts;
        if (strippingReturns.Count > 0) result.StructuralStrippingReturnTimestamps = strippingReturns;

        if (dropouts.Count > 0) result.SubBassDropoutTimestamps = dropouts;
        if (returns.Count > 0)
        {
            result.SubBassReturnTimestamps = returns;
        }
        else if (f.DropTimeSeconds.HasValue && f.DropConfidence > 0.4f)
        {
            result.SubBassReturnTimestamps = new List<double> { f.DropTimeSeconds.Value };
        }

        if (signatures.Count > 0)
        {
            result.NoveltyDropSignatures = signatures
                .Select(s => (s.DropSeconds, s.BuildStartSeconds, s.Strength))
                .ToList();
        }
        else if (f.CueDrop.HasValue)
        {
            double buildStart = f.CueBuild.HasValue
                ? f.CueBuild.Value
                : Math.Max(0, f.CueDrop.Value - 16 * (60.0 / Math.Max(1, f.Bpm)));
            result.NoveltyDropSignatures = new List<(double, double, float)> { (f.CueDrop.Value, buildStart, f.DropConfidence) };
        }

        return result;
    }

    private static float[]? ParseJsonFloatArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return null;
        try { return JsonSerializer.Deserialize<float[]>(json); } catch { return null; }
    }

    private static List<PhraseSegment> ParsePhraseSegments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<PhraseSegment>>(json) ?? new(); } catch { return new(); }
    }

    private static List<double> ParseJsonDoubleList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<double>>(json) ?? new(); } catch { return new(); }
    }

    private static List<NoveltyDropSignatureDto> ParseJsonNoveltySignatures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<NoveltyDropSignatureDto>>(json) ?? new(); } catch { return new(); }
    }
}
