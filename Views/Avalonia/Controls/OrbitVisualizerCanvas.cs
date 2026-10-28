using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.Collections.Generic;
using SLSKDONET.Models.Entertainment;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// ORBIT's full-screen visualizer: SkiaSharp, 12 presets, driven by a log-band spectrum
/// (<see cref="SpectrumBands"/>), the live waveform, the analysed beat pulse, energy and the
/// album-art hue.
///
/// Threading: every piece of animation state lives on the UI thread and is advanced once per
/// display frame (TopLevel.RequestAnimationFrame). Each frame is frozen into an immutable
/// <see cref="Frame"/> that the render-thread draw operation reads — the draw operation never
/// touches the control or its StyledProperties (reading those off the UI thread throws on the
/// render thread, which took the app down silently before).
/// </summary>
public sealed class OrbitVisualizerCanvas : Control
{
    // ── Styled Properties ────────────────────────────────────────────────────

    public static readonly StyledProperty<float[]?> SpectrumDataProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float[]?>(nameof(SpectrumData));

    /// <summary>Mono time-domain samples for the oscilloscope/phase presets.</summary>
    public static readonly StyledProperty<float[]?> WaveformDataProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float[]?>(nameof(WaveformData));

    public static readonly StyledProperty<float> VuLeftProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float>(nameof(VuLeft), 0f);

    public static readonly StyledProperty<float> VuRightProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float>(nameof(VuRight), 0f);

    public static readonly StyledProperty<double> EnergyProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, double>(nameof(Energy), 0.5);

    /// <summary>Beat-synced pulse (0–1, peaking on each beat) from PlayerViewModel.BeatPulse.</summary>
    public static readonly StyledProperty<double> BeatPulseProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, double>(nameof(BeatPulse), 0.0);

    public static readonly StyledProperty<double> BpmProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, double>(nameof(Bpm), 120);

    public static readonly StyledProperty<string?> MoodTagProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, string?>(nameof(MoodTag));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, bool>(nameof(IsPlaying), false);

    public static readonly StyledProperty<VisualizerPreset> PresetProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, VisualizerPreset>(nameof(Preset), VisualizerPreset.SpectrumBars);

    public static readonly StyledProperty<VisualizerEngineMode> EngineModeProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, VisualizerEngineMode>(nameof(EngineMode), VisualizerEngineMode.Standard);

    /// <summary>Primary hue derived from album art (0–360). -1 = derive from energy.</summary>
    public static readonly StyledProperty<float> AlbumHueProperty =
        AvaloniaProperty.Register<OrbitVisualizerCanvas, float>(nameof(AlbumHue), -1f);

    public float[]? SpectrumData { get => GetValue(SpectrumDataProperty); set => SetValue(SpectrumDataProperty, value); }
    public float[]? WaveformData { get => GetValue(WaveformDataProperty); set => SetValue(WaveformDataProperty, value); }
    public float VuLeft { get => GetValue(VuLeftProperty); set => SetValue(VuLeftProperty, value); }
    public float VuRight { get => GetValue(VuRightProperty); set => SetValue(VuRightProperty, value); }
    public double Energy { get => GetValue(EnergyProperty); set => SetValue(EnergyProperty, value); }
    public double BeatPulse { get => GetValue(BeatPulseProperty); set => SetValue(BeatPulseProperty, value); }
    public double Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public string? MoodTag { get => GetValue(MoodTagProperty); set => SetValue(MoodTagProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public VisualizerPreset Preset { get => GetValue(PresetProperty); set => SetValue(PresetProperty, value); }
    public VisualizerEngineMode EngineMode { get => GetValue(EngineModeProperty); set => SetValue(EngineModeProperty, value); }
    public float AlbumHue { get => GetValue(AlbumHueProperty); set => SetValue(AlbumHueProperty, value); }

    // ── UI-thread animation state ────────────────────────────────────────────

    private const int BandCount = 96;
    private const int WaterfallRows = 90;
    private const int WaterfallBins = 96;
    private const int ScopeTrail = 6;

    private readonly SpectrumBands _bands = new(BandCount);
    private readonly Random _rng = new();
    private float[]? _lastSpectrumRef;
    private bool _attached;
    private bool _frameRequested;
    private bool _feedingFft;
    private TimeSpan? _lastFrameTime;
    private double _time;
    private float _hueDrift;
    private float _beat;          // smoothed beat pulse
    private double _lastRippleTime;

    private readonly List<Particle> _particles = new();
    private readonly List<Ripple> _ripples = new();
    private RainColumn[] _rain = Array.Empty<RainColumn>();
    private readonly float[][] _waterfall = new float[WaterfallRows][];
    private int _waterfallHead;
    private double _waterfallAccumulator;
    private readonly Queue<float[]> _scopeHistory = new();
    private Frame? _frame;

    public OrbitVisualizerCanvas()
    {
        for (int i = 0; i < WaterfallRows; i++) _waterfall[i] = new float[WaterfallBins];
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _lastFrameTime = null;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        SetFeedingFft(false);
    }

    private void RequestFrame()
    {
        if (!_attached || _frameRequested) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;
        if (!_attached) return;

        try
        {
            double dt = _lastFrameTime is { } last ? (now - last).TotalSeconds : 1 / 60.0;
            _lastFrameTime = now;
            dt = Math.Clamp(dt, 0, 0.1);

            // Only ask the audio engine for FFT data while actually on screen — a hidden
            // fullscreen player used to keep the FFT running for the whole session.
            bool visible = IsEffectivelyVisible && Bounds.Width >= 4 && Bounds.Height >= 4;
            SetFeedingFft(visible);
            if (visible)
            {
                Step(dt);
                InvalidateVisual();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "OrbitVisualizerCanvas: frame update failed — skipping frame");
        }
        RequestFrame();
    }

    private void SetFeedingFft(bool on)
    {
        if (on == _feedingFft) return;
        _feedingFft = on;
        var player = ResolvePlayerService();
        if (on) player?.NotifyVisualizerAttached();
        else player?.NotifyVisualizerDetached();
    }

    private static SLSKDONET.Services.IAudioPlayerService? ResolvePlayerService()
    {
        if (Design.IsDesignMode) return null;
        if (Application.Current is not SLSKDONET.App app || app.Services == null) return null;
        return app.Services.GetService(typeof(SLSKDONET.Services.IAudioPlayerService)) as SLSKDONET.Services.IAudioPlayerService;
    }

    // ── Per-frame update (UI thread) ─────────────────────────────────────────

    private void Step(double dt)
    {
        var spectrum = SpectrumData;
        bool playing = IsPlaying;
        if (playing && spectrum != null && !ReferenceEquals(spectrum, _lastSpectrumRef))
        {
            _lastSpectrumRef = spectrum;
            _bands.Push(spectrum, dt);
        }
        else if (!playing)
        {
            _bands.Decay(dt);
        }
        else
        {
            _bands.Push(null, 0); // hold: no new block this frame, keep smoothing state
            _bands.Decay(dt * 0.15);
        }

        float energy = (float)Math.Clamp(double.IsFinite(Energy) ? Energy : 0.5, 0, 1);
        float beatTarget = (float)Math.Clamp(double.IsFinite(BeatPulse) ? BeatPulse : 0, 0, 1);
        _beat += (beatTarget - _beat) * (float)Math.Min(1, dt * (beatTarget > _beat ? 40 : 10));

        double speed = EngineMode == VisualizerEngineMode.Ambient ? 0.35 : 0.6 + energy * 0.8 + _bands.Bass * 0.8;
        _time += dt * speed;
        _hueDrift = (float)((_hueDrift + dt * 4) % 360);

        var preset = EffectivePreset();
        switch (preset)
        {
            case VisualizerPreset.NeonParticles: StepParticles(dt, energy); break;
            case VisualizerPreset.RipplePool: StepRipples(dt); break;
            case VisualizerPreset.DigitalRain: StepRain(dt); break;
            case VisualizerPreset.Waterfall: StepWaterfall(dt); break;
            case VisualizerPreset.Oscilloscope:
            case VisualizerPreset.PhaseScope: StepScope(); break;
        }

        _frame = new Frame(
            preset, _time, energy, _beat, AlbumHueOrDrift(energy),
            (float[])_bands.Levels.Clone(), (float[])_bands.Peaks.Clone(),
            _bands.Bass, _bands.Mid, _bands.Treble, _bands.Overall,
            playing,
            preset is VisualizerPreset.Oscilloscope or VisualizerPreset.PhaseScope ? _scopeHistory.ToArray() : null,
            preset == VisualizerPreset.NeonParticles ? _particles.ConvertAll(p => p.Snapshot()) : null,
            preset == VisualizerPreset.RipplePool ? _ripples.ConvertAll(r => (r.Radius, r.Alpha)) : null,
            preset == VisualizerPreset.DigitalRain ? Array.ConvertAll(_rain, c => c.Snapshot()) : null,
            preset == VisualizerPreset.Waterfall ? SnapshotWaterfall() : null);
    }

    private float AlbumHueOrDrift(float energy)
    {
        var hue = AlbumHue;
        if (hue >= 0 && float.IsFinite(hue)) return hue;
        return (220f * (1f - energy) + _hueDrift * 0.25f) % 360f;
    }

    private VisualizerPreset EffectivePreset()
    {
        if (EngineMode == VisualizerEngineMode.Ambient) return VisualizerPreset.AmbientBreath;
        if (EngineMode != VisualizerEngineMode.MetadataDriven) return Preset;

        var mood = MoodTag?.ToLowerInvariant() ?? "";
        double energy = Energy;
        if (mood.Contains("ambient") || mood.Contains("sleep") || mood.Contains("meditat")) return VisualizerPreset.AmbientBreath;
        if (energy > 0.8) return Bpm > 140 ? VisualizerPreset.StarBurst : VisualizerPreset.NeonParticles;
        if (energy > 0.6) return VisualizerPreset.CircularWave;
        if (energy > 0.4) return VisualizerPreset.SpectrumBars;
        if (mood.Contains("sad") || mood.Contains("dark") || mood.Contains("melanchol")) return VisualizerPreset.AuroraBands;
        return VisualizerPreset.PlasmaMesh;
    }

    private void StepParticles(double dt, float energy)
    {
        int target = (int)(60 + energy * 120);
        // Bursts on the beat: fresh particles leave the centre fast.
        int burst = _beat > 0.85f ? (int)(6 + _bands.Bass * 18) : 0;
        for (int i = 0; i < burst && _particles.Count < target * 2; i++) _particles.Add(Particle.Spawn(_rng, fromCentre: true));
        while (_particles.Count < target) _particles.Add(Particle.Spawn(_rng, fromCentre: false));

        float push = (float)(dt * (0.4 + _bands.Bass * 2.5));
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.X += p.Vx * push;
            p.Y += p.Vy * push;
            p.Life -= (float)(dt * 0.35);
            if (p.Life <= 0 || p.X < -0.1f || p.X > 1.1f || p.Y < -0.1f || p.Y > 1.1f)
            {
                if (_particles.Count > target) _particles.RemoveAt(i);
                else _particles[i] = Particle.Spawn(_rng, fromCentre: false);
            }
        }
    }

    private void StepRipples(double dt)
    {
        if (_beat > 0.9f && _time - _lastRippleTime > 0.18)
        {
            _ripples.Add(new Ripple { Radius = 0.02f, Alpha = 1f, Speed = 0.35f + _bands.Bass * 0.6f });
            _lastRippleTime = _time;
        }
        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            var r = _ripples[i];
            r.Radius += (float)(dt * r.Speed);
            r.Alpha = Math.Max(0, 1f - r.Radius / 0.75f);
            if (r.Alpha <= 0.01f) _ripples.RemoveAt(i);
        }
    }

    private void StepRain(double dt)
    {
        const float ColWidth = 16f;
        int cols = Math.Max(1, (int)(Bounds.Width / ColWidth));
        if (_rain.Length != cols)
        {
            _rain = new RainColumn[cols];
            for (int i = 0; i < cols; i++)
                _rain[i] = new RainColumn { Y = (float)(_rng.NextDouble() * -1.0), Speed = 0.15f + (float)_rng.NextDouble() * 0.35f, Brightness = 0.4f + (float)_rng.NextDouble() * 0.6f };
        }
        var levels = _bands.Levels;
        for (int i = 0; i < cols; i++)
        {
            var c = _rain[i];
            float band = levels[Math.Min(levels.Length - 1, i * levels.Length / cols)];
            c.Y += (float)(dt * c.Speed * (0.6f + band * 3f));
            c.Level = band;
            if (c.Y > 1.3f)
            {
                c.Y = (float)(_rng.NextDouble() * -0.5);
                c.Speed = 0.15f + (float)_rng.NextDouble() * 0.35f;
            }
        }
    }

    private void StepWaterfall(double dt)
    {
        // One row every ~22 ms regardless of display rate, so the scroll speed is steady.
        _waterfallAccumulator += dt;
        if (_waterfallAccumulator < 0.022) return;
        _waterfallAccumulator = 0;
        _waterfallHead = (_waterfallHead + 1) % WaterfallRows;
        var row = _waterfall[_waterfallHead];
        var levels = _bands.Levels;
        for (int i = 0; i < WaterfallBins; i++) row[i] = levels[i * levels.Length / WaterfallBins];
    }

    private float[][] SnapshotWaterfall()
    {
        var rows = new float[WaterfallRows][];
        for (int i = 0; i < WaterfallRows; i++)
            rows[i] = (float[])_waterfall[(_waterfallHead - i + WaterfallRows) % WaterfallRows].Clone(); // newest first
        return rows;
    }

    private void StepScope()
    {
        var wave = WaveformData;
        if (wave is not { Length: > 16 } || !IsPlaying) return;
        if (_scopeHistory.Count > 0 && ReferenceEquals(_scopeHistory.Peek(), wave)) return;
        _scopeHistory.Enqueue(wave);
        while (_scopeHistory.Count > ScopeTrail) _scopeHistory.Dequeue();
    }

    // ── Render (UI thread → render thread handoff) ──────────────────────────

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 4 || bounds.Height < 4 || _frame is null) return;
        context.Custom(new DrawOperation(bounds, _frame));
    }

    // ── Snapshot types ───────────────────────────────────────────────────────

    private sealed record Frame(
        VisualizerPreset Preset, double Time, float Energy, float Beat, float Hue,
        float[] Levels, float[] Peaks, float Bass, float Mid, float Treble, float Overall,
        bool Playing,
        float[][]? Scope,
        List<ParticleState>? Particles,
        List<(float Radius, float Alpha)>? Ripples,
        RainState[]? Rain,
        float[][]? Waterfall);

    private readonly record struct ParticleState(float X, float Y, float Size, float Life, float HueShift);
    private readonly record struct RainState(float Y, float Level, float Brightness);

    private sealed class Particle
    {
        public float X, Y, Vx, Vy, Size, Life, HueShift;

        public static Particle Spawn(Random rng, bool fromCentre)
        {
            double angle = rng.NextDouble() * Math.PI * 2;
            double speed = fromCentre ? 0.25 + rng.NextDouble() * 0.35 : 0.02 + rng.NextDouble() * 0.06;
            return new Particle
            {
                X = fromCentre ? 0.5f : (float)rng.NextDouble(),
                Y = fromCentre ? 0.5f : (float)rng.NextDouble(),
                Vx = (float)(Math.Cos(angle) * speed),
                Vy = (float)(Math.Sin(angle) * speed),
                Size = (float)(rng.NextDouble() * 3 + (fromCentre ? 2 : 1)),
                Life = (float)(rng.NextDouble() * 0.5 + 0.5),
                HueShift = (float)(rng.NextDouble() * 50 - 25),
            };
        }

        public ParticleState Snapshot() => new(X, Y, Size, Life, HueShift);
    }

    private sealed class Ripple { public float Radius, Alpha, Speed; }

    private sealed class RainColumn
    {
        public float Y, Speed, Brightness, Level;
        public RainState Snapshot() => new(Y, Level, Brightness);
    }

    // ── Render-thread drawing: reads only the immutable Frame ───────────────

    private sealed class DrawOperation : ICustomDrawOperation
    {
        private readonly Frame _f;
        public Rect Bounds { get; }

        public DrawOperation(Rect bounds, Frame frame)
        {
            Bounds = bounds;
            _f = frame;
        }

        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (!context.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var leaseFeature) || leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            float w = (float)Bounds.Width, h = (float)Bounds.Height;

            canvas.Save();
            try
            {
                canvas.ClipRect(new SKRect(0, 0, w, h));
                switch (_f.Preset)
                {
                    case VisualizerPreset.SpectrumBars: DrawSpectrumBars(canvas, w, h); break;
                    case VisualizerPreset.CircularWave: DrawCircularWave(canvas, w, h); break;
                    case VisualizerPreset.NeonParticles: DrawParticles(canvas, w, h); break;
                    case VisualizerPreset.Oscilloscope: DrawOscilloscope(canvas, w, h); break;
                    case VisualizerPreset.StarBurst: DrawStarBurst(canvas, w, h); break;
                    case VisualizerPreset.PlasmaMesh: DrawPlasma(canvas, w, h); break;
                    case VisualizerPreset.AuroraBands: DrawAurora(canvas, w, h); break;
                    case VisualizerPreset.Waterfall: DrawWaterfall(canvas, w, h); break;
                    case VisualizerPreset.PhaseScope: DrawPhaseScope(canvas, w, h); break;
                    case VisualizerPreset.DigitalRain: DrawRain(canvas, w, h); break;
                    case VisualizerPreset.RipplePool: DrawRipples(canvas, w, h); break;
                    default: DrawAmbient(canvas, w, h); break;
                }
            }
            catch (Exception ex)
            {
                // Never let a bad frame escape the render thread.
                Serilog.Log.Warning(ex, "OrbitVisualizerCanvas: {Preset} draw failed — skipping frame", _f.Preset);
            }
            finally
            {
                canvas.Restore();
            }
        }

        private SKColor Hsv(float hueOffset, float s, float v, float alpha = 1f) =>
            SKColor.FromHsv(((_f.Hue + hueOffset) % 360 + 360) % 360, s * 100, v * 100, (byte)Math.Clamp(alpha * 255, 0, 255));

        private float Level(float t)
        {
            // Band level at normalised position t (0 = bass, 1 = treble), linearly interpolated.
            var levels = _f.Levels;
            float x = Math.Clamp(t, 0, 1) * (levels.Length - 1);
            int i = (int)x;
            float frac = x - i;
            return i >= levels.Length - 1 ? levels[^1] : levels[i] * (1 - frac) + levels[i + 1] * frac;
        }

        // 1. Spectrum bars: log bands, mirrored reflection, peak caps, glow.
        private void DrawSpectrumBars(SKCanvas canvas, float w, float h)
        {
            int bars = Math.Clamp((int)(w / 14), 32, _f.Levels.Length);
            float gap = Math.Max(2, w / bars * 0.22f);
            float barW = w / bars - gap;
            float baseY = h * 0.68f;
            float maxH = h * 0.55f;
            float lift = 1f + _f.Beat * 0.12f;

            using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, ImageFilter = SKImageFilter.CreateBlur(10, 10) };
            using var bar = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var cap = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var mirror = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            for (int i = 0; i < bars; i++)
            {
                float t = (float)i / (bars - 1);
                float v = Math.Min(1, Level(t) * lift);
                float x = i * (barW + gap) + gap / 2;
                float bh = Math.Max(2, v * maxH);
                var rect = new SKRect(x, baseY - bh, x + barW, baseY);
                var top = Hsv(t * 90 + v * 30, 0.75f, 1f);
                var bottom = Hsv(t * 90, 0.9f, 0.55f);

                using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom), new[] { top, bottom }, null, SKShaderTileMode.Clamp))
                {
                    bar.Shader = shader;
                    glow.Color = top.WithAlpha((byte)(90 * v));
                    canvas.DrawRoundRect(rect, barW / 2, barW / 2, glow);
                    canvas.DrawRoundRect(rect, barW / 2, barW / 2, bar);
                    bar.Shader = null;
                }

                using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, baseY), new SKPoint(0, baseY + bh * 0.45f),
                           new[] { bottom.WithAlpha(90), bottom.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
                {
                    mirror.Shader = shader;
                    canvas.DrawRoundRect(new SKRect(x, baseY + 3, x + barW, baseY + 3 + bh * 0.45f), barW / 2, barW / 2, mirror);
                    mirror.Shader = null;
                }

                float peakY = baseY - Math.Max(2, Math.Min(1, _f.Peaks[Math.Min(_f.Peaks.Length - 1, (int)(t * (_f.Peaks.Length - 1)))] * lift) * maxH) - 6;
                cap.Color = top.WithAlpha(220);
                canvas.DrawRoundRect(new SKRect(x, peakY, x + barW, peakY + 3), 1.5f, 1.5f, cap);
            }
        }

        // 2. Circular: radial bars around the centre (where the artwork sits) + a smooth outline.
        private void DrawCircularWave(SKCanvas canvas, float w, float h)
        {
            float cx = w / 2, cy = h / 2;
            float radius = Math.Min(w, h) * 0.2f * (1 + _f.Beat * 0.05f);
            float reach = Math.Min(w, h) * 0.24f;
            const int Spokes = 180;

            using var spoke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = Math.Max(2, radius * 2 * MathF.PI / Spokes * 0.55f) };
            using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = Hsv(40, 0.5f, 1f, 0.8f), ImageFilter = SKImageFilter.CreateBlur(2, 2) };
            using var path = new SKPath();

            float rotation = (float)(_f.Time * 0.15);
            for (int i = 0; i <= Spokes; i++)
            {
                // Mirror the spectrum around the circle so bass sits at the top and bottom.
                int k = i % Spokes;
                float t = k < Spokes / 2 ? (float)k / (Spokes / 2) : (float)(Spokes - k) / (Spokes / 2);
                float v = Level(t);
                float angle = rotation + k * 2 * MathF.PI / Spokes - MathF.PI / 2;
                float r0 = radius + 4, r1 = radius + 4 + v * reach;
                float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
                if (i < Spokes)
                {
                    spoke.Color = Hsv(t * 120, 0.8f, 1f, 0.35f + v * 0.65f);
                    canvas.DrawLine(cx + cos * r0, cy + sin * r0, cx + cos * r1, cy + sin * r1, spoke);
                }
                float ro = r1 + 6;
                if (i == 0) path.MoveTo(cx + cos * ro, cy + sin * ro); else path.LineTo(cx + cos * ro, cy + sin * ro);
            }
            path.Close();
            canvas.DrawPath(path, outline);

            using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Hsv(0, 0.6f, 1f, 0.25f + _f.Beat * 0.5f), ImageFilter = SKImageFilter.CreateBlur(6, 6) };
            canvas.DrawCircle(cx, cy, radius, halo);
        }

        // 3. Particles: drift field plus bursts from the centre on each beat.
        private void DrawParticles(SKCanvas canvas, float w, float h)
        {
            if (_f.Particles is null) return;
            using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, ImageFilter = SKImageFilter.CreateBlur(6, 6) };
            using var dot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            float scale = 1 + _f.Bass * 1.2f;
            foreach (var p in _f.Particles)
            {
                float a = Math.Clamp(p.Life, 0, 1) * (0.35f + _f.Overall * 0.9f);
                var c = Hsv(p.HueShift, 0.7f, 1f, a);
                glow.Color = c.WithAlpha((byte)(a * 140));
                dot.Color = c;
                canvas.DrawCircle(p.X * w, p.Y * h, p.Size * scale * 2.4f, glow);
                canvas.DrawCircle(p.X * w, p.Y * h, p.Size * scale * 0.8f, dot);
            }
        }

        // 4. Oscilloscope: the real signal, with fading trails of the previous blocks.
        private void DrawOscilloscope(SKCanvas canvas, float w, float h)
        {
            float cy = h / 2;
            using var grid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Hsv(0, 0.3f, 1f, 0.12f) };
            canvas.DrawLine(0, cy, w, cy, grid);
            if (_f.Scope is not { Length: > 0 } blocks) return;

            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeJoin = SKStrokeJoin.Round };
            using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7f, ImageFilter = SKImageFilter.CreateBlur(5, 5) };
            for (int b = 0; b < blocks.Length; b++)
            {
                var samples = blocks[b];
                float age = (float)(b + 1) / blocks.Length; // oldest first → newest last
                // Trigger on a rising zero crossing so the trace stands still instead of scrolling.
                int start = 0;
                for (int i = 1; i < samples.Length / 2; i++)
                    if (samples[i - 1] < 0 && samples[i] >= 0) { start = i; break; }
                int span = Math.Min(samples.Length - start, samples.Length / 2);
                using var path = new SKPath();
                for (int i = 0; i < span; i++)
                {
                    float x = (float)i / (span - 1) * w;
                    float y = cy - Math.Clamp(samples[start + i] * 1.6f, -1, 1) * h * 0.38f;
                    if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }
                line.Color = Hsv(age * 40, 0.7f, 1f, age * age);
                if (b == blocks.Length - 1)
                {
                    glow.Color = Hsv(20, 0.8f, 1f, 0.5f);
                    canvas.DrawPath(path, glow);
                }
                canvas.DrawPath(path, line);
            }
        }

        // 5. Star burst: long radial spokes, beat-punched core.
        private void DrawStarBurst(SKCanvas canvas, float w, float h)
        {
            float cx = w / 2, cy = h / 2;
            float inner = Math.Min(w, h) * 0.06f;
            float outer = Math.Max(w, h) * 0.55f;
            const int Spokes = 144;
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 2 };
            float rot = (float)(_f.Time * 0.12);
            for (int i = 0; i < Spokes; i++)
            {
                float t = i < Spokes / 2 ? (float)i / (Spokes / 2) : (float)(Spokes - i) / (Spokes / 2);
                float v = Level(t);
                if (v < 0.03f) continue;
                float angle = rot + i * 2 * MathF.PI / Spokes;
                float r = inner + v * v * (outer - inner);
                float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
                var head = Hsv(t * 140, 0.8f, 1f, 0.2f + v * 0.8f);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(cx + cos * inner, cy + sin * inner), new SKPoint(cx + cos * r, cy + sin * r),
                    new[] { head.WithAlpha(0), head }, null, SKShaderTileMode.Clamp);
                paint.Shader = shader;
                canvas.DrawLine(cx + cos * inner, cy + sin * inner, cx + cos * r, cy + sin * r, paint);
                paint.Shader = null;
            }
            using var core = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Hsv(0, 0.4f, 1f, 0.35f + _f.Beat * 0.6f), ImageFilter = SKImageFilter.CreateBlur(24, 24) };
            canvas.DrawCircle(cx, cy, inner * (1.6f + _f.Beat * 1.4f + _f.Bass), core);
        }

        // 6. Plasma: soft interference field, bass-warped.
        private void DrawPlasma(SKCanvas canvas, float w, float h)
        {
            float t = (float)_f.Time;
            float cell = Math.Max(18, Math.Min(w, h) / 36);
            float warp = 6 + _f.Bass * 10;
            using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            for (float y = 0; y < h; y += cell)
                for (float x = 0; x < w; x += cell)
                {
                    float nx = x / w, ny = y / h;
                    float v = MathF.Sin(nx * warp + t) + MathF.Sin(ny * (warp * 0.8f) + t * 1.3f)
                              + MathF.Sin((nx + ny) * warp * 0.7f + t * 0.7f)
                              + MathF.Sin(MathF.Sqrt((nx - 0.5f) * (nx - 0.5f) + (ny - 0.5f) * (ny - 0.5f)) * 14 - t * 2);
                    float n = (v + 4) / 8;
                    paint.Color = Hsv(n * 120 - 60, 0.75f, 0.35f + n * 0.5f + _f.Beat * 0.1f, 0.55f + _f.Overall * 0.4f);
                    canvas.DrawRect(x, y, cell + 1, cell + 1, paint);
                }
            using var soften = new SKPaint { Color = new SKColor(0, 0, 0, 60) };
            canvas.DrawRect(0, 0, w, h, soften);
        }

        // 7. Aurora: flowing curtains whose height follows bass / mid / treble.
        private void DrawAurora(SKCanvas canvas, float w, float h)
        {
            float t = (float)_f.Time;
            float[] drive = { _f.Bass, _f.Mid, _f.Treble, _f.Mid, _f.Bass };
            for (int b = 0; b < drive.Length; b++)
            {
                float amp = h * (0.08f + drive[b] * 0.22f);
                float baseY = h * (0.3f + b * 0.1f);
                using var path = new SKPath();
                path.MoveTo(0, h);
                for (float x = 0; x <= w; x += 12)
                {
                    float nx = x / w;
                    float y = baseY + MathF.Sin(nx * 5 + t * (0.6f + b * 0.15f) + b) * amp + MathF.Sin(nx * 11 - t * 0.8f) * amp * 0.35f;
                    path.LineTo(x, y);
                }
                path.LineTo(w, h);
                path.Close();
                var c = Hsv(b * 35, 0.7f, 1f, 0.18f + drive[b] * 0.35f);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, baseY - amp), new SKPoint(0, h), new[] { c, c.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { IsAntialias = true, Shader = shader, BlendMode = SKBlendMode.Plus };
                canvas.DrawPath(path, paint);
            }
        }

        // 8. Waterfall: scrolling spectrogram, newest at the bottom.
        private void DrawWaterfall(SKCanvas canvas, float w, float h)
        {
            if (_f.Waterfall is null) return;
            int rows = _f.Waterfall.Length;
            float rowH = h / rows;
            using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            for (int r = 0; r < rows; r++)
            {
                var row = _f.Waterfall[r];
                float y = h - (r + 1) * rowH;
                float fade = 1f - (float)r / rows * 0.6f;
                float binW = w / row.Length;
                for (int i = 0; i < row.Length; i++)
                {
                    float v = row[i];
                    if (v < 0.04f) continue;
                    paint.Color = Hsv(v * 140 - 40, 0.85f, 0.4f + v * 0.6f, v * fade);
                    canvas.DrawRect(i * binW, y, binW + 0.5f, rowH + 0.5f, paint);
                }
            }
        }

        // 9. Phase scope: phase portrait of the signal (x = s[t], y = s[t+k]) with trails.
        private void DrawPhaseScope(SKCanvas canvas, float w, float h)
        {
            float cx = w / 2, cy = h / 2, size = Math.Min(w, h) * 0.42f;
            using var grid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Hsv(0, 0.3f, 1f, 0.12f) };
            canvas.DrawCircle(cx, cy, size, grid);
            canvas.DrawLine(cx - size, cy - size, cx + size, cy + size, grid);
            canvas.DrawLine(cx - size, cy + size, cx + size, cy - size, grid);
            if (_f.Scope is not { Length: > 0 } blocks) return;

            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f };
            const int Lag = 12;
            for (int b = 0; b < blocks.Length; b++)
            {
                var s = blocks[b];
                float age = (float)(b + 1) / blocks.Length;
                line.Color = Hsv(age * 60, 0.7f, 1f, age * 0.8f);
                using var path = new SKPath();
                int step = Math.Max(1, s.Length / 700);
                for (int i = 0; i + Lag < s.Length; i += step)
                {
                    float x = cx + Math.Clamp(s[i] * 2f, -1, 1) * size;
                    float y = cy - Math.Clamp(s[i + Lag] * 2f, -1, 1) * size;
                    if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }
                canvas.DrawPath(path, line);
            }
        }

        // 10. Digital rain: columns fall faster where their band is loud.
        private void DrawRain(SKCanvas canvas, float w, float h)
        {
            if (_f.Rain is null || _f.Rain.Length == 0) return;
            float colW = w / _f.Rain.Length;
            float cellH = colW;
            using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            for (int i = 0; i < _f.Rain.Length; i++)
            {
                var c = _f.Rain[i];
                float headY = c.Y * h;
                int trail = 10 + (int)(c.Level * 14);
                for (int k = 0; k < trail; k++)
                {
                    float y = headY - k * cellH;
                    if (y < -cellH || y > h) continue;
                    float a = (1f - (float)k / trail) * c.Brightness * (0.35f + c.Level);
                    paint.Color = k == 0 ? Hsv(0, 0.15f, 1f, Math.Min(1, 0.6f + c.Level)) : Hsv(0, 0.75f, 0.9f, a);
                    canvas.DrawRect(i * colW + 2, y, colW - 4, cellH - 3, paint);
                }
            }
        }

        // 11. Ripples: a ring per beat, bass-sized.
        private void DrawRipples(SKCanvas canvas, float w, float h)
        {
            float cx = w / 2, cy = h / 2, maxR = Math.Max(w, h) * 0.75f;
            using (var shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), maxR * 0.6f, new[] { Hsv(0, 0.6f, 0.6f, 0.18f + _f.Bass * 0.2f), SKColors.Transparent }, null, SKShaderTileMode.Clamp))
            using (var pool = new SKPaint { IsAntialias = true, Shader = shader })
                canvas.DrawCircle(cx, cy, maxR * 0.6f, pool);

            if (_f.Ripples is null) return;
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
            foreach (var (radius, alpha) in _f.Ripples)
            {
                ring.StrokeWidth = 1.5f + alpha * 5;
                ring.Color = Hsv((1 - alpha) * 60, 0.7f, 1f, alpha * 0.9f);
                canvas.DrawCircle(cx, cy, radius * maxR, ring);
            }
        }

        // 12. Ambient: slow breathing glow, gently tied to overall level.
        private void DrawAmbient(SKCanvas canvas, float w, float h)
        {
            float t = (float)_f.Time;
            float breathe = (MathF.Sin(t * 0.5f) + 1) / 2;
            float cx = w / 2, cy = h / 2, maxR = Math.Max(w, h) * 0.55f;
            float r = maxR * (0.55f + breathe * 0.25f + _f.Overall * 0.2f);
            using (var shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), r,
                       new[] { Hsv(0, 0.45f, 0.9f, 0.35f), Hsv(30, 0.6f, 0.5f, 0.12f), SKColors.Transparent }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp))
            using (var glow = new SKPaint { IsAntialias = true, Shader = shader })
                canvas.DrawCircle(cx, cy, r, glow);

            float ox = cx + MathF.Cos(t * 0.21f) * maxR * 0.35f, oy = cy + MathF.Sin(t * 0.17f) * maxR * 0.25f;
            using (var shader = SKShader.CreateRadialGradient(new SKPoint(ox, oy), r * 0.5f, new[] { Hsv(60, 0.5f, 1f, 0.22f), SKColors.Transparent }, null, SKShaderTileMode.Clamp))
            using (var orb = new SKPaint { IsAntialias = true, Shader = shader, BlendMode = SKBlendMode.Plus })
                canvas.DrawCircle(ox, oy, r * 0.5f, orb);
        }
    }
}
