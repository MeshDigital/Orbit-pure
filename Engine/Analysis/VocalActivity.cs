using System;
using System.Globalization;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Engine.Analysis;

/// <summary>Where a track sings, from per-patch voice probabilities (#285).</summary>
public sealed record VocalActivityResult(
    float Density,
    float? StartSeconds,
    float? EndSeconds,
    VocalType Type,
    float Intensity,
    string CurveJson);

/// <summary>
/// Turns the voice/instrumental head's per-patch probabilities (one per ≈2 s mel patch) into the
/// vocal fields analysis stores: density, the vocal span, the <see cref="VocalType"/> class and the
/// curve. A patch is voiced above <see cref="VoicedThreshold"/>; the span needs two voiced patches
/// in a row (≈4 s) so a single shout or a misfire doesn't stretch it across the track.
/// </summary>
public static class VocalActivity
{
    public const float VoicedThreshold = 0.6f;
    public const int MinRunPatches = 2;

    public static VocalActivityResult FromPatchProbabilities(float[] voice, double patchSeconds)
    {
        ArgumentNullException.ThrowIfNull(voice);
        if (voice.Length == 0)
            return new VocalActivityResult(0, null, null, VocalType.Instrumental, 0, "[]");

        var voiced = voice.Select(p => p > VoicedThreshold).ToArray();
        var density = voiced.Count(v => v) / (float)voice.Length;

        int? first = null, last = null;
        for (int i = 0; i + MinRunPatches - 1 < voice.Length; i++)
        {
            if (Enumerable.Range(i, MinRunPatches).All(j => voiced[j]))
            {
                first ??= i;
                last = i + MinRunPatches - 1;
            }
        }

        var type = density < 0.05f ? VocalType.Instrumental
            : density < 0.35f ? VocalType.VocalChops
            : VocalType.LeadVocal;
        var intensity = voiced.Any(v => v) ? voice.Where(p => p > VoicedThreshold).Average() : 0f;
        var curve = "[" + string.Join(",", voice.Select(p => Math.Round(p, 2).ToString("0.##", CultureInfo.InvariantCulture))) + "]";

        return new VocalActivityResult(
            density,
            first is int f ? (float)(f * patchSeconds) : null,
            last is int l ? (float)((l + 1) * patchSeconds) : null,
            type,
            intensity,
            curve);
    }

    /// <summary>Writes the result onto the analysis row.</summary>
    public static void Apply(VocalActivityResult result, AudioFeaturesEntity features)
    {
        features.VocalDensity = result.Density;
        features.VocalStartSeconds = result.StartSeconds;
        features.VocalEndSeconds = result.EndSeconds;
        features.DetectedVocalType = result.Type;
        features.VocalIntensity = result.Intensity;
        features.VocalDensityCurveJson = result.CurveJson;
    }
}
