using System;

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
        if (sampleRate <= 0 || reference.Length == 0 || candidate.Length == 0)
            return 0.0;

        var n = Math.Min(windowSamples, Math.Min(reference.Length, candidate.Length));
        var maxShift = Math.Max(1, sampleRate * maxSearchMs / 1000);

        var bestShift = 0;
        var bestScore = double.NegativeInfinity;
        for (var shift = -maxShift; shift <= maxShift; shift++)
        {
            double score = 0.0;
            var count = 0;
            for (var i = 0; i < n; i++)
            {
                var j = i + shift;
                if (j < 0 || j >= n) continue;
                score += (double)reference[i] * candidate[j];
                count++;
            }
            if (count == 0) continue;
            var mean = score / count;
            if (mean > bestScore)
            {
                bestScore = mean;
                bestShift = shift;
            }
        }

        return (double)bestShift / sampleRate * 1000.0;
    }
}
