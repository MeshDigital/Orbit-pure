using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Dsp;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Isolates the true sub-bass band (30–100 Hz, a bandpass) and detects sub-bass dropouts/returns —
/// the primary DnB drop signature.
///
/// Why this matters for DnB and EDM:
///   The most reliable drop signature in DnB is a "bass dropout" — the sub-bass
///   disappears for 8–32 bars (the breakdown) then returns with extreme energy
///   at the drop. This pattern is acoustically more stable than spectral flux peaks
///   because it persists even when producers layer melodic content over the breakdown.
///
/// Previously this used a single low-pass at 120 Hz, which lumps true sub-bass together with
/// kick fundamental/punch (roughly 90-250 Hz) — content that often stays present through a DnB
/// breakdown even when the sub-bass genuinely drops out, diluting the very signal this engine
/// depends on. A proper bandpass (high-pass 30 Hz to reject DC/rumble, low-pass 100 Hz to
/// substantially exclude kick punch) isolates the sub shelf much more cleanly.
///
/// Signal pipeline:
///   Raw PCM → 30-100 Hz bandpass (NAudio.Dsp.BiQuadFilter, cascaded HP+LP) → RMS energy per
///   window → detect sustained low regions (dropout) → detect return spike
/// </summary>
public sealed class SubBassDropoutEngine
{
    private const double LowCutHz = 30.0;
    private const double HighCutHz = 100.0;
    private const double DropoutThresholdRatio = 0.20; // below 20% of track-average = dropout
    private const double ReturnThresholdRatio = 0.65;  // above 65% of track-average after dropout = return
    private const double MinDropoutSeconds = 1.75;     // ~1 bar at typical DnB tempos
    private const double EnergyWindowSeconds = 0.25;

    /// <summary>
    /// Isolates the true sub-bass band (30-100 Hz bandpass) and computes per-window RMS energy.
    /// </summary>
    public float[] ComputeSubBassEnergyCurve(float[] monoSignal, int sampleRate)
    {
        if (monoSignal == null || monoSignal.Length == 0) return Array.Empty<float>();

        var filtered = ApplySubBassBandpass(monoSignal, sampleRate);
        return ComputeWindowedRms(filtered, sampleRate);
    }

    /// <summary>
    /// Isolates an arbitrary low-pass band and computes per-window RMS energy. Generalized
    /// from the sub-bass-only (120 Hz) version so other detectors needing a different band
    /// (e.g. a wider mid-low band covering kick fundamental + punch) can reuse the same
    /// Butterworth filter + windowed-RMS pipeline instead of duplicating it.
    /// </summary>
    public float[] ComputeBandEnergyCurve(float[] monoSignal, int sampleRate, double cutoffHz)
    {
        if (monoSignal == null || monoSignal.Length == 0) return Array.Empty<float>();

        // Apply 4th-order Butterworth LP filter cascaded as two 2nd-order sections
        var filtered = ApplyButterworthLowPass(monoSignal, sampleRate, cutoffHz);

        // Compute RMS per window
        int windowSamples = (int)(EnergyWindowSeconds * sampleRate);
        windowSamples = Math.Max(1, windowSamples);
        int numWindows = monoSignal.Length / windowSamples;

        var energyCurve = new float[numWindows];
        for (int i = 0; i < numWindows; i++)
        {
            int start = i * windowSamples;
            double sumSq = 0.0;
            for (int j = start; j < start + windowSamples && j < filtered.Length; j++)
                sumSq += filtered[j] * (double)filtered[j];
            energyCurve[i] = (float)Math.Sqrt(sumSq / windowSamples);
        }

        return energyCurve;
    }

    /// <summary>Window size (seconds) used by <see cref="ComputeBandEnergyCurve"/> — exposed so
    /// other detectors sharing this engine's curve can convert indices to timestamps correctly.</summary>
    public double WindowSeconds => EnergyWindowSeconds;

    /// <summary>
    /// Detects sub-bass dropout and return events — the primary DnB drop signature.
    /// A dropout is a sustained period where sub-bass energy falls below 25% of the track mean.
    /// A return is when sub-bass energy rises above 60% of mean after a dropout.
    /// </summary>
    public (List<double> DropoutStarts, List<double> ReturnTimestamps) DetectDropoutEvents(
        float[] subBassEnergyCurve)
    {
        var dropoutStarts = new List<double>();
        var returnTimestamps = new List<double>();

        if (subBassEnergyCurve == null || subBassEnergyCurve.Length == 0)
            return (dropoutStarts, returnTimestamps);

        float trackMean = subBassEnergyCurve.Average();
        if (trackMean < 1e-8f) return (dropoutStarts, returnTimestamps);

        float dropoutThreshold = trackMean * (float)DropoutThresholdRatio;
        float returnThreshold = trackMean * (float)ReturnThresholdRatio;
        int minDropoutWindows = (int)Math.Ceiling(MinDropoutSeconds / EnergyWindowSeconds);

        bool inDropout = false;
        int dropoutStartWindow = -1;
        int consecutiveLow = 0;

        for (int i = 0; i < subBassEnergyCurve.Length; i++)
        {
            double ts = i * EnergyWindowSeconds;

            if (!inDropout)
            {
                if (subBassEnergyCurve[i] < dropoutThreshold)
                {
                    consecutiveLow++;
                    if (consecutiveLow >= minDropoutWindows && dropoutStartWindow < 0)
                        dropoutStartWindow = i - consecutiveLow + 1;
                }
                else
                {
                    consecutiveLow = 0;
                    dropoutStartWindow = -1;
                }

                if (dropoutStartWindow >= 0 && consecutiveLow >= minDropoutWindows)
                {
                    inDropout = true;
                    dropoutStarts.Add(dropoutStartWindow * EnergyWindowSeconds);
                }
            }
            else
            {
                // In dropout — watch for bass return
                if (subBassEnergyCurve[i] >= returnThreshold)
                {
                    returnTimestamps.Add(ts);
                    inDropout = false;
                    consecutiveLow = 0;
                    dropoutStartWindow = -1;
                }
            }
        }

        return (dropoutStarts, returnTimestamps);
    }

    /// <summary>
    /// Given a set of candidate beat timestamps (typically the first few ticks from a beat
    /// tracker), returns the index of whichever candidate has the strongest sub-bass/kick energy
    /// in a short window around it — the DJ-genre-standard assumption that the true downbeat
    /// (bar 1, beat 1) carries the most low-end emphasis. Beat trackers report beat times with no
    /// bar-phase information, so "the first detected tick" is not reliably the actual downbeat;
    /// this gives a real signal to pick among the first few candidates instead of blindly trusting
    /// index 0.
    /// </summary>
    public static int FindStrongestBeatIndex(
        float[] monoSignal, int sampleRate, IReadOnlyList<double> beatTimestamps, int candidateCount = 4)
    {
        if (monoSignal == null || monoSignal.Length == 0 || beatTimestamps == null || beatTimestamps.Count == 0 || sampleRate <= 0)
            return 0;

        int limit = Math.Min(candidateCount, beatTimestamps.Count);
        int halfWindowSamples = Math.Max(1, (int)Math.Round(0.050 * sampleRate)); // +/- 50ms

        int bestIndex = 0;
        double bestRms = -1.0;

        for (int i = 0; i < limit; i++)
        {
            int center = (int)Math.Round(beatTimestamps[i] * sampleRate);
            int start = Math.Max(0, center - halfWindowSamples);
            int end = Math.Min(monoSignal.Length, center + halfWindowSamples);
            int count = end - start;
            if (count <= 0) continue;

            var segment = new float[count];
            Array.Copy(monoSignal, start, segment, 0, count);
            var filtered = ApplySubBassBandpass(segment, sampleRate);

            double sumSq = 0.0;
            foreach (var s in filtered) sumSq += s * (double)s;
            double rms = Math.Sqrt(sumSq / count);

            if (rms > bestRms)
            {
                bestRms = rms;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    // ── True sub-bass bandpass (NAudio.Dsp.BiQuadFilter, cascaded high-pass + low-pass) ─────

    private static float[] ApplySubBassBandpass(float[] signal, int sampleRate)
    {
        var hp = BiQuadFilter.HighPassFilter(sampleRate, (float)LowCutHz, 0.7071f);
        var lp = BiQuadFilter.LowPassFilter(sampleRate, (float)HighCutHz, 0.7071f);

        var output = new float[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            output[i] = lp.Transform(hp.Transform(signal[i]));
        }
        return output;
    }

    private static float[] ComputeWindowedRms(float[] filtered, int sampleRate)
    {
        int windowSamples = Math.Max(1, (int)(EnergyWindowSeconds * sampleRate));
        int numWindows = filtered.Length / windowSamples;

        var energyCurve = new float[numWindows];
        for (int i = 0; i < numWindows; i++)
        {
            int start = i * windowSamples;
            double sumSq = 0.0;
            for (int j = start; j < start + windowSamples && j < filtered.Length; j++)
                sumSq += filtered[j] * (double)filtered[j];
            energyCurve[i] = (float)Math.Sqrt(sumSq / windowSamples);
        }

        return energyCurve;
    }

    // ── 4th-order Butterworth LP filter (cascaded biquads) — still used by ComputeBandEnergyCurve,
    // which StructuralStrippingEngine relies on for its own, wider (250 Hz) House/Techno band ─────

    private static float[] ApplyButterworthLowPass(float[] signal, int sampleRate, double cutoffHz)
    {
        // Compute normalized cutoff (0..1, where 1 = Nyquist)
        double wc = 2.0 * Math.PI * cutoffHz / sampleRate;

        // Pre-warp for bilinear transform
        double wcAnalog = 2.0 * Math.Tan(wc / 2.0);

        // 4th order = two cascaded 2nd-order sections
        // Pole angles for 4th-order Butterworth: π/8, 3π/8 relative to unit circle
        double[] angles = { Math.PI * 3 / 8, Math.PI / 8 };

        var output = (float[])signal.Clone();
        foreach (double angle in angles)
        {
            // Analog prototype poles
            double realPole = -Math.Sin(angle) * wcAnalog;
            double imagPole = Math.Cos(angle) * wcAnalog;

            // Bilinear transform to digital coefficients
            double d = (2.0 - realPole) * (2.0 - realPole) + imagPole * imagPole;
            if (d < 1e-12) continue;

            double b0 = wcAnalog * wcAnalog / d;
            double b1 = 2.0 * b0;
            double b2 = b0;
            double a1 = 2.0 * (4.0 - wcAnalog * wcAnalog) / d;
            double a2 = ((2.0 + realPole) * (2.0 + realPole) + imagPole * imagPole - 4.0 * imagPole * imagPole) / d;

            output = ApplyBiquad(output, b0, b1, b2, -a1, -a2);
        }

        return output;
    }

    private static float[] ApplyBiquad(float[] signal, double b0, double b1, double b2, double a1, double a2)
    {
        var output = new float[signal.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;

        for (int i = 0; i < signal.Length; i++)
        {
            double x0 = signal[i];
            double y0 = b0 * x0 + b1 * x1 + b2 * x2 + a1 * y1 + a2 * y2;
            output[i] = (float)Math.Clamp(y0, -1.0, 1.0);
            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
        }

        return output;
    }
}
