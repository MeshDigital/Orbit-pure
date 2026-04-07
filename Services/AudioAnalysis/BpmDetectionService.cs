using System;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Extracts BPM and confidence from Essentia JSON output, applies
/// median-smoothing over the BPM histogram, and populates
/// <see cref="AudioFeaturesEntity"/> fields.
/// </summary>
public sealed class BpmDetectionService
{
    // Any BPM below this threshold is considered a potential half-time artefact.
    private const float MinReasonableBpm = 60f;
    // BPM above this is a potential double-time artefact.
    private const float MaxReasonableBpm = 200f;

    /// <summary>
    /// Extracts BPM from <paramref name="essentiaOutput"/> using histogram median smoothing
    /// and writes the result into <paramref name="target"/>.
    /// </summary>
    public void Detect(EssentiaOutput essentiaOutput, AudioFeaturesEntity target)
    {
        ArgumentNullException.ThrowIfNull(essentiaOutput);
        ArgumentNullException.ThrowIfNull(target);

        var rhythm = essentiaOutput.Rhythm;
        if (rhythm == null) return;

        float rawBpm = rhythm.Bpm;
        float confidence = rhythm.BpmConfidence;

        // ── histogram median smoothing ────────────────────────────────────
        if (rhythm.BpmHistogram is { Length: > 0 } histogram)
        {
            float smoothed = HistogramMedian(histogram);
            // Only use the histogram median if it is not an obvious octave error
            // compared with the direct BPM estimate.
            if (IsOctaveConsistent(rawBpm, smoothed))
            {
                rawBpm = smoothed;
            }
            // Confidence penalty when the histogram is flat (unstable tempo)
            float histMax = histogram.Max();
            float histMean = histogram.Average();
            float peakedness = histMax > 0f ? histMean / histMax : 0f;
            confidence *= (1f - 0.3f * peakedness); // reduce conf for broad histograms
        }

        // ── half/double time correction ───────────────────────────────────
        rawBpm = NormaliseToRange(rawBpm);

        target.Bpm = MathF.Round(rawBpm, 2);
        target.BpmConfidence = Math.Clamp(confidence, 0f, 1f);

        // BPM stability from histogram peak width
        if (rhythm.BpmHistogram is { Length: > 0 } h2)
            target.BpmStability = ComputeStability(h2);
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    /// <summary>
    /// Computes the weighted median BPM from an Essentia BPM histogram.
    /// Each bin index corresponds to 1 BPM; the value is the weight.
    /// </summary>
    private static float HistogramMedian(float[] histogram)
    {
        float total = histogram.Sum();
        if (total <= 0f) return 0f;

        float cumulative = 0f;
        float half = total / 2f;
        for (int i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= half)
                return i + 1f; // bin index is 0-based; BPM starts at 1
        }
        return histogram.Length;
    }

    /// <summary>
    /// Returns true if two BPM values are within the same octave (neither is
    /// an obvious 2×/0.5× artefact).
    /// </summary>
    private static bool IsOctaveConsistent(float a, float b)
    {
        if (a <= 0f || b <= 0f) return false;
        float ratio = a / b;
        // Accept 0.9–1.1 as "same", 1.8–2.2 as double-time (reject), etc.
        return ratio is > 0.9f and < 1.1f;
    }

    /// <summary>Clamps BPM to [60, 200] recovering half/double-time errors.</summary>
    private static float NormaliseToRange(float bpm)
    {
        if (bpm <= 0f) return bpm;
        while (bpm < MinReasonableBpm) bpm *= 2f;
        while (bpm > MaxReasonableBpm) bpm /= 2f;
        return bpm;
    }

    /// <summary>
    /// Measures how concentrated the histogram peak is.
    /// 1.0 = perfect single-peak; 0.0 = completely flat.
    /// </summary>
    private static float ComputeStability(float[] histogram)
    {
        float max = histogram.Max();
        float sum = histogram.Sum();
        if (sum <= 0f) return 1f;
        return Math.Clamp(max / sum * histogram.Length, 0f, 1f);
    }
}
