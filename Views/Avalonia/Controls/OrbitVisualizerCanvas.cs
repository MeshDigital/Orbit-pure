using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models.Entertainment;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// ORBIT Pure Entertainment visualizer canvas — SkiaSharp-based, beat-reactive,
/// supporting 12 distinct presets that respond to spectrum data, VU levels,
/// energy, mood, BPM, and album-art color palette.
/// </summary>
public sealed class OrbitVisualizerCanvas : Control
{
    // ── Styled Properties ────────────────────────────────────────────────────

    public static readonly StyledProperty<float[]?> SpectrumDataProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float[]?>(nameof(SpectrumData));

    public static readonly StyledProperty<float> VuLeftProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float>(nameof(VuLeft), 0f);

    public static readonly StyledProperty<float> VuRightProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float>(nameof(VuRight), 0f);

    public static readonly StyledProperty<double> EnergyProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, double>(nameof(Energy), 0.5);

    public static readonly StyledProperty<double> BpmProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, double>(nameof(Bpm), 120);

    public static readonly StyledProperty<string?> MoodTagProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, string?>(nameof(MoodTag));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, bool>(nameof(IsPlaying), false);

    public static readonly StyledProperty<VisualizerPreset> PresetProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, VisualizerPreset>(
            nameof(Preset), VisualizerPreset.SpectrumBars);

    public static readonly StyledProperty<VisualizerEngineMode> EngineModeProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, VisualizerEngineMode>(
            nameof(EngineMode), VisualizerEngineMode.Standard);

    /// <summary>Primary hue derived from album art (0–360). -1 = use default palette.</summary>
    public static readonly StyledProperty<float> AlbumHueProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float>(nameof(AlbumHue), -1f);

    // ── Property Accessors ───────────────────────────────────────────────────

    public float[]? SpectrumData { get => GetValue(SpectrumDataProperty); set => SetValue(SpectrumDataProperty, value); }
    public float VuLeft { get => GetValue(VuLeftProperty); set => SetValue(VuLeftProperty, value); }
    public float VuRight { get => GetValue(VuRightProperty); set => SetValue(VuRightProperty, value); }
    public double Energy { get => GetValue(EnergyProperty); set => SetValue(EnergyProperty, value); }
    public double Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public string? MoodTag { get => GetValue(MoodTagProperty); set => SetValue(MoodTagProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public VisualizerPreset Preset { get => GetValue(PresetProperty); set => SetValue(PresetProperty, value); }
    public VisualizerEngineMode EngineMode { get => GetValue(EngineModeProperty); set => SetValue(EngineModeProperty, value); }
    public float AlbumHue { get => GetValue(AlbumHueProperty); set => SetValue(AlbumHueProperty, value); }

    // ── Internal State ───────────────────────────────────────────────────────

    private readonly DispatcherTimer _renderTimer;
    private readonly Random _rng = new();
    private double _time;
    private float[] _smoothedSpectrum = Array.Empty<float>();
    private int _idleTickCounter;

    // Particle system
    private readonly List<Particle> _particles = [];

    // Ripple pool state
    private readonly List<RippleRing> _ripples = [];
    private double _lastBeatTime;

    // Digital rain columns
    private RainColumn[]? _rainColumns;

    // Waterfall history
    private readonly Queue<float[]> _waterfallHistory = [];
    private const int WaterfallDepth = 60;

    // Phase scope trace
    private readonly Queue<(float x, float y)> _scopeTrace = [];
    private const int ScopeTraceLength = 80;

    // ── Constructor ──────────────────────────────────────────────────────────

    static OrbitVisualizerCanvas()
    {
        AffectsRender<OrbitVisualizerCanvas>(
            PresetProperty, EngineModeProperty, IsPlayingProperty,
            EnergyProperty, MoodTagProperty, AlbumHueProperty);
    }

    public OrbitVisualizerCanvas()
    {
        _renderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),   // ~20 fps
            DispatcherPriority.Background,
            OnRenderTick);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _renderTimer.Stop();
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (!IsVisible || Bounds.Width < 4 || Bounds.Height < 4)
        {
            return;
        }

        bool hasSpectrum = HasActiveSpectrum(SpectrumData);
        if (!IsPlaying && !hasSpectrum)
        {
            // Keep ambient animation alive at low cadence to avoid UI thread pressure.
            _idleTickCounter++;
            if (_idleTickCounter % 10 != 0)
            {
                return;
            }
        }
        else
        {
            _idleTickCounter = 0;
        }

        _time += 0.033;
        UpdateSmoothSpectrum();
        if (IsPlaying || hasSpectrum)
        {
            UpdateParticles();
        }
        InvalidateVisual();
    }

    private static bool HasActiveSpectrum(float[]? spectrum)
    {
        if (spectrum == null || spectrum.Length == 0)
        {
            return false;
        }

        const float activityThreshold = 0.005f;
        for (int i = 0; i < spectrum.Length; i++)
        {
            if (spectrum[i] > activityThreshold)
            {
                return true;
            }
        }

        return false;
    }

    // ── Render ───────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width < 4 || bounds.Height < 4) return;

        // Determine effective preset: metadata-driven mode may override it
        var effectivePreset = EngineMode == VisualizerEngineMode.MetadataDriven
            ? MetadataDrivenPreset()
            : Preset;

        // Ambient mode always uses AmbientBreath
        if (EngineMode == VisualizerEngineMode.Ambient)
            effectivePreset = VisualizerPreset.AmbientBreath;

        context.Custom(new VisualizerDrawOperation(bounds, this, effectivePreset, _time));
    }

    // ── Metadata-Driven Preset Selection ─────────────────────────────────────

    private VisualizerPreset MetadataDrivenPreset()
    {
        var mood = MoodTag?.ToLowerInvariant() ?? "";
        double energy = Energy;

        if (mood.Contains("ambient") || mood.Contains("sleep") || mood.Contains("meditat"))
            return VisualizerPreset.AmbientBreath;

        if (energy > 0.8)
            return Bpm > 140 ? VisualizerPreset.DigitalRain : VisualizerPreset.NeonParticles;

        if (energy > 0.6)
            return VisualizerPreset.StarBurst;

        if (energy > 0.4)
            return VisualizerPreset.CircularWave;

        if (mood.Contains("sad") || mood.Contains("dark") || mood.Contains("melanchol"))
            return VisualizerPreset.AuroraBands;

        return VisualizerPreset.PlasmaMesh;
    }

    // ── Smoothing & Particle Updates ─────────────────────────────────────────

    private void UpdateSmoothSpectrum()
    {
        var raw = SpectrumData;
        if (raw == null || raw.Length == 0) return;

        if (_smoothedSpectrum.Length != raw.Length)
            _smoothedSpectrum = new float[raw.Length];

        for (int i = 0; i < raw.Length; i++)
            _smoothedSpectrum[i] = _smoothedSpectrum[i] * 0.7f + raw[i] * 0.3f;
    }

    private void UpdateParticles()
    {
        int target = EngineMode == VisualizerEngineMode.Ambient ? 20 : (int)(30 + Energy * 70);
        while (_particles.Count < target) _particles.Add(SpawnParticle());
        while (_particles.Count > target) _particles.RemoveAt(0);

        float speed = EngineMode == VisualizerEngineMode.Ambient ? 0.05f : (float)(0.3 + Energy * 1.5);
        foreach (var p in _particles)
        {
            p.X += p.Vx * speed;
            p.Y += p.Vy * speed;
            p.Life -= 0.008f * speed;
            if (p.Life <= 0 || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)
            {
                var fresh = SpawnParticle();
                p.X = fresh.X; p.Y = fresh.Y;
                p.Vx = fresh.Vx; p.Vy = fresh.Vy;
                p.Life = fresh.Life;
                p.Size = fresh.Size;
            }
        }
    }

    private Particle SpawnParticle()
    {
        var angle = _rng.NextDouble() * Math.PI * 2;
        var speed = _rng.NextDouble() * 0.003 + 0.001;
        return new Particle
        {
            X = (float)_rng.NextDouble(),
            Y = (float)_rng.NextDouble(),
            Vx = (float)(Math.Cos(angle) * speed),
            Vy = (float)(Math.Sin(angle) * speed),
            Size = (float)(_rng.NextDouble() * 4 + 1),
            Life = (float)(_rng.NextDouble() * 0.7 + 0.3),
        };
    }

    // ── Helper Types ─────────────────────────────────────────────────────────

    private sealed class Particle
    {
        public float X, Y, Vx, Vy, Size, Life;
    }

    private sealed class RippleRing
    {
        public float Radius;
        public float MaxRadius;
        public float Alpha = 1f;
    }

    private sealed class RainColumn
    {
        public int X;
        public int Y;
        public int Speed;
        public float Brightness;
    }

    // ── Custom Draw Operation ─────────────────────────────────────────────────

    private sealed class VisualizerDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly OrbitVisualizerCanvas _canvas;
        private readonly VisualizerPreset _preset;
        private readonly double _time;

        public Rect Bounds => _bounds;

        public VisualizerDrawOperation(
            Rect bounds,
            OrbitVisualizerCanvas canvas,
            VisualizerPreset preset,
            double time)
        {
            _bounds = bounds;
            _canvas = canvas;
            _preset = preset;
            _time = time;
        }

        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (!context.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var lease)) return;
            using var skia = lease!.Lease();
            var canvas = skia.SkCanvas;

            float w = (float)_bounds.Width;
            float h = (float)_bounds.Height;

            canvas.Clear(SKColors.Transparent);

            switch (_preset)
            {
                case VisualizerPreset.SpectrumBars:  DrawSpectrumBars(canvas, w, h); break;
                case VisualizerPreset.CircularWave:  DrawCircularWave(canvas, w, h); break;
                case VisualizerPreset.NeonParticles: DrawNeonParticles(canvas, w, h); break;
                case VisualizerPreset.Oscilloscope:  DrawOscilloscope(canvas, w, h); break;
                case VisualizerPreset.StarBurst:     DrawStarBurst(canvas, w, h); break;
                case VisualizerPreset.PlasmaMesh:    DrawPlasmaMesh(canvas, w, h); break;
                case VisualizerPreset.AuroraBands:   DrawAuroraBands(canvas, w, h); break;
                case VisualizerPreset.Waterfall:     DrawWaterfall(canvas, w, h); break;
                case VisualizerPreset.PhaseScope:    DrawPhaseScope(canvas, w, h); break;
                case VisualizerPreset.DigitalRain:   DrawDigitalRain(canvas, w, h); break;
                case VisualizerPreset.RipplePool:    DrawRipplePool(canvas, w, h); break;
                case VisualizerPreset.AmbientBreath: DrawAmbientBreath(canvas, w, h); break;
                default:                             DrawSpectrumBars(canvas, w, h); break;
            }
        }

        // ── Palette Helper ───────────────────────────────────────────────────

        private SKColor PrimaryColor(float alpha = 1f)
        {
            float hue = _canvas.AlbumHue >= 0 ? _canvas.AlbumHue : EnergyHue();
            var color = SKColor.FromHsv(hue, 0.9f, 1.0f);
            return color.WithAlpha((byte)(alpha * 255));
        }

        private float EnergyHue()
        {
            // Low energy = blue (220°), high energy = red (0°/360°)
            float energy = (float)_canvas.Energy;
            return 220f * (1f - energy); // 220→0
        }

        private float[] GetSpectrum(int bins)
        {
            var src = _canvas._smoothedSpectrum;
            if (src == null || src.Length == 0) return new float[bins];

            var result = new float[bins];
            float ratio = (float)src.Length / bins;
            for (int i = 0; i < bins; i++)
            {
                int start = (int)(i * ratio);
                int end = Math.Min((int)((i + 1) * ratio), src.Length);
                float sum = 0;
                for (int j = start; j < end; j++) sum += src[j];
                result[i] = (end > start) ? sum / (end - start) : 0f;
            }
            return result;
        }

        // ── 1. Spectrum Bars ─────────────────────────────────────────────────

        private void DrawSpectrumBars(SKCanvas canvas, float w, float h)
        {
            const int Bars = 64;
            var spectrum = GetSpectrum(Bars);
            float barW = w / Bars;
            float intensity = (float)_canvas.Energy;

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            for (int i = 0; i < Bars; i++)
            {
                float val = Math.Min(spectrum[i] * 8f * (0.5f + intensity), 1f);
                float barH = h * val;
                float x = i * barW;

                // Color shifts from teal→purple→red as bar height grows
                float hue = _canvas.AlbumHue >= 0
                    ? _canvas.AlbumHue + (val * 60f)
                    : 180f - (val * 180f);
                paint.Color = SKColor.FromHsv(hue % 360, 0.8f, 1.0f);

                // Draw bar with rounded top
                var rect = new SKRect(x + 1, h - barH, x + barW - 1, h);
                canvas.DrawRect(rect, paint);

                // Reflection
                paint.Color = paint.Color.WithAlpha(60);
                canvas.DrawRect(new SKRect(x + 1, h, x + barW - 1, h + barH * 0.3f), paint);
                paint.Color = paint.Color.WithAlpha(255);
            }
        }

        // ── 2. Circular Wave ─────────────────────────────────────────────────

        private void DrawCircularWave(SKCanvas canvas, float w, float h)
        {
            const int Points = 256;
            var spectrum = GetSpectrum(Points);
            var cx = w / 2; var cy = h / 2;
            float baseR = Math.Min(w, h) * 0.25f;
            float intensity = (float)_canvas.Energy;

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f,
                Color = PrimaryColor(),
            };

            var path = new SKPath();
            for (int i = 0; i <= Points; i++)
            {
                int idx = i % Points;
                float angle = (float)(idx * Math.PI * 2 / Points) - (float)(_time * 0.5);
                float r = baseR + spectrum[idx] * h * 0.35f * (0.5f + intensity);
                float px = cx + r * MathF.Cos(angle);
                float py = cy + r * MathF.Sin(angle);
                if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
            }
            path.Close();
            canvas.DrawPath(path, paint);

            // Inner solid ring
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1f;
            paint.Color = PrimaryColor(0.3f);
            canvas.DrawCircle(cx, cy, baseR * 0.8f, paint);
        }

        // ── 3. Neon Particles ─────────────────────────────────────────────────

        private void DrawNeonParticles(SKCanvas canvas, float w, float h)
        {
            float intensity = (float)_canvas.Energy;
            var color = PrimaryColor();

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            foreach (var p in _canvas._particles)
            {
                float alpha = p.Life * intensity;
                paint.Color = color.WithAlpha((byte)(alpha * 200));

                // Glow halo
                using var halo = new SKPaint
                {
                    IsAntialias = true,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, p.Size * 2),
                    Color = color.WithAlpha((byte)(alpha * 100)),
                    Style = SKPaintStyle.Fill
                };
                canvas.DrawCircle(p.X * w, p.Y * h, p.Size * 2.5f, halo);
                canvas.DrawCircle(p.X * w, p.Y * h, p.Size, paint);
            }
        }

        // ── 4. Oscilloscope ───────────────────────────────────────────────────

        private void DrawOscilloscope(SKCanvas canvas, float w, float h)
        {
            var src = _canvas._smoothedSpectrum;
            if (src == null || src.Length < 2) return;

            float cy = h / 2;
            float intensity = (float)_canvas.Energy;

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                Color = PrimaryColor(),
            };

            var path = new SKPath();
            for (int i = 0; i < src.Length; i++)
            {
                float x = (float)i / src.Length * w;
                // Use spectrum as proxy waveform — oscillate around centre
                float val = src[i] * 6f * intensity * (float)Math.Sin(_time * 4 + i * 0.1);
                float y = cy + val * h * 0.4f;
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.DrawPath(path, paint);

            // Centre line
            paint.Color = PrimaryColor(0.2f);
            canvas.DrawLine(0, cy, w, cy, paint);
        }

        // ── 5. Star Burst ─────────────────────────────────────────────────────

        private void DrawStarBurst(SKCanvas canvas, float w, float h)
        {
            const int Spokes = 128;
            var spectrum = GetSpectrum(Spokes);
            float cx = w / 2; float cy = h / 2;
            float baseR = Math.Min(w, h) * 0.08f;
            float maxR = Math.Min(w, h) * 0.45f;
            float intensity = (float)_canvas.Energy;

            using var paint = new SKPaint { IsAntialias = true, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };

            for (int i = 0; i < Spokes; i++)
            {
                float angle = (float)(i * Math.PI * 2 / Spokes) + (float)(_time * 0.3);
                float r = baseR + spectrum[i] * maxR * (0.4f + intensity * 0.6f);

                float hue = (_canvas.AlbumHue >= 0 ? _canvas.AlbumHue : 0) + (float)i / Spokes * 60f;
                paint.Color = SKColor.FromHsv(hue % 360, 0.8f, 1f, (byte)(spectrum[i] * 200 + 50));

                float x1 = cx + MathF.Cos(angle) * baseR;
                float y1 = cy + MathF.Sin(angle) * baseR;
                float x2 = cx + MathF.Cos(angle) * r;
                float y2 = cy + MathF.Sin(angle) * r;
                canvas.DrawLine(x1, y1, x2, y2, paint);
            }

            // Centre glow
            using var glow = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = PrimaryColor(0.6f),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 20),
            };
            canvas.DrawCircle(cx, cy, baseR * 1.5f, glow);
        }

        // ── 6. Plasma Mesh ────────────────────────────────────────────────────

        private void DrawPlasmaMesh(SKCanvas canvas, float w, float h)
        {
            float t = (float)_time;
            float intensity = (float)_canvas.Energy;
            const int GridStep = 20;

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            for (int y = 0; y <= h; y += GridStep)
            {
                for (int x = 0; x <= w; x += GridStep)
                {
                    float nx = x / w;
                    float ny = y / h;
                    float v = MathF.Sin(nx * 10 + t) +
                              MathF.Sin(ny * 10 + t * 1.3f) +
                              MathF.Sin((nx + ny) * 8 + t * 0.7f);

                    float hue = _canvas.AlbumHue >= 0
                        ? (_canvas.AlbumHue + v * 40f) % 360f
                        : (v + 2f) / 4f * 360f;
                    hue = (hue + 360f) % 360f;

                    float alpha = 0.5f + intensity * 0.5f;
                    paint.Color = SKColor.FromHsv(hue, 0.7f, 0.9f, (byte)(alpha * 180));

                    canvas.DrawRect(x, y, GridStep, GridStep, paint);
                }
            }
        }

        // ── 7. Aurora Bands ───────────────────────────────────────────────────

        private void DrawAuroraBands(SKCanvas canvas, float w, float h)
        {
            float t = (float)_time;
            const int Bands = 6;
            float intensity = (float)_canvas.Energy;

            for (int b = 0; b < Bands; b++)
            {
                float phase = b * MathF.PI / Bands;
                float bandH = h / (Bands + 2);

                var colors = new SKColor[]
                {
                    SKColors.Transparent,
                    PrimaryColor((0.1f + intensity * 0.3f) * (1f - (float)b / Bands)).WithAlpha(
                        (byte)(60 + intensity * 100)),
                    SKColors.Transparent,
                };

                var yPositions = new float[] { 0f, 0.5f, 1f };

                float yOff = (float)(MathF.Sin(t * 0.4f + phase) * bandH * 0.5f);
                float yBase = (float)b / Bands * h + yOff;

                var rect = new SKRect(0, yBase, w, yBase + bandH * 2f);
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, rect.Top),
                    new SKPoint(0, rect.Bottom),
                    colors, yPositions, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                canvas.DrawRect(rect, paint);
            }
        }

        // ── 8. Waterfall ──────────────────────────────────────────────────────

        private void DrawWaterfall(SKCanvas canvas, float w, float h)
        {
            const int Bins = 128;
            var row = GetSpectrum(Bins);

            // Push new row
            _canvas._waterfallHistory.Enqueue(row);
            while (_canvas._waterfallHistory.Count > WaterfallDepth)
                _canvas._waterfallHistory.Dequeue();

            float rowH = h / WaterfallDepth;
            float binW = w / Bins;
            float hueBase = _canvas.AlbumHue >= 0 ? _canvas.AlbumHue : 220f;

            using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };

            int rowIdx = 0;
            foreach (var frame in _canvas._waterfallHistory.Reverse())
            {
                float y = rowIdx * rowH;
                for (int i = 0; i < Bins; i++)
                {
                    float val = Math.Min(frame[i] * 10f, 1f);
                    if (val < 0.01f) continue;
                    float hue = (hueBase + val * 120f) % 360f;
                    paint.Color = SKColor.FromHsv(hue, 0.9f, 1f, (byte)(val * 255));
                    canvas.DrawRect(i * binW, y, binW, rowH, paint);
                }
                rowIdx++;
            }
        }

        // ── 9. Phase Scope ─────────────────────────────────────────────────────

        private void DrawPhaseScope(SKCanvas canvas, float w, float h)
        {
            float cx = w / 2; float cy = h / 2;
            float size = Math.Min(w, h) * 0.4f;

            // Goniometer background grid
            using var gridPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f, Color = PrimaryColor(0.2f)
            };
            canvas.DrawCircle(cx, cy, size, gridPaint);
            canvas.DrawLine(cx - size, cy, cx + size, cy, gridPaint);
            canvas.DrawLine(cx, cy - size, cx, cy + size, gridPaint);

            // Lissajous trace
            float L = _canvas.VuLeft;
            float R = _canvas.VuRight;
            float m = (L + R) * 0.707f;
            float s = (L - R) * 0.707f;

            _canvas._scopeTrace.Enqueue((s, m));
            while (_canvas._scopeTrace.Count > ScopeTraceLength)
                _canvas._scopeTrace.Dequeue();

            var trace = _canvas._scopeTrace.ToArray();
            using var tracePaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
            };
            for (int i = 1; i < trace.Length; i++)
            {
                float alpha = (float)i / trace.Length;
                tracePaint.Color = PrimaryColor(alpha);
                canvas.DrawLine(
                    cx + trace[i - 1].x * size, cy - trace[i - 1].y * size,
                    cx + trace[i].x * size,     cy - trace[i].y * size,
                    tracePaint);
            }

            // Current head dot
            using var dotPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = PrimaryColor() };
            canvas.DrawCircle(cx + s * size, cy - m * size, 3f, dotPaint);
        }

        // ── 10. Digital Rain ──────────────────────────────────────────────────

        private void DrawDigitalRain(SKCanvas canvas, float w, float h)
        {
            const int ColWidth = 14;
            int cols = (int)(w / ColWidth);
            float intensity = (float)_canvas.Energy;

            // Init columns
            if (_canvas._rainColumns == null || _canvas._rainColumns.Length != cols)
            {
                _canvas._rainColumns = new RainColumn[cols];
                for (int i = 0; i < cols; i++)
                    _canvas._rainColumns[i] = new RainColumn
                    {
                        X = i * ColWidth,
                        Y = _canvas._rng.Next(-(int)h, 0),
                        Speed = _canvas._rng.Next(2, 6),
                        Brightness = (float)_canvas._rng.NextDouble(),
                    };
            }

            var spectrum = GetSpectrum(cols);
            using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };

            for (int i = 0; i < cols; i++)
            {
                var col = _canvas._rainColumns[i];
                float speedMult = 0.5f + spectrum[i] * 3f * intensity;
                col.Y += (int)(col.Speed * speedMult);
                if (col.Y > h)
                {
                    col.Y = _canvas._rng.Next(-(int)h / 2, 0);
                    col.Speed = _canvas._rng.Next(2, 6);
                }

                // Head glyph (bright)
                float hue = _canvas.AlbumHue >= 0 ? _canvas.AlbumHue : 120f;
                paint.Color = SKColor.FromHsv(hue, 0.2f, 1f);
                canvas.DrawRect(col.X, col.Y, ColWidth - 1, ColWidth, paint);

                // Trail
                for (int trailIndex = 1; trailIndex < 12; trailIndex++)
                {
                    float trailAlpha = (1f - (float)trailIndex / 12f) * col.Brightness;
                    paint.Color = SKColor.FromHsv(hue, 0.6f, 0.8f, (byte)(trailAlpha * 180));
                    canvas.DrawRect(col.X, col.Y - trailIndex * ColWidth, ColWidth - 1, ColWidth, paint);
                }
            }
        }

        // ── 11. Ripple Pool ───────────────────────────────────────────────────

        private void DrawRipplePool(SKCanvas canvas, float w, float h)
        {
            float cx = w / 2; float cy = h / 2;
            float vu = (_canvas.VuLeft + _canvas.VuRight) / 2f;
            double now = _time;

            // Spawn ripple on beat
            if (vu > 0.4f && now - _canvas._lastBeatTime > 0.3)
            {
                _canvas._ripples.Add(new RippleRing { MaxRadius = Math.Min(w, h) * 0.5f });
                _canvas._lastBeatTime = now;
            }

            // Update ripples
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

            foreach (var ripple in _canvas._ripples.ToList())
            {
                ripple.Radius += (float)(2.0 + _canvas.Energy * 4.0);
                ripple.Alpha = 1f - ripple.Radius / ripple.MaxRadius;

                if (ripple.Alpha <= 0.01f) { _canvas._ripples.Remove(ripple); continue; }

                paint.Color = PrimaryColor(ripple.Alpha * 0.8f);
                paint.StrokeWidth = ripple.Alpha * 3f;
                canvas.DrawCircle(cx, cy, ripple.Radius, paint);
            }

            // Calm pool background gradient
            using var bgPaint = new SKPaint
            {
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(cx, cy), Math.Min(w, h) * 0.5f,
                    new[] { PrimaryColor(0.1f), SKColors.Transparent },
                    new[] { 0f, 1f }, SKShaderTileMode.Clamp),
                IsAntialias = true
            };
            canvas.DrawCircle(cx, cy, Math.Min(w, h) * 0.5f, bgPaint);
        }

        // ── 12. Ambient Breath ────────────────────────────────────────────────

        private void DrawAmbientBreath(SKCanvas canvas, float w, float h)
        {
            float t = (float)_time;
            float breathe = (MathF.Sin(t * 0.4f) + 1f) / 2f; // 0→1 over ~7s
            float cx = w / 2; float cy = h / 2;
            float maxR = Math.Min(w, h) * 0.4f;
            float r = maxR * (0.5f + breathe * 0.5f);

            float hue = _canvas.AlbumHue >= 0 ? _canvas.AlbumHue : 200f;

            // Soft radial glow
            using var glowPaint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(cx, cy), r,
                    new[]
                    {
                        SKColor.FromHsv(hue, 0.4f, 0.8f, (byte)(breathe * 80)),
                        SKColor.FromHsv(hue, 0.6f, 0.5f, (byte)(breathe * 30)),
                        SKColors.Transparent,
                    },
                    new[] { 0f, 0.6f, 1f },
                    SKShaderTileMode.Clamp),
            };
            canvas.DrawCircle(cx, cy, r, glowPaint);

            // Secondary drifting orb
            float ox = cx + MathF.Cos(t * 0.15f) * maxR * 0.3f;
            float oy = cy + MathF.Sin(t * 0.2f) * maxR * 0.25f;
            using var orbPaint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(ox, oy), r * 0.5f,
                    new[]
                    {
                        SKColor.FromHsv((hue + 30f) % 360f, 0.5f, 0.9f, (byte)(breathe * 50)),
                        SKColors.Transparent,
                    },
                    new[] { 0f, 1f },
                    SKShaderTileMode.Clamp),
            };
            canvas.DrawCircle(ox, oy, r * 0.5f, orbPaint);
        }
    }
}
