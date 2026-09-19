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

        // EDMFormer ML output, Rekordbox's own commercial phrase analysis (PSSI, via
        // RekordboxPssiService), and the rule-based StructuralAnalysisEngine ("Heuristic") are all
        // trusted as "phrase segments" for GenerateCues' Path-1 priority gate. Heuristic was
        // previously excluded on the theory that it's the same weak signal the DSP path
        // (sub-bass/novelty) already improves on — but verified against real Rekordbox-cued
        // tracks, Heuristic's structural analysis was repeatedly the *correct* answer (e.g. a
        // track where it placed the real drop within 0.4s of the DJ's own hand-set cue) while the
        // DSP path it was supposedly deferring to had picked a completely different, wrong
        // section. CueGenerationService.SanitizeSegments normalizes Heuristic's ordinal-suffixed
        // labels ("Drop 1", "Drop 5") down to the plain "Drop"/"Build"/etc. vocabulary the other
        // two sources use.
        if (f.PhraseSegmentsSource is "EDMFormer" or "RekordboxPSSI" or "Heuristic")
        {
            var phraseSegments = ParsePhraseSegments(f.PhraseSegmentsJson);

            // Sanity gate: verified against the real library (2,157 of 3,375 analyzed tracks —
            // 64%, overwhelmingly "Heuristic"-sourced), phrase data can describe events well past
            // where the track actually ends — stale data left over from a track whose
            // TrackDuration was wrong at analysis time and has since been corrected elsewhere
            // without the structural analysis ever being re-run against the fix. Trusting it
            // anyway silently misplaces every downstream cue by however far the old and new
            // durations disagree, sometimes hundreds of seconds.
            //
            // Originally this discarded the WHOLE segment list whenever the last segment ran past
            // duration — but a segment's Start is a real detected event timestamp, while its
            // Duration is often an estimate/extrapolation (especially for the last segment in a
            // list, which sometimes pads to "however long is left"). A track can have perfectly
            // correct early segments (verified case: a drop placed within 0.4s of the DJ's own
            // hand-set cue) alongside one bad trailing segment whose claimed Duration runs past
            // the real end — discarding everything threw away the good data along with the bad.
            // So instead: drop only segments that START after the track has already ended (those
            // can never be correct at all), and clamp the Duration of any segment that starts
            // in-bounds but claims to run past the end, rather than rejecting it outright.
            if (f.TrackDuration > 0 && phraseSegments.Count > 0)
            {
                const double ToleranceSeconds = 5.0;
                double duration = f.TrackDuration;
                phraseSegments = phraseSegments
                    .Where(s => s.Start <= duration + ToleranceSeconds)
                    .Select(s =>
                    {
                        double maxDuration = Math.Max(0, duration - s.Start);
                        if (s.Duration > maxDuration + ToleranceSeconds)
                            s.Duration = (float)maxDuration;
                        return s;
                    })
                    .ToList();
            }

            result.PhraseSegments = phraseSegments;
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
