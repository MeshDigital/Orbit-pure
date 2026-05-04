using System;
using System.Collections.Generic;
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

    // ── Task 4.1: RGB tri-band renderer ───────────────────────────────────

    /// <summary>
    /// Renders a three-band RGB waveform from a <see cref="SLSKDONET.Models.WaveformAnalysisData"/>.
    /// Bands are alpha-composited on top of each other:
    ///   Bass (low)  → Red (#FF4444)
    ///   Mids        → Green (#44FF88)
    ///   Highs       → Blue (#44AAFF)
    /// </summary>
    public static SKBitmap RenderRgb(
        SLSKDONET.Models.WaveformAnalysisData data,
        int width,
        int height,
        SKColor bgColor,
        double zoom = 1.0,
        double scrollOffset = 0.0)
    {
        var bmp = new SKBitmap(Math.Max(1, width), Math.Max(1, height));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(bgColor);

        if (data is null || width <= 0 || height <= 0) return bmp;

        // Bass = red, alpha 200 so mids/highs show through
        if (data.LowData?.Length > 0)
            DrawBand(canvas, ComputeRmsProfile(data.LowData, width * 2), width, height,
                     new SKColor(255, 68,  68,  200), zoom, scrollOffset);

        if (data.MidData?.Length > 0)
            DrawBand(canvas, ComputeRmsProfile(data.MidData, width * 2), width, height,
                     new SKColor(68,  255, 136, 180), zoom, scrollOffset);

        if (data.HighData?.Length > 0)
            DrawBand(canvas, ComputeRmsProfile(data.HighData, width * 2), width, height,
                     new SKColor(68,  170, 255, 160), zoom, scrollOffset);

        return bmp;
    }

    private static void DrawBand(
        SKCanvas canvas, float[] profile, int width, int height,
        SKColor color, double zoom, double scrollOffset)
    {
        if (profile.Length == 0) return;

        zoom = Math.Max(1.0, zoom);
        scrollOffset = Math.Clamp(scrollOffset, 0.0, 1.0);

        int visibleSamples = Math.Max(1, Math.Min((int)Math.Ceiling(profile.Length / zoom), profile.Length));
        int maxStart = profile.Length - visibleSamples;
        int startSample = Math.Clamp((int)(scrollOffset * maxStart), 0, maxStart);

        using var paint = new SKPaint
        {
            IsAntialias = false,
            Color       = color,
            BlendMode   = SKBlendMode.Plus   // additive blend: bands overlap cleanly
        };

        float half     = height / 2f;
        float barWidth = Math.Max(1f, (float)width / visibleSamples);

        for (int i = 0; i < visibleSamples; i++)
        {
            int srcIdx = startSample + i;
            if (srcIdx >= profile.Length) break;

            float barHalf = profile[srcIdx] * half * 0.9f;
            float x       = i * barWidth;
            float top     = half - barHalf;

            canvas.DrawRect(x, top, barWidth, barHalf * 2f, paint);
        }
    }

    // ── Task 4.2: Beat-grid overlay ───────────────────────────────────────

    /// <summary>
    /// Draws vertical beat-grid lines over an existing <see cref="SKBitmap"/> (in-place).
    /// </summary>
    /// <param name="bmp">Target bitmap (must already be rendered).</param>
    /// <param name="beatPositionsSeconds">Absolute beat timestamps in seconds.</param>
    /// <param name="trackDurationSeconds">Full track duration; used for x-mapping.</param>
    /// <param name="zoom">Must match the zoom value used when rendering the waveform.</param>
    /// <param name="scrollOffset">Must match the scrollOffset used when rendering.</param>
    public static void OverlayBeatGrid(
        SKBitmap bmp,
        IReadOnlyList<double> beatPositionsSeconds,
        double trackDurationSeconds,
        double zoom = 1.0,
        double scrollOffset = 0.0)
    {
        if (bmp is null || beatPositionsSeconds is null || trackDurationSeconds <= 0) return;

        using var canvas = new SKCanvas(bmp);
        zoom = Math.Max(1.0, zoom);

        // Visible time window
        double windowDuration = trackDurationSeconds / zoom;
        double windowStart    = scrollOffset * (trackDurationSeconds - windowDuration);

        using var barPaint = new SKPaint
        {
            Color       = new SKColor(255, 255, 255, 60),
            StrokeWidth = 1f,
            IsAntialias = false
        };
        using var downbeatPaint = new SKPaint
        {
            Color       = new SKColor(255, 255, 255, 120),
            StrokeWidth = 1.5f,
            IsAntialias = false
        };

        for (int i = 0; i < beatPositionsSeconds.Count; i++)
        {
            double t = beatPositionsSeconds[i];
            if (t < windowStart || t > windowStart + windowDuration) continue;

            float x = (float)((t - windowStart) / windowDuration * bmp.Width);
            // Every 4th beat = bar start → brighter line
            var paint = i % 4 == 0 ? downbeatPaint : barPaint;
            canvas.DrawLine(x, 0, x, bmp.Height, paint);
        }
    }

    // ── Task 4.2b: Phrase section overlay ─────────────────────────────────

    /// <summary>
    /// Draws 16-bar gridlines and phrase-colored section backgrounds over a waveform bitmap.
    /// </summary>
    public static void OverlayPhraseSections(
        SKBitmap bmp,
        IReadOnlyList<SLSKDONET.Models.PhraseSegment> phrases,
        double trackDurationSeconds,
        float bpm,
        double zoom = 1.0,
        double scrollOffset = 0.0)
    {
        if (bmp is null || phrases is null || phrases.Count == 0 || trackDurationSeconds <= 0) return;

        using var canvas = new SKCanvas(bmp);
        zoom = Math.Max(1.0, zoom);

        double windowDuration = trackDurationSeconds / zoom;
        double windowStart = scrollOffset * (trackDurationSeconds - windowDuration);

        if (bpm > 0)
        {
            double phraseSeconds = (16d * 4d * 60d) / bpm;
            using var gridPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 42),
                StrokeWidth = 1f,
                IsAntialias = false
            };

            for (double t = 0; t <= trackDurationSeconds; t += phraseSeconds)
            {
                if (t < windowStart || t > windowStart + windowDuration) continue;
                float x = (float)((t - windowStart) / windowDuration * bmp.Width);
                canvas.DrawLine(x, 0, x, bmp.Height, gridPaint);
            }
        }

        foreach (var phrase in phrases)
        {
            double start = phrase.Start;
            double end = phrase.Start + phrase.Duration;
            if (end < windowStart || start > windowStart + windowDuration) continue;

            float x1 = (float)((start - windowStart) / windowDuration * bmp.Width);
            float x2 = (float)((end - windowStart) / windowDuration * bmp.Width);
            if (x2 <= x1) continue;

            var color = ResolvePhraseColor(phrase.Label);
            using var fillPaint = new SKPaint { Color = color.WithAlpha(38), IsAntialias = false };
            canvas.DrawRect(x1, 0, x2 - x1, bmp.Height, fillPaint);
        }
    }

    private static SKColor ResolvePhraseColor(string? label)
    {
        var text = label ?? string.Empty;
        if (text.Contains("intro", StringComparison.OrdinalIgnoreCase)) return SKColor.Parse("#1E3A5F");
        if (text.Contains("build", StringComparison.OrdinalIgnoreCase) || text.Contains("riser", StringComparison.OrdinalIgnoreCase)) return SKColor.Parse("#FFB347");
        if (text.Contains("drop", StringComparison.OrdinalIgnoreCase) || text.Contains("chorus", StringComparison.OrdinalIgnoreCase)) return SKColor.Parse("#DC143C");
        if (text.Contains("break", StringComparison.OrdinalIgnoreCase) || text.Contains("bridge", StringComparison.OrdinalIgnoreCase)) return SKColor.Parse("#6A0DAD");
        if (text.Contains("outro", StringComparison.OrdinalIgnoreCase)) return SKColor.Parse("#708090");
        return SKColor.Parse("#708090");
    }

    // ── Task 4.3: Cue-point markers ───────────────────────────────────────

    /// <summary>
    /// Draws coloured cue-point markers (triangle + vertical line) over the bitmap.
    /// </summary>
    public static void OverlayCueMarkers(
        SKBitmap bmp,
        IReadOnlyList<(double TimeSeconds, SKColor Color, string? Label)> cues,
        double trackDurationSeconds,
        double zoom = 1.0,
        double scrollOffset = 0.0)
    {
        if (bmp is null || cues is null || trackDurationSeconds <= 0) return;

        using var canvas = new SKCanvas(bmp);
        zoom = Math.Max(1.0, zoom);

        double windowDuration = trackDurationSeconds / zoom;
        double windowStart    = scrollOffset * (trackDurationSeconds - windowDuration);

        foreach (var (time, color, label) in cues)
        {
            if (time < windowStart || time > windowStart + windowDuration) continue;

            float x = (float)((time - windowStart) / windowDuration * bmp.Width);

            using var linePaint  = new SKPaint { Color = color, StrokeWidth = 1.5f };
            using var fillPaint  = new SKPaint { Color = color };
            using var textPaint  = new SKPaint
            {
                Color    = color,
                TextSize = 10f,
                IsAntialias = true
            };

            // Vertical line
            canvas.DrawLine(x, 0, x, bmp.Height, linePaint);

            // Triangle marker at top
            var path = new SKPath();
            path.MoveTo(x, 0);
            path.LineTo(x - 6, 12);
            path.LineTo(x + 6, 12);
            path.Close();
            canvas.DrawPath(path, fillPaint);

            if (!string.IsNullOrWhiteSpace(label))
                canvas.DrawText(label, x + 3, 22, textPaint);
        }
    }

    // ── Task 4.4: Energy contour overlay ─────────────────────────────────

    /// <summary>
    /// Renders a smoothed energy-curve line overlay on the waveform bitmap.
    /// </summary>
    /// <param name="energyCurve">Time-series energy values in [0, 1].</param>
    public static void OverlayEnergyContour(
        SKBitmap bmp,
        IReadOnlyList<float> energyCurve,
        double zoom = 1.0,
        double scrollOffset = 0.0)
    {
        if (bmp is null || energyCurve is null || energyCurve.Count == 0) return;

        using var canvas = new SKCanvas(bmp);
        zoom = Math.Max(1.0, zoom);

        int visibleSamples = Math.Max(1, (int)Math.Ceiling(energyCurve.Count / zoom));
        int maxStart = energyCurve.Count - visibleSamples;
        int startSample = Math.Clamp((int)(scrollOffset * maxStart), 0, maxStart);

        using var paint = new SKPaint
        {
            Color       = new SKColor(255, 220, 50, 200),
            StrokeWidth = 2f,
            IsAntialias = true,
            Style       = SKPaintStyle.Stroke
        };

        var path = new SKPath();
        float wScale = (float)bmp.Width / visibleSamples;

        for (int i = 0; i < visibleSamples; i++)
        {
            int srcIdx = Math.Clamp(startSample + i, 0, energyCurve.Count - 1);
            float x = i * wScale;
            // Map energy 0-1 to bottom→top: y = height - energy*height
            float y = bmp.Height - energyCurve[srcIdx] * (bmp.Height - 4) - 2;

            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }

        canvas.DrawPath(path, paint);
    }
}
