using System;
using SkiaSharp;

namespace SLSKDONET.Services.Timeline;

/// <summary>
/// SkiaSharp-based waveform renderer for timeline clip lanes.
/// Produces an <see cref="SKBitmap"/> for a given RMS profile, supporting
/// zoom/scroll and LO-detail (LOD) downsampling for performance.
/// </summary>
public static class WaveformRenderer
{
    // ── Profile computation ───────────────────────────────────────────────

    /// <summary>
    /// Downsamples a raw RMS byte array (values 0–255, one byte per stored
    /// time window) to exactly <paramref name="targetBins"/> float values
    /// in [0, 1].  If <paramref name="sourceBytes"/> is shorter than
    /// <paramref name="targetBins"/>, the available data is interpolated.
    /// </summary>
    public static float[] ComputeRmsProfile(byte[] sourceBytes, int targetBins)
    {
        if (sourceBytes is null || sourceBytes.Length == 0 || targetBins <= 0)
            return Array.Empty<float>();

        var profile = new float[targetBins];
        double ratio = (double)sourceBytes.Length / targetBins;

        for (int i = 0; i < targetBins; i++)
        {
            int srcStart = (int)(i * ratio);
            int srcEnd = Math.Min((int)((i + 1) * ratio), sourceBytes.Length);
            if (srcEnd <= srcStart) srcEnd = srcStart + 1;

            float sum = 0f;
            int count = 0;
            for (int j = srcStart; j < srcEnd && j < sourceBytes.Length; j++)
            {
                sum += sourceBytes[j];
                count++;
            }
            profile[i] = count > 0 ? sum / (count * 255f) : 0f;
        }

        return profile;
    }

    // ── Rendering ─────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a waveform bitmap using the provided RMS profile.
    /// </summary>
    /// <param name="rmsProfile">Normalised RMS values in [0, 1].</param>
    /// <param name="width">Bitmap width in pixels.</param>
    /// <param name="height">Bitmap height in pixels.</param>
    /// <param name="waveColor">Bar fill colour.</param>
    /// <param name="bgColor">Background fill colour.</param>
    /// <param name="zoom">
    ///   Horizontal zoom factor (1.0 = one profile sample per pixel;
    ///   2.0 = zoom in 2x showing the first half of the profile, etc.).
    /// </param>
    /// <param name="scrollOffset">
    ///   Fractional horizontal scroll offset in [0, 1].  0 = start of clip,
    ///   1 = end of clip (only meaningful when zoom &gt; 1).
    /// </param>
    /// <returns>A new <see cref="SKBitmap"/> owned by the caller.</returns>
    public static SKBitmap Render(
        float[] rmsProfile,
        int width,
        int height,
        SKColor waveColor,
        SKColor bgColor,
        double zoom = 1.0,
        double scrollOffset = 0.0)
    {
        var bmp = new SKBitmap(Math.Max(1, width), Math.Max(1, height));
        using var canvas = new SKCanvas(bmp);

        canvas.Clear(bgColor);

        if (rmsProfile is null || rmsProfile.Length == 0 || width <= 0 || height <= 0)
            return bmp;

        zoom = Math.Max(1.0, zoom);
        scrollOffset = Math.Clamp(scrollOffset, 0.0, 1.0);

        // Number of source samples visible at current zoom
        int visibleSamples = (int)Math.Ceiling(rmsProfile.Length / zoom);
        visibleSamples = Math.Max(1, Math.Min(visibleSamples, rmsProfile.Length));

        // Start sample based on scroll
        int maxStart = rmsProfile.Length - visibleSamples;
        int startSample = (int)(scrollOffset * maxStart);
        startSample = Math.Clamp(startSample, 0, maxStart);

        using var paint = new SKPaint { IsAntialias = false, Color = waveColor };

        float half = height / 2f;
        float barWidth = (float)width / visibleSamples;
        barWidth = Math.Max(barWidth, 1f);

        for (int i = 0; i < visibleSamples; i++)
        {
            int srcIdx = startSample + i;
            if (srcIdx >= rmsProfile.Length) break;

            float rms = rmsProfile[srcIdx];
            float barHalf = rms * half * 0.9f; // leave 10% headroom

            float x = i * barWidth;
            float top = half - barHalf;
            float bottom = half + barHalf;

            // Sub-pixel rect per bar
            canvas.DrawRect(x, top, barWidth, bottom - top, paint);
        }

        return bmp;
    }

    /// <summary>
    /// Renders a waveform directly from a <see cref="SLSKDONET.Models.WaveformAnalysisData"/>
    /// instance.  Applies LOD: when <paramref name="width"/> / visible samples ratio is
    /// below one pixel per source sample, the profile is automatically resampled.
    /// </summary>
    public static SKBitmap RenderFromWaveformData(
        SLSKDONET.Models.WaveformAnalysisData data,
        int width,
        int height,
        SKColor waveColor,
        SKColor bgColor,
        double zoom = 1.0,
        double scrollOffset = 0.0)
    {
        // Choose the highest-resolution band available; fall back through mid → peak
        byte[] source = data.RmsData?.Length > 0 ? data.RmsData
            : data.MidData?.Length > 0 ? data.MidData
            : data.PeakData;

        // LOD: target at most 4× width worth of bins (enough for smooth zoom-in)
        int targetBins = Math.Min(source?.Length ?? 0, width * 4);
        if (targetBins <= 0) targetBins = width;

        float[] profile = ComputeRmsProfile(source ?? Array.Empty<byte>(), targetBins);
        return Render(profile, width, height, waveColor, bgColor, zoom, scrollOffset);
    }
}
