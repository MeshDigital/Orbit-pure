using System;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>A single display column sampled from the shared low/mid/high waveform arrays.</summary>
internal readonly record struct WaveformColumn(byte Low, byte Mid, byte High);

/// <summary>
/// Maps waveform samples to display columns. Downsampling uses per-band peaks so a short kick,
/// bass return or drop cannot disappear through averaging; sub-sample zoom interpolates smoothly.
/// </summary>
internal static class WaveformColumnSampler
{
    public static WaveformColumn Sample(
        byte[] low,
        byte[] mid,
        byte[] high,
        double startIndex,
        double endIndex)
    {
        if (low.Length == 0 || mid.Length != low.Length || high.Length != low.Length)
            return default;

        int last = low.Length - 1;
        double exact = Math.Clamp(startIndex, 0, last);
        int first = (int)Math.Clamp(Math.Floor(startIndex), 0, last);
        int end = (int)Math.Clamp(Math.Ceiling(endIndex), first + 1, low.Length);

        if (end - first <= 1)
        {
            int next = Math.Min(first + 1, last);
            double fraction = exact - first;
            return new WaveformColumn(
                Interpolate(low, first, next, fraction),
                Interpolate(mid, first, next, fraction),
                Interpolate(high, first, next, fraction));
        }

        byte lowPeak = 0, midPeak = 0, highPeak = 0;
        for (int i = first; i < end; i++)
        {
            lowPeak = Math.Max(lowPeak, low[i]);
            midPeak = Math.Max(midPeak, mid[i]);
            highPeak = Math.Max(highPeak, high[i]);
        }
        return new WaveformColumn(lowPeak, midPeak, highPeak);
    }

    private static byte Interpolate(byte[] values, int first, int next, double fraction) =>
        (byte)Math.Clamp((int)Math.Round(values[first] + (values[next] - values[first]) * fraction), 0, 255);
}
