using System;
using System.Linq;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Energy weights for EffNet's 128-frame patches (GA7, #252): a 32-bar kick-only intro, an outro
/// and a breakdown otherwise weigh as much as the drops in the track's style prediction. Patches
/// are ranked by RMS: the loudest 40 % count fully, the next 40 % half, the quietest 20 % at
/// <see cref="QuietWeight"/>. Computed from the same 16 kHz audio the patches come from, so it
/// works on every analysis run (Mood-only backfills have no RMS curve). Pure; zero extra inference.
/// </summary>
public static class PatchEnergyWeights
{
    public const float LoudWeight = 1.0f;
    public const float MiddleWeight = 0.5f;
    public const float QuietWeight = 0.15f;

    /// <summary>Tag suffix for style scores pooled with these weights; bump when the weighting changes.</summary>
    public const string PoolTag = "pool=energy1";

    /// <summary>One weight per patch; all 1 when there are fewer than 3 patches or the audio is silent.</summary>
    public static float[] Compute(float[] audio16k, int patchCount)
    {
        var weights = Enumerable.Repeat(1f, Math.Max(0, patchCount)).ToArray();
        if (patchCount < 3 || audio16k is null || audio16k.Length == 0) return weights;

        var patchSamples = EffnetMelSpectrogramExtractor.PatchFrames * EffnetMelSpectrogramExtractor.HopSize;
        var rms = new double[patchCount];
        for (var p = 0; p < patchCount; p++)
        {
            var start = p * patchSamples;
            var end = Math.Min(audio16k.Length, start + patchSamples + EffnetMelSpectrogramExtractor.FrameSize - EffnetMelSpectrogramExtractor.HopSize);
            double sum = 0; var n = 0;
            for (var i = start; i < end; i += 2) { sum += (double)audio16k[i] * audio16k[i]; n++; }
            rms[p] = n > 0 ? Math.Sqrt(sum / n) : 0;
        }
        if (rms.Max() <= 1e-9) return weights;

        // Rank by loudness (ties keep track order, so the result is deterministic).
        var order = Enumerable.Range(0, patchCount).OrderByDescending(p => rms[p]).ThenBy(p => p).ToArray();
        var loudCount = (int)Math.Ceiling(0.4 * patchCount);
        var quietStart = patchCount - (int)Math.Floor(0.2 * patchCount);
        for (var rank = 0; rank < patchCount; rank++)
            weights[order[rank]] = rank < loudCount ? LoudWeight : rank >= quietStart ? QuietWeight : MiddleWeight;
        return weights;
    }
}
