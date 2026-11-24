using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Engine.Analysis;

/// <summary>Where a track sings, derived from the Demucs vocal stem's energy (#285).</summary>
public sealed record VocalActivityResult(
    float Density,
    float? StartSeconds,
    float? EndSeconds,
    VocalType Type,
    float Intensity,
    string CurveJson);

/// <summary>
/// Turns a per-window RMS curve of an isolated vocal stem into the vocal fields analysis stores:
/// density, the vocal span, the <see cref="VocalType"/> class, the intensity and the per-window
/// activity curve.
///
/// A window is "voiced" when its RMS is within <see cref="DefaultThresholdDb"/> of the stem's peak
/// and above <see cref="AbsoluteFloor"/> (so a track whose vocal stem is nothing but leakage or
/// silence — an instrumental — never reads as voiced). The span is the first and last window that
/// sit inside a run of at least <see cref="MinRunWindows"/> consecutive voiced windows, so a single
/// stray hit doesn't stretch it across the whole track.
/// </summary>
public static class VocalActivity
{
    /// <summary>Density below this reads as instrumental (matches the existing <see cref="VocalType"/> docs).</summary>
    public const float InstrumentalDensity = 0.05f;

    /// <summary>Density at or above this reads as a lead vocal; in between is sparse vocal chops.</summary>
    public const float LeadVocalDensity = 0.35f;

    /// <summary>A voiced window's RMS may be this many dB below the stem's peak.</summary>
    public const float DefaultThresholdDb = -28f;

    /// <summary>Absolute RMS floor: below this the stem is treated as silent/leakage regardless of its peak.</summary>
    public const float AbsoluteFloor = 1e-3f;

    /// <summary>Consecutive voiced windows needed to open or close the span.</summary>
    public const int MinRunWindows = 2;

    public static VocalActivityResult FromStemRms(
        IReadOnlyList<float> rms,
        double windowSeconds,
        float thresholdDb = DefaultThresholdDb)
    {
        if (rms is null || rms.Count == 0)
            return new VocalActivityResult(0, null, null, VocalType.Instrumental, 0, "[]");

        var peak = rms.Max();
        if (peak <= 0f || !float.IsFinite(peak) || peak < AbsoluteFloor)
            return new VocalActivityResult(0, null, null, VocalType.Instrumental, 0, Curve(rms, peak));

        var threshold = Math.Max(peak * DbToLinear(thresholdDb), AbsoluteFloor);
        var voiced = new bool[rms.Count];
        for (var i = 0; i < rms.Count; i++)
            voiced[i] = rms[i] >= threshold;

        var density = voiced.Count(v => v) / (float)voiced.Length;

        int? first = null, last = null;
        for (var i = 0; i + MinRunWindows - 1 < voiced.Length; i++)
        {
            if (!RunVoiced(voiced, i, MinRunWindows)) continue;
            first ??= i;
            last = i + MinRunWindows - 1;
        }

        var type = density < InstrumentalDensity ? VocalType.Instrumental
            : density < LeadVocalDensity ? VocalType.VocalChops
            : VocalType.LeadVocal;

        float intensity = 0f;
        var voicedSum = 0f;
        var voicedCount = 0;
        for (var i = 0; i < rms.Count; i++)
        {
            if (!voiced[i]) continue;
            voicedSum += Math.Min(1f, rms[i] / peak);
            voicedCount++;
        }
        if (voicedCount > 0)
            intensity = voicedSum / voicedCount;

        return new VocalActivityResult(
            density,
            first is int f ? (float)(f * windowSeconds) : null,
            last is int l ? (float)((l + 1) * windowSeconds) : null,
            type,
            intensity,
            Curve(rms, peak));
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

    /// <summary>True when a row has been measured (a successful run always writes a curve, even for instrumentals).</summary>
    public static bool IsMeasured(AudioFeaturesEntity features) =>
        !string.IsNullOrEmpty(features.VocalDensityCurveJson) && features.VocalDensityCurveJson != "[]";

    /// <summary>RMS per window over interleaved float PCM, averaged to mono. The final partial window is kept (#303).</summary>
    public static float[] ComputeWindowRms(float[] interleaved, int channels, int sampleRate, double windowSeconds, CancellationToken ct = default)
    {
        if (interleaved.Length == 0 || channels <= 0 || sampleRate <= 0 || windowSeconds <= 0)
            return Array.Empty<float>();

        var windowFrames = Math.Max(1, (int)Math.Round(sampleRate * windowSeconds));
        var frames = interleaved.Length / channels;
        var windowCount = (int)Math.Ceiling(frames / (double)windowFrames);
        var rms = new float[windowCount];

        for (var w = 0; w < windowCount; w++)
        {
            ct.ThrowIfCancellationRequested();
            var startFrame = w * windowFrames;
            var endFrame = Math.Min(frames, startFrame + windowFrames);
            double sumSquares = 0.0;
            for (var f = startFrame; f < endFrame; f++)
            {
                float sample = 0f;
                for (var c = 0; c < channels; c++)
                    sample += interleaved[f * channels + c];
                sample /= channels;
                sumSquares += (double)sample * sample;
            }
            var count = endFrame - startFrame;
            rms[w] = count > 0 ? (float)Math.Sqrt(sumSquares / count) : 0f;
        }

        return rms;
    }

    private static string Curve(IReadOnlyList<float> rms, float peak)
    {
        var parts = new string[rms.Count];
        for (var i = 0; i < rms.Count; i++)
        {
            var normalized = peak > 0f ? Math.Min(1f, rms[i] / peak) : 0f;
            parts[i] = normalized.ToString("0.##", CultureInfo.InvariantCulture);
        }
        return "[" + string.Join(",", parts) + "]";
    }

    private static bool RunVoiced(bool[] voiced, int start, int length)
    {
        for (var i = start; i < start + length; i++)
            if (!voiced[i]) return false;
        return true;
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10.0, db / 20.0);
}
