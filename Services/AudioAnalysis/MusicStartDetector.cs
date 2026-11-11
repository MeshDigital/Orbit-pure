using System;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Finds "music start" — the first point after which short-term RMS stays above
/// <c>max(-50 dBFS, integrated loudness - 35 LU)</c> for at least 200 ms. Pure DSP over a mono PCM
/// buffer; used during analysis and (via the waveform blob) by the backfill. The audio file is never
/// trimmed — this is a marker only.
/// </summary>
public static class MusicStartDetector
{
    public const double DefaultThresholdDbFs = -50.0;
    public const double MinSustainSeconds = 0.2;

    /// <summary>The RMS threshold for "music present", from the track's integrated loudness.</summary>
    public static double ThresholdDbFs(double? integratedLoudnessLufs)
    {
        double reference = integratedLoudnessLufs ?? -100.0;
        return Math.Max(DefaultThresholdDbFs, reference - 35.0);
    }

    /// <summary>
    /// Returns the music-start time in seconds, or null when the signal never sustains above the
    /// threshold. A 100 ms RMS window is hopped at 10 ms; the first window of a run of
    /// <see cref="MinSustainSeconds"/> worth of above-threshold windows is the answer.
    /// </summary>
    public static double? Detect(float[] mono, int sampleRate, double thresholdDbFs = DefaultThresholdDbFs)
    {
        if (mono is null || mono.Length == 0 || sampleRate <= 0) return null;

        double thresholdLinear = Math.Pow(10.0, thresholdDbFs / 20.0);
        int window = Math.Max(1, sampleRate / 100);   // 10 ms short-term RMS
        int hop = window;                             // non-overlapping
        int sustainFrames = Math.Max(1, (int)Math.Ceiling(MinSustainSeconds * sampleRate / hop));

        int frameCount = mono.Length >= window ? 1 + (mono.Length - window) / hop : 0;
        int run = 0;
        for (int f = 0; f < frameCount; f++)
        {
            int start = f * hop;
            double sumSq = 0;
            for (int i = 0; i < window; i++)
            {
                double v = mono[start + i];
                sumSq += v * v;
            }

            bool above = Math.Sqrt(sumSq / window) >= thresholdLinear;
            if (above)
            {
                if (++run >= sustainFrames)
                    return (f - sustainFrames + 1) * hop / (double)sampleRate;
            }
            else
            {
                run = 0;
            }
        }

        return null;
    }
}

/// <summary>
/// The Intro/load-cue anchor rule (issue #224): use music start when it precedes
/// <paramref name="anchorSeconds"/> (normally Bar 1) by more than one beat; otherwise keep the anchor.
/// </summary>
public static class MusicStartIntroAnchor
{
    public static double Resolve(double? musicStartSeconds, double anchorSeconds, double bpm)
    {
        if (musicStartSeconds is not { } ms) return anchorSeconds;
        double beat = bpm > 0 ? 60.0 / bpm : 0.5;
        if (ms >= 0 && ms < anchorSeconds && (anchorSeconds - ms) > beat)
            return ms;
        return anchorSeconds;
    }
}
