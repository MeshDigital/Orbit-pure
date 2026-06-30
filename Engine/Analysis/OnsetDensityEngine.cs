using System;
using System.Collections.Generic;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Computes an onset density curve from an absolute beat grid.
/// Onset density = number of beats per bar window, normalized to [0, 1].
///
/// High density sections = active musical content (builds, drops).
/// Low density sections  = breakdowns, intros, outros.
///
/// This gives the CueForge phrase map a continuous signal for coloring
/// track sections and helps the IntentClassifier confirm structural boundaries.
/// </summary>
public sealed class OnsetDensityEngine
{
    private const double DefaultBarWindowBeats = 8.0; // count beats in an 8-beat window

    /// <summary>
    /// Computes per-second onset density from an absolute beat grid.
    /// Returns one density value per second of track duration.
    /// </summary>
    /// <param name="beatTimestamps">Absolute beat positions in seconds (from Essentia BeatTrackerMultiFeature).</param>
    /// <param name="durationSeconds">Total track duration.</param>
    /// <param name="windowSeconds">Smoothing window size in seconds (default 4s).</param>
    public float[] ComputeOnsetDensityCurve(
        IReadOnlyList<double> beatTimestamps,
        double durationSeconds,
        double windowSeconds = 4.0)
    {
        if (beatTimestamps == null || beatTimestamps.Count == 0 || durationSeconds <= 0)
            return Array.Empty<float>();

        int numBins = (int)Math.Ceiling(durationSeconds);
        var raw = new float[numBins];

        // Count how many beats fall in each 1-second bin
        foreach (var ts in beatTimestamps)
        {
            int bin = (int)Math.Floor(ts);
            if (bin >= 0 && bin < numBins) raw[bin]++;
        }

        // Smooth with boxcar window
        int halfWindow = Math.Max(1, (int)(windowSeconds / 2.0));
        var smoothed = new float[numBins];
        for (int i = 0; i < numBins; i++)
        {
            int start = Math.Max(0, i - halfWindow);
            int end = Math.Min(numBins - 1, i + halfWindow);
            float sum = 0f;
            for (int j = start; j <= end; j++) sum += raw[j];
            smoothed[i] = sum / (end - start + 1);
        }

        // Normalize
        float max = 0f;
        foreach (var v in smoothed) if (v > max) max = v;
        if (max > 1e-6f)
            for (int i = 0; i < numBins; i++) smoothed[i] /= max;

        return smoothed;
    }

    /// <summary>
    /// Detects sustained low-density regions (breakdowns) and sudden high-density
    /// returns (drops) from a pre-computed density curve.
    /// Thresholds tuned for DnB / EDM structures.
    /// </summary>
    public (List<double> BreakdownStarts, List<double> DropHits) DetectBreakdownsAndDrops(
        float[] densityCurve,
        double lowThreshold = 0.30,
        double highThreshold = 0.65,
        double minBreakdownSeconds = 4.0)
    {
        var breakdownStarts = new List<double>();
        var dropHits = new List<double>();

        if (densityCurve == null || densityCurve.Length == 0)
            return (breakdownStarts, dropHits);

        bool inBreakdown = false;
        int breakdownStartBin = -1;
        int minBreakdownBins = (int)minBreakdownSeconds;

        for (int i = 1; i < densityCurve.Length; i++)
        {
            if (!inBreakdown && densityCurve[i] < lowThreshold)
            {
                inBreakdown = true;
                breakdownStartBin = i;
            }
            else if (inBreakdown)
            {
                if (densityCurve[i] >= highThreshold)
                {
                    // Drop hit: density returned above threshold after a real breakdown
                    int breakdownLen = i - breakdownStartBin;
                    if (breakdownLen >= minBreakdownBins)
                    {
                        breakdownStarts.Add(breakdownStartBin);
                        dropHits.Add(i);
                    }
                    inBreakdown = false;
                    breakdownStartBin = -1;
                }
                else if (densityCurve[i] >= lowThreshold * 1.5)
                {
                    // Density drifted back up partially — not a clean drop, cancel
                    inBreakdown = false;
                    breakdownStartBin = -1;
                }
            }
        }

        return (breakdownStarts, dropHits);
    }
}
