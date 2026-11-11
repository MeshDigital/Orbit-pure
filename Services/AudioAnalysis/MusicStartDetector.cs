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

    /// <summary><c>MusicStartSource</c> values: measured from PCM during analysis, placed by the user,
    /// or estimated from the stored waveform blob by the one-off backfill (display-only).</summary>
    public const string SourceAnalysis = "Analysis";
    public const string SourceUser = "User";
    public const string SourceEstimated = "Estimated";

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

    /// <summary>
    /// Music-start estimate from the duration-scaled tri-band waveform blob (no decode). Energy is the
    /// per-sample loudest of the three bands (a quiet pad lives in one band, so a 3-band mean hid it);
    /// music start is the first sample above the blob's noise floor (2% of peak, at least 3 of 255)
    /// sustained for <see cref="MinSustainSeconds"/>. The blob is display-scaled, not dBFS, so a high
    /// relative threshold treated quiet intros as silence (2026-11-11: <i>Livin' On A Prayer</i> came
    /// out at 16.9 s). When <paramref name="latestPlausibleSeconds"/> (Bar 1 — the beat tracker found
    /// beats there, so there is sound) is given, an estimate later than it is rejected as a miss.
    /// Best-effort, display-only (<see cref="SourceEstimated"/>).
    /// </summary>
    public static double? DetectFromWaveformBlob(byte[] blob, int sampleCount, double duration, double? latestPlausibleSeconds = null)
    {
        if (blob is null || sampleCount <= 0 || blob.Length < sampleCount * 3 || duration <= 0) return null;

        double peak = 0;
        var energy = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            energy[i] = Math.Max(blob[i], Math.Max(blob[sampleCount + i], blob[2 * sampleCount + i]));
            if (energy[i] > peak) peak = energy[i];
        }
        if (peak <= 0) return null;

        double threshold = Math.Max(3.0, peak * 0.02);
        double secondsPerSample = duration / sampleCount;
        int sustainSamples = Math.Max(1, (int)Math.Ceiling(MinSustainSeconds / secondsPerSample));

        int run = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            if (energy[i] >= threshold)
            {
                if (++run >= sustainSamples)
                {
                    double start = (i - sustainSamples + 1) * secondsPerSample;
                    return latestPlausibleSeconds is { } latest && start > latest + MinSustainSeconds ? null : start;
                }
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
