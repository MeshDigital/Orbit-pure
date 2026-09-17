using System;
using System.Collections.Generic;
using SLSKDONET.ViewModels.Workstation;

namespace SLSKDONET.Services.Playlist;

/// <summary>
/// Row-friendly (non-canvas) façade over the harmonic/energy compatibility formulas the
/// Workstation "SET PLAN" Flow timeline already uses
/// (<see cref="WorkstationViewModel.ComputeFlowHarmonicCompatibility"/>,
/// <see cref="WorkstationViewModel.ComputeFlowEnergyCompatibility"/>). Deliberately delegates
/// rather than re-implements for those two axes, so they never drift apart.
///
/// BPM and genre are NOT shared with the Flow timeline's own combine formula
/// (<see cref="WorkstationViewModel.ComputeCombinedFlowCompatibilityScore"/>, which stays a plain
/// 0.6/0.4 harmonic/energy blend) — this facade's <see cref="PairScore.CombinedScore"/>
/// intentionally diverges from it now that the Mix badge and reorder-suggestion features need a
/// richer signal than the low-level per-transition editor does.
/// </summary>
public static class TrackPairCompatibilityScorer
{
    // NOTE: HarmonicScore/HarmonicLabel/EnergyScore/EnergyLabel/CombinedScore keep their original
    // positional order (existing callers, e.g. TransitionPresetLibraryTests, construct this via
    // positional args) — Bpm/Genre fields are appended after CombinedScore rather than inserted
    // before it, so those call sites keep compiling unchanged.
    public readonly record struct PairScore(
        double HarmonicScore, string HarmonicLabel,
        double EnergyScore, string EnergyLabel,
        double CombinedScore,
        double BpmScore = 65.0, string BpmLabel = "unknown",
        double GenreScore = 65.0, string GenreLabel = "unknown");

    /// <param name="outgoingBpm">Outgoing track's BPM, if known. Null/0 on either side scores a
    /// neutral mid value rather than penalizing unanalyzed tracks.</param>
    /// <param name="incomingBpm">Incoming track's BPM, if known.</param>
    /// <param name="genreSimilarity">Cosine similarity (0..1) between the two tracks' style
    /// embeddings from <see cref="Similarity.SimilarityIndex"/>, or null when either track lacks
    /// an embedding — treated as neutral, not penalized.</param>
    public static PairScore Score(
        string? outgoingCamelotKey, string? incomingCamelotKey,
        double? outgoingEnergy, double? incomingEnergy,
        double transitionLengthSeconds = 8.0,
        double? outgoingBpm = null, double? incomingBpm = null,
        double? genreSimilarity = null)
    {
        var harmonic = WorkstationViewModel.ComputeFlowHarmonicCompatibility(outgoingCamelotKey, incomingCamelotKey, semitoneShift: 0);
        var energy = WorkstationViewModel.ComputeFlowEnergyCompatibility(outgoingEnergy, incomingEnergy, transitionLengthSeconds);
        var (bpmScore, bpmLabel) = ComputeBpmScore(outgoingBpm, incomingBpm);
        var (genreScore, genreLabel) = ComputeGenreScore(genreSimilarity);

        var combined = Math.Round(Math.Clamp(
            (harmonic.Score * 0.30) + (energy.Score * 0.20) + (bpmScore * 0.30) + (genreScore * 0.20),
            0.0, 100.0), 1);

        return new PairScore(
            harmonic.Score, harmonic.Label,
            energy.Score, energy.Label,
            combined,
            bpmScore, bpmLabel,
            genreScore, genreLabel);
    }

    /// <summary>Compatibility-score bucket → the badge color used for both the Flow timeline and the inline Mix badge.</summary>
    public static string CompatibilityColor(double combinedScore) => combinedScore switch
    {
        >= 80 => "#66B8E986",
        >= 60 => "#66FFD58A",
        >= 40 => "#66FFA94B",
        _ => "#66FF6B6B",
    };

    /// <summary>
    /// Human-readable reasons a transition scored poorly, for the badge tooltip. Mirrors
    /// <see cref="WorkstationViewModel"/>'s own (private) BuildFlowWarningFlags pattern.
    /// </summary>
    public static IReadOnlyList<string> BuildWarnings(PairScore score, double? outgoingBpm = null, double? incomingBpm = null)
    {
        var warnings = new List<string>();

        if (score.BpmLabel == "clash" && outgoingBpm is > 0 && incomingBpm is > 0)
        {
            var pctOff = 100.0 * Math.Abs(outgoingBpm.Value - incomingBpm.Value) / Math.Max(outgoingBpm.Value, incomingBpm.Value);
            warnings.Add($"BPM gap: {outgoingBpm:F0} → {incomingBpm:F0} ({pctOff:F0}% off)");
        }
        else if (score.BpmLabel == "clash")
        {
            warnings.Add("Large BPM gap");
        }

        if (score.GenreLabel is "drifting" or "clash")
        {
            warnings.Add("Different genre/style");
        }

        if (score.HarmonicLabel == "risky")
        {
            warnings.Add("Harmonic mismatch risk");
        }

        if (score.EnergyLabel == "mismatch")
        {
            warnings.Add("Energy jump is aggressive");
        }

        return warnings;
    }

    /// <summary>
    /// Scores BPM compatibility using the smallest relative gap across direct, half-time, and
    /// double-time ratios — halftime/doubletime mixing (e.g. 90 vs 180 BPM) is normal DJ practice,
    /// not a mismatch, so a naive direct-difference comparison would wrongly flag it as one.
    /// </summary>
    private static (double Score, string Label) ComputeBpmScore(double? outgoingBpm, double? incomingBpm)
    {
        if (outgoingBpm is not > 0 || incomingBpm is not > 0)
            return (65.0, "unknown");

        double a = outgoingBpm.Value, b = incomingBpm.Value;
        double direct = Math.Abs(a - b) / Math.Max(a, b);
        double half = Math.Abs(a - (b * 2)) / Math.Max(a, b * 2);
        double dbl = Math.Abs((a * 2) - b) / Math.Max(a * 2, b);
        double bestGap = Math.Min(direct, Math.Min(half, dbl));

        return bestGap switch
        {
            <= 0.06 => (92.0, "locked"),
            <= 0.10 => (75.0, "pitchable"),
            <= 0.16 => (50.0, "stretch"),
            _ => (15.0, "clash"),
        };
    }

    /// <summary>
    /// Scores genre/style compatibility from an embedding cosine similarity, when available.
    /// </summary>
    private static (double Score, string Label) ComputeGenreScore(double? genreSimilarity)
    {
        if (genreSimilarity is not double sim)
            return (65.0, "unknown");

        var score = Math.Clamp(sim * 100.0, 0.0, 100.0);
        var label = score switch
        {
            >= 80 => "cohesive",
            >= 60 => "related",
            >= 40 => "drifting",
            _ => "clash",
        };

        return (score, label);
    }
}
