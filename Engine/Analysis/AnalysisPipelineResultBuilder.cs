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
    public static AnalysisPipelineResult Build(AudioFeaturesEntity f, IReadOnlyList<PhraseSegment>? phraseSegments = null)
    {
        var result = new AnalysisPipelineResult
        {
            Bpm = f.Bpm,
            DurationSeconds = f.TrackDuration,
            EnergyCurve = ParseJsonFloatArray(f.EnergyCurveJson) ?? Array.Empty<float>(),
            EssentiaInstrumentalProbability = f.InstrumentalProbability,
            // ONNX MTG-Jamendo mood head (clamped 0-1 at analysis time). Was hardcoded 0, which
            // silently disabled IntentClassifier's aggressive-drop boost for every track.
            EssentiaAggressiveProbability = f.MoodAggressive,
            EssentiaDanceability = f.Danceability,
            Genre = !string.IsNullOrWhiteSpace(f.DetectedSubGenre) ? f.DetectedSubGenre : f.ElectronicSubgenre,
            GenreConfidence = f.SubGenreConfidence > 0
                ? f.SubGenreConfidence
                : f.ElectronicSubgenreConfidence > 0 ? f.ElectronicSubgenreConfidence : 1f,
            GenreSource = !string.IsNullOrWhiteSpace(f.DetectedSubGenre)
                ? GenreSource.ModelInferred
                : GenreSource.Unknown,
        };

        // Phrase source, highest precedence first:
        //   1. A list the caller resolved itself (Cue Forge and the structural job read the fresh
        //      TrackPhrases rows, so they pass those in — the legacy JSON column is only rewritten
        //      when empty and can therefore hold boundaries from an older, wrong-duration analysis).
        //   2. The legacy PhraseSegmentsJson column, but only when it records a trusted provenance.
        //      An empty source means the row predates the bridge (or was written by an analysis that
        //      never finished), and its timestamps cannot be trusted for cue placement.
        //   3. Nothing — the DSP path scores raw audio signals instead.
        if (phraseSegments is not null)
        {
            result.PhraseSegments = PhraseSourceResolver.Sanitize(phraseSegments, f.TrackDuration);
        }
        else if (PhraseSourceResolver.IsTrustedSource(f.PhraseSegmentsSource))
        {
            result.PhraseSegments = PhraseSourceResolver.Sanitize(ParsePhraseSegments(f.PhraseSegmentsJson), f.TrackDuration);
        }

        // Detected first-drop timestamp (the value behind the header's "Time till drop"). It is a
        // reported consensus of the analysis rather than a single DSP candidate, so the generator
        // may use it to corroborate a drop candidate — see CueGenerationService's candidate merge.
        double? hintedDrop = f.TimeTillDropSeconds is > 0
            ? f.TimeTillDropSeconds
            : f.DropTimeSeconds is > 0 && f.DropConfidence >= 0.5f ? f.DropTimeSeconds : null;
        if (hintedDrop is > 0 && (f.TrackDuration <= 0 || hintedDrop < f.TrackDuration - 4))
            result.HintedDropTimestamp = hintedDrop.Value;

        // Rekordbox's own commercial phrase analysis (PSSI) and the rule-based structural analysis
        // ("Heuristic") are both trusted provenance for the legacy column; the gate itself lives in
        // PhraseSourceResolver so the JSON and TrackPhrases routes share one definition. The
        // per-segment duration clamping that used to live here (a stale analysis can claim a segment
        // runs past the real end) is part of that resolver's Sanitize step.

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
