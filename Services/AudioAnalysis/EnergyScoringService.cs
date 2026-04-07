using System;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Computes a Mixed-In-Key-style energy score (1–10) from Essentia low-level
/// features: Danceability, DynamicComplexity, and average loudness (RMS/LUFS).
///
/// Formula:
///   raw = w_dance * Danceability + w_dynamic * DynamicComplexity_norm + w_loud * Loudness_norm
///   EnergyScore = round(clamp(raw * 10, 1, 10))
///
/// Weights are empirically tuned to match the intuitive MIK 1–10 scale:
///   - Danceability  (0-1)  drives 40 % of the score
///   - DynamicComplexity (0–9 typical range, normalised) drives 35 %
///   - Loudness (0–1 normalised from RMS or LUFS) drives 25 %
/// </summary>
public sealed class EnergyScoringService
{
    private const float WeightDanceability      = 0.40f;
    private const float WeightDynamicComplexity = 0.35f;
    private const float WeightLoudness          = 0.25f;

    /// <summary>
    /// Typical maximum for Essentia DynamicComplexity (beyond this = heavily dynamic).
    /// Used to normalise the raw value to [0, 1].
    /// </summary>
    private const float DynamicComplexityMax = 9f;

    /// <summary>
    /// Reference loudness floor in dBFS (normalised RMS).  Values below this
    /// map to 0; at 0 dBFS the normalised value is 1.
    /// </summary>
    private const float LoudnessFloorDb = -60f;

    /// <summary>
    /// Scores the track from <paramref name="essentiaOutput"/> and writes the 1–10
    /// integer EnergyScore plus the raw [0,1] Energy signal into <paramref name="target"/>.
    /// </summary>
    public void Score(EssentiaOutput essentiaOutput, AudioFeaturesEntity target)
    {
        ArgumentNullException.ThrowIfNull(essentiaOutput);
        ArgumentNullException.ThrowIfNull(target);

        var ll = essentiaOutput.LowLevel;
        
        float danceability       = essentiaOutput.Rhythm?.Danceability ?? target.Danceability;
        float dynamicComplexity  = ll?.DynamicComplexity ?? target.DynamicComplexity;
        float rms                = ll?.Rms?.Mean ?? 0f;

        // Normalise DynamicComplexity to [0, 1]
        float dynNorm = Math.Clamp(dynamicComplexity / DynamicComplexityMax, 0f, 1f);

        // Normalise RMS (0–1 linear where 1 = full-scale)
        float loudNorm = rms > 0f ? Math.Clamp(rms, 0f, 1f) : NormaliseLufs(target.LoudnessLUFS);

        float raw = WeightDanceability      * Math.Clamp(danceability, 0f, 1f)
                  + WeightDynamicComplexity * dynNorm
                  + WeightLoudness          * loudNorm;

        // Map [0, 1] → [1, 10]
        int score = (int)Math.Round(Math.Clamp(raw * 10f, 1f, 10f));

        target.Energy      = Math.Clamp(raw, 0f, 1f);
        target.EnergyScore = score;
        target.Danceability = Math.Clamp(danceability, 0f, 1f);

        if (ll != null)
        {
            target.DynamicComplexity = dynamicComplexity;
            target.IsDynamicCompressed = dynamicComplexity < 2.0f && target.LoudnessLUFS > -7f;
        }
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    /// <summary>Normalises an LUFS value to [0, 1].</summary>
    private static float NormaliseLufs(float lufs)
    {
        if (lufs <= LoudnessFloorDb || lufs == 0f) return 0f;
        // e.g. -14 LUFS → ~0.77; -8 LUFS → ~0.87
        return Math.Clamp((lufs - LoudnessFloorDb) / (-LoudnessFloorDb), 0f, 1f);
    }
}
