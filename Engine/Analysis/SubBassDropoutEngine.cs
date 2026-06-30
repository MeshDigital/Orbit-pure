using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Isolates the sub-bass frequency band (20–120 Hz) using a 4th-order Butterworth
/// low-pass IIR filter, then detects sub-bass dropouts and returns.
///
/// Why this matters for DnB and EDM:
///   The most reliable drop signature in DnB is a "bass dropout" — the sub-bass
///   disappears for 8–32 bars (the breakdown) then returns with extreme energy
///   at the drop. This pattern is acoustically more stable than spectral flux peaks
///   because it persists even when producers layer melodic content over the breakdown.
///
/// Signal pipeline:
///   Raw PCM → 4th-order Butterworth LPF @120Hz → RMS energy per 0.5s window
///   → detect sustained low regions (dropout) → detect return spike
/// </summary>
public sealed class SubBassDropoutEngine
{
    private const double CutoffHz = 120.0;
    private const double DropoutThresholdRatio = 0.25; // below 25% of track-average = dropout
    private const double ReturnThresholdRatio = 0.60;  // above 60% of track-average after dropout = return
    private const double MinDropoutSeconds = 2.0;
    private const double EnergyWindowSeconds = 0.5;

    /// <summary>
    /// Isolates sub-bass band and computes per-window RMS energy.
    /// </summary>
    public float[] ComputeSubBassEnergyCurve(float[] monoSignal, int sampleRate)
    {
        if (monoSignal == null || monoSignal.Length == 0) return Array.Empty<float>();

        // Apply 4th-order Butterworth LP filter cascaded as two 2nd-order sections
        var filtered = ApplyButterworthLowPass(monoSignal, sampleRate, CutoffHz);

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

    // ── 4th-order Butterworth LP filter (cascaded biquads) ──────────────────

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
