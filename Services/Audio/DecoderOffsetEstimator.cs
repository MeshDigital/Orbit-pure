using System;
using System.Numerics;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// LF6 (#348): estimates the decoder offset between two renditions of the same audio (e.g. the MP3
/// encoder delay/padding vs the FLAC) by cross-correlating the first seconds of their PCM. The result
/// is the shift (in milliseconds) to apply to the cues/beatgrid when swapping one file for the other.
/// A positive value means the candidate is delayed relative to the reference.
/// </summary>
public static class DecoderOffsetEstimator
{
    /// <param name="reference">Mono PCM of the old file.</param>
    /// <param name="candidate">Mono PCM of the new file.</param>
    /// <param name="sampleRate">Sample rate of both signals.</param>
    /// <param name="maxSearchMs">Largest offset to search for (encoder padding is typically well under 100 ms).</param>
    /// <param name="windowSamples">How many samples of the start of each signal to correlate (default 1 s).</param>
    public static double EstimateOffsetMs(
        float[] reference,
        float[] candidate,
        int sampleRate,
        int maxSearchMs = 100,
        int windowSamples = 44100)
    {
        if (sampleRate <= 0 || maxSearchMs <= 0 || windowSamples <= 0 || reference.Length == 0 || candidate.Length == 0)
            return 0.0;

        var n = Math.Min(windowSamples, Math.Min(reference.Length, candidate.Length));
        if (n < 2)
            return 0.0;

        // Clamp maxShift to at most half the correlation window so tiny 1-sample edge overlaps
        // cannot outscore the true alignment.
        var maxShift = Math.Min(Math.Max(1, (int)((long)sampleRate * maxSearchMs / 1000)), n / 2);

        var refSpan = reference.AsSpan(0, n);
        var candSpan = candidate.AsSpan(0, n);

        var bestShift = 0;
        // Require strictly positive correlation so silence or uncorrelated signals return 0 ms.
        // Search outward from 0 (0, +1, -1, +2, -2, ...) so periodic signals that tie at multiples
        // of their period always prefer the smallest magnitude shift (e.g. 0 ms over -100 ms).
        var bestScore = 0.0;
        const double tieEpsilon = 1e-9;

        var zeroScore = MeanCorrelation(refSpan, candSpan, n, 0);
        if (zeroScore > bestScore + tieEpsilon)
        {
            bestScore = zeroScore;
            bestShift = 0;
        }

        for (var d = 1; d <= maxShift; d++)
        {
            var posScore = MeanCorrelation(refSpan, candSpan, n, d);
            if (posScore > bestScore + tieEpsilon)
            {
                bestScore = posScore;
                bestShift = d;
            }

            var negScore = MeanCorrelation(refSpan, candSpan, n, -d);
            if (negScore > bestScore + tieEpsilon)
            {
                bestScore = negScore;
                bestShift = -d;
            }
        }

        return (double)bestShift / sampleRate * 1000.0;
    }

    private static double MeanCorrelation(ReadOnlySpan<float> refSpan, ReadOnlySpan<float> candSpan, int n, int shift)
    {
        var iStart = shift < 0 ? -shift : 0;
        var iEnd = shift > 0 ? n - shift : n;
        var count = iEnd - iStart;
        if (count <= 0) return double.NegativeInfinity;

        return DotProduct(refSpan.Slice(iStart, count), candSpan.Slice(iStart + shift, count)) / count;
    }

    private static double DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0.0;
        var i = 0;
        var width = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && a.Length >= width)
        {
            var accLo = Vector<double>.Zero;
            var accHi = Vector<double>.Zero;
            var limit = a.Length - width;
            for (; i <= limit; i += width)
            {
                Vector.Widen(new Vector<float>(a.Slice(i, width)), out var aLo, out var aHi);
                Vector.Widen(new Vector<float>(b.Slice(i, width)), out var bLo, out var bHi);
                accLo += aLo * bLo;
                accHi += aHi * bHi;
            }
            sum = Vector.Sum(accLo) + Vector.Sum(accHi);
        }

        for (; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }
}
