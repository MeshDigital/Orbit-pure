using System;
using SkiaSharp;

namespace SLSKDONET.Services.Video;

/// <summary>
/// Renders audio-reactive visual primitives into a <see cref="SKBitmap"/>
/// from a <see cref="VisualFrame"/> snapshot.  Implements Issue 5.1 / #35.
/// </summary>
public sealed class VisualEngine
{
    // ── Configuration ────────────────────────────────────────────────────

    public VisualPreset Preset { get; set; } = VisualPreset.Bars;

    /// <summary>Rendered frame width in pixels.</summary>
    public int Width  { get; set; } = 1920;

    /// <summary>Rendered frame height in pixels.</summary>
    public int Height { get; set; } = 1080;

    /// <summary>Base hue (0–360) used by colour mapping.</summary>
    public float BaseHue { get; set; } = 200f;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Renders a single frame for the supplied <paramref name="frame"/> snapshot.
    /// The caller owns the returned <see cref="SKBitmap"/> and must dispose it.
    /// </summary>
    public SKBitmap RenderFrame(VisualFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bmp    = new SKBitmap(Math.Max(1, Width), Math.Max(1, Height));
        using var canvas = new SKCanvas(bmp);

        canvas.Clear(SKColors.Black);

        switch (Preset)
        {
            case VisualPreset.Bars:     DrawBars(canvas, frame);     break;
            case VisualPreset.Circles:  DrawCircles(canvas, frame);  break;
            case VisualPreset.Waveform: DrawWaveform(canvas, frame); break;
            case VisualPreset.Particles:DrawParticles(canvas, frame);break;
        }

        return bmp;
    }

    /// <summary>
    /// Maps a <paramref name="frame"/> to a <see cref="VisualState"/> describing
    /// derived rendering parameters.  This is the pure-logic path covered by unit tests.
    /// </summary>
    public VisualState ComputeState(VisualFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        float energy  = Math.Clamp(frame.Energy, 0f, 1f);
        float pulse   = Math.Clamp(frame.BeatPulse, 0f, 1f);

        // Hue shifts with progress; saturation driven by energy
        float hue        = (BaseHue + frame.Progress * 120f) % 360f;
        float saturation = 0.4f + energy * 0.6f;
        float brightness = 0.4f + pulse  * 0.6f;

        // Scale: 0.5 at silence, up to 1.5 on full beat + energy
        float scale = 0.5f + energy * 0.5f + pulse * 0.5f;

        // Motion speed proportional to BPM (normalised at 120 bpm = 1.0)
        float motionSpeed = frame.Bpm > 0f ? frame.Bpm / 120f : 1f;

        return new VisualState
        {
            Hue          = hue,
            Saturation   = saturation,
            Brightness   = brightness,
            Scale        = Math.Clamp(scale, 0f, 2f),
            MotionSpeed  = Math.Clamp(motionSpeed, 0f, 4f),
            PrimaryColor = HsvToSKColor(hue, saturation, brightness),
        };
    }

    // ── Visual primitives ────────────────────────────────────────────────

    private void DrawBars(SKCanvas canvas, VisualFrame frame)
    {
        var state  = ComputeState(frame);
        int bands  = frame.FrequencyBands.Length;
        if (bands == 0) return;

        float barW = (float)Width / bands;

        using var paint = new SKPaint { IsAntialias = true, Color = state.PrimaryColor };

        for (int i = 0; i < bands; i++)
        {
            float magnitude = Math.Clamp(frame.FrequencyBands[i], 0f, 1f) * state.Scale;
            float barH      = magnitude * Height;
            float x         = i * barW;
            float y         = Height - barH;

            // Per-band hue shift
            float hue = (state.Hue + i * (360f / bands)) % 360f;
            paint.Color = HsvToSKColor(hue, state.Saturation, state.Brightness);

            canvas.DrawRect(x, y, barW - 2, barH, paint);
        }
    }

    private void DrawCircles(SKCanvas canvas, VisualFrame frame)
    {
        var state = ComputeState(frame);
        float cx  = Width  / 2f;
        float cy  = Height / 2f;

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        int rings = Math.Min(frame.FrequencyBands.Length, 6);
        for (int i = 0; i < rings; i++)
        {
            float magnitude = Math.Clamp(frame.FrequencyBands[i], 0f, 1f);
            float radius    = (i + 1) * (Height / (2f * rings)) * magnitude * state.Scale;
            float strokeW   = 2f + magnitude * 8f;

            float hue = (state.Hue + i * 40f) % 360f;
            paint.Color       = HsvToSKColor(hue, state.Saturation, state.Brightness);
            paint.StrokeWidth = strokeW;

            canvas.DrawCircle(cx, cy, Math.Max(1f, radius), paint);
        }
    }

    private void DrawWaveform(SKCanvas canvas, VisualFrame frame)
    {
        var state  = ComputeState(frame);
        int bands  = frame.FrequencyBands.Length;
        if (bands < 2) return;

        float cx = Width / 2f;
        float cy = Height / 2f;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            Color       = state.PrimaryColor,
        };

        float stepX = (float)Width / (bands - 1);

        using var path = new SKPath();
        for (int i = 0; i < bands; i++)
        {
            float mag = Math.Clamp(frame.FrequencyBands[i], 0f, 1f);
            float x   = i * stepX;
            // Alternate above/below centre for waveform look
            float y   = cy + (i % 2 == 0 ? -1f : 1f) * mag * cy * state.Scale;

            if (i == 0) path.MoveTo(x, y);
            else        path.LineTo(x, y);
        }

        canvas.DrawPath(path, paint);
    }

    private void DrawParticles(SKCanvas canvas, VisualFrame frame)
    {
        var   state    = ComputeState(frame);
        float energy   = Math.Clamp(frame.Energy, 0f, 1f);
        int   count    = (int)(energy * 80f + 10f);

        // Deterministic pseudo-random positions seeded by progress
        var   rng      = new System.Random((int)(frame.Progress * 10000f));

        using var paint = new SKPaint { IsAntialias = true };

        for (int i = 0; i < count; i++)
        {
            float x     = (float)(rng.NextDouble() * Width);
            float y     = (float)(rng.NextDouble() * Height);
            float r     = 2f + (float)(rng.NextDouble() * 8f * state.Scale);
            float hue   = (state.Hue + (float)(rng.NextDouble() * 60f)) % 360f;
            paint.Color = HsvToSKColor(hue, state.Saturation, state.Brightness);

            canvas.DrawCircle(x, y, r, paint);
        }
    }

    // ── Colour helpers ───────────────────────────────────────────────────

    private static SKColor HsvToSKColor(float h, float s, float v)
    {
        h = h % 360f;
        if (h < 0) h += 360f;
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);

        float c  = v * s;
        float x  = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
        float m  = v - c;

        float r, g, b;
        if      (h < 60)  { r = c;  g = x;  b = 0f; }
        else if (h < 120) { r = x;  g = c;  b = 0f; }
        else if (h < 180) { r = 0f; g = c;  b = x;  }
        else if (h < 240) { r = 0f; g = x;  b = c;  }
        else if (h < 300) { r = x;  g = 0f; b = c;  }
        else              { r = c;  g = 0f; b = x;  }

        return new SKColor(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }
}

/// <summary>
/// Derived rendering parameters computed from a <see cref="VisualFrame"/>.
/// Pure data structure – no SkiaSharp dependency – making it unit-testable.
/// </summary>
public sealed class VisualState
{
    public float   Hue          { get; init; }
    public float   Saturation   { get; init; }
    public float   Brightness   { get; init; }
    public float   Scale        { get; init; }
    public float   MotionSpeed  { get; init; }
    public SKColor PrimaryColor { get; init; }
}
