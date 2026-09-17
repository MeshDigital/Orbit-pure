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
    /// A dropout is a sustained period where sub-bass energy falls below <see cref="DropoutThresholdRatio"/>
    /// (20%) of the track mean. A return is when sub-bass energy rises above <see cref="ReturnThresholdRatio"/>
    /// (65%) of mean after a dropout.
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

    /// <summary>
    /// Q values for the two cascaded 2nd-order sections of a proper 4th-order Butterworth lowpass:
    /// Q = 1 / (2*cos(angle)) at pole angles π/8 and 3π/8 — the same angles a textbook 4th-order
    /// Butterworth pole layout uses. Delegates the actual biquad math to NAudio.Dsp.BiQuadFilter
    /// (the same trusted implementation already used a few lines above for the sub-bass bandpass)
    /// instead of a hand-rolled bilinear-transform derivation: that derivation had a sign error in
    /// its a1 coefficient and a spurious extra term in a2, which together made this filter's DC
    /// gain wildly wrong (verified numerically — as low as 0.0001 instead of the required 1.0 at
    /// realistic cutoffs), i.e. it was destroying almost all signal instead of passing it through.
    /// </summary>
    private static readonly float[] ButterworthStageQ =
    {
        (float)(1.0 / (2.0 * Math.Cos(Math.PI / 8))),
        (float)(1.0 / (2.0 * Math.Cos(Math.PI * 3 / 8))),
    };

    private static float[] ApplyButterworthLowPass(float[] signal, int sampleRate, double cutoffHz)
    {
        var output = (float[])signal.Clone();
        foreach (var q in ButterworthStageQ)
        {
            var stage = BiQuadFilter.LowPassFilter(sampleRate, (float)cutoffHz, q);
            for (int i = 0; i < output.Length; i++)
                output[i] = stage.Transform(output[i]);
        }
        return output;
    }
}
