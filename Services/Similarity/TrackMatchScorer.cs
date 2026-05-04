using System;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Similarity;

/// <summary>
/// Pure-static scorer that converts raw audio feature data for two tracks into a
/// <see cref="TrackMatchScore"/>.  No DI, no state — just math.
///
/// Caller responsibility:
///   • Supply <see cref="SectionFeatureVector"/> for each track's Drop/peak section
///     (from <see cref="SectionVectorService"/>).  Null is valid — the scorer falls
///     back to the embedding cosine for the drop-sonic dimension.
///   • Supply the embedding cosine similarity (already computed by
///     <see cref="SimilarityIndex"/>).
/// </summary>
public static class TrackMatchScorer
{
    /// <summary>
    /// Common DJ BPM ratios considered for beat matching.
    /// 1:1 exact, 2:1 double-time, 1:2 half-time, 3:2 / 2:3 phrase-sync.
    /// </summary>
    private static readonly (double Ratio, string Label)[] BpmRatios =
    [
        (1.0,        "Same BPM"),
        (2.0,        "2× (double time)"),
        (0.5,        "½ (half time)"),
        (1.5,        "3:2 ratio"),
        (2.0 / 3.0,  "2:3 ratio"),
    ];

    /// <summary>
    /// Exponential-decay half-width for the <see cref="TrackMatchScore.BeatScore"/>.
    /// 3 BPM → score ≈ 0.37, 6 BPM → score ≈ 0.14. Tuned for "feels compatible".
    /// </summary>
    private const double BeatDecayBpmWidth = 3.0;

    /// <summary>
    /// Tighter BPM tolerance used only for <see cref="TrackMatchScore.DoubleDropScore"/>.
    /// 1 BPM → score ≈ 0.37, 2 BPM → score ≈ 0.14. Must lock tight to double-drop.
    /// </summary>
    private const double TightBeatDecayBpmWidth = 1.0;

    // ── Main entry point ──────────────────────────────────────────────────

    /// <summary>
    /// Computes the full <see cref="TrackMatchScore"/> for the pair (a, b).
    /// </summary>
    /// <param name="a">Audio features for the seed / current track.</param>
    /// <param name="b">Audio features for the candidate track.</param>
    /// <param name="embeddingCosine">
    ///   Cosine similarity from <see cref="SimilarityIndex"/> for this pair.
    ///   Already in [0, 1] (the index converts distance back to similarity).
    ///   Pass 0.5 as a neutral value when not available.
    /// </param>
    /// <param name="dropA">Highest-confidence Drop section for track A, or null.</param>
    /// <param name="dropB">Highest-confidence Drop section for track B, or null.</param>
    public static TrackMatchScore Compute(
        AudioFeaturesEntity? a,
        AudioFeaturesEntity? b,
        double embeddingCosine,
        SectionFeatureVector? dropA = null,
        SectionFeatureVector? dropB = null,
        SectionFeatureVector? outroA = null,
        SectionFeatureVector? introB = null)
    {
        if (a is null || b is null)
            return TrackMatchScore.Unknown with
            {
                SoundScore = (float)Math.Clamp(embeddingCosine, 0, 1),
                OutroIntroScore = (float)Math.Clamp(embeddingCosine, 0, 1)
            };

        // ── Harmony ───────────────────────────────────────────────────────
        float harmonyScore  = ComputeHarmony(a.CamelotKey, b.CamelotKey,
                                             out string harmonyLabel);

        // ── Beat ──────────────────────────────────────────────────────────
        float beatScore     = ComputeBeat(a.Bpm, b.Bpm, BeatDecayBpmWidth,
                                          out string beatLabel,
                                          out double bestDiff,
                                          out string _);
        float tightBeatScore = ComputeBeatFromBestDiff(bestDiff, TightBeatDecayBpmWidth);

        // ── Sound (embedding cosine) ──────────────────────────────────────
        float soundScore    = (float)Math.Clamp(embeddingCosine, 0.0, 1.0);

        // ── Transition + drop section scoring ─────────────────────────────
        float dropSonicScore = ComputeDropSonic(dropA, dropB, soundScore);
        float outroIntroScore = ComputeTransitionScore(outroA, introB, soundScore);
        float doubleDropScore = ComputeDoubleDropScore(harmonyScore, tightBeatScore, dropA, dropB, soundScore);

        // ── Overall (weighted sum) ────────────────────────────────────────
        float overall = Math.Clamp(
            0.28f * soundScore +
            0.22f * harmonyScore +
            0.18f * beatScore +
            0.17f * dropSonicScore +
            0.15f * outroIntroScore,
            0f, 1f);

        // ── Drop verdict label ────────────────────────────────────────────
        string dropLabel = doubleDropScore >= 0.75f ? "⚡ Double drop ready"
                         : doubleDropScore >= 0.50f ? "Drop compatible"
                         : string.Empty;

        return new TrackMatchScore
        {
            HarmonyScore    = harmonyScore,
            BeatScore       = beatScore,
            SoundScore      = soundScore,
            DropSonicScore  = dropSonicScore,
            OutroIntroScore = outroIntroScore,
            DoubleDropScore = doubleDropScore,
            OverallScore    = overall,
            HarmonyLabel    = harmonyLabel,
            BeatLabel       = beatLabel,
            DropLabel       = dropLabel,
            BpmA            = a.Bpm,
            BpmB            = b.Bpm,
        };
    }

    // ── Harmony ────────────────────────────────────────────────────────────

    private static float ComputeHarmony(
        string? keyA,
        string? keyB,
        out string label)
    {
        const double maxCamelotSteps = 6.0;

        if (string.IsNullOrWhiteSpace(keyA) || string.IsNullOrWhiteSpace(keyB))
        {
            label = "Unknown key";
            return 0.5f; // neutral
        }

        double dist = Playlist.PlaylistOptimizer.CamelotDistancePublic(keyA, keyB);
        float score = (float)Math.Clamp(1.0 - dist / maxCamelotSteps, 0.0, 1.0);

        label = (int)dist switch
        {
            0 => "Same key",
            1 => "Compatible (+1)",
            2 => "Energy shift (+2)",
            3 => "Tension (+3)",
            _ => $"Different key ({(int)dist} steps)",
        };

        return score;
    }

    // ── Beat ───────────────────────────────────────────────────────────────

    private static float ComputeBeat(
        float bpmA,
        float bpmB,
        double decayWidth,
        out string label,
        out double bestDiff,
        out string bestRatioLabel)
    {
        bestDiff       = double.MaxValue;
        bestRatioLabel = string.Empty;
        label          = string.Empty;

        if (bpmA <= 0 || bpmB <= 0)
        {
            label = "Unknown BPM";
            bestDiff = 0;
            return 0.5f; // neutral
        }

        foreach (var (ratio, ratioLabel) in BpmRatios)
        {
            double diff = Math.Abs(bpmA - bpmB * ratio);
            if (diff < bestDiff)
            {
                bestDiff       = diff;
                bestRatioLabel = ratioLabel;
            }
        }

        float score = (float)Math.Clamp(Math.Exp(-bestDiff / decayWidth), 0.0, 1.0);

        // Build human label
        if (bestRatioLabel == "Same BPM")
        {
            label = bestDiff < 1.0 ? "Same BPM" : $"±{bestDiff:F1} BPM";
        }
        else
        {
            label = $"{bestRatioLabel} (±{bestDiff:F1})";
        }

        return score;
    }

    private static float ComputeBeatFromBestDiff(double bestDiff, double decayWidth)
        => (float)Math.Clamp(Math.Exp(-bestDiff / decayWidth), 0.0, 1.0);

    // ── Drop sonic ─────────────────────────────────────────────────────────

    public static float ComputeTransitionScore(
        SectionFeatureVector? outro,
        SectionFeatureVector? intro,
        float soundScoreFallback)
    {
        if (outro is null || intro is null)
            return soundScoreFallback;

        float structuralFlow = outro.TransitionScore(intro);
        float energyFlow = ComputeEnergyCompatibility(outro.EnergyLevel, intro.EnergyLevel);

        return Math.Clamp((structuralFlow * 0.65f) + (energyFlow * 0.35f), 0f, 1f);
    }

    public static float ComputeEnergyCompatibility(float outgoingEnergy, float incomingEnergy)
    {
        double difference = Math.Abs(outgoingEnergy - incomingEnergy);
        return (float)Math.Clamp(1.0 - difference, 0.0, 1.0);
    }

    public static float ComputeDoubleDropScore(
        float harmonyScore,
        float tightBeatScore,
        SectionFeatureVector? dropA,
        SectionFeatureVector? dropB,
        float soundScoreFallback)
    {
        float dropSonicScore = ComputeDropSonic(dropA, dropB, soundScoreFallback);
        float ddRaw = (float)Math.Pow((double)harmonyScore * tightBeatScore * dropSonicScore, 1.0 / 3.0);
        return Math.Clamp(ddRaw, 0f, 1f);
    }

    private static float ComputeDropSonic(
        SectionFeatureVector? dropA,
        SectionFeatureVector? dropB,
        float soundScoreFallback)
    {
        if (dropA is null || dropB is null)
            return soundScoreFallback; // no section data; use global embedding

        const double maxDist = 2.0;
        double dist = dropA.DistanceTo(dropB);
        return (float)Math.Clamp(1.0 - dist / maxDist, 0.0, 1.0);
    }
}
