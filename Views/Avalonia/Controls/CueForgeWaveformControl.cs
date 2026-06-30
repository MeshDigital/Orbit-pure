using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// RGB waveform renderer with interactive cue/loop editing.
/// Simplified for Avalonia compatibility.
/// </summary>
public class CueForgeWaveformControl : Control
{
    // ── Properties ──────────────────────────────────────────────────────

    public static readonly StyledProperty<ObservableCollection<OrbitCue>?> CuesProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, ObservableCollection<OrbitCue>?>(
            nameof(Cues), null);

    public ObservableCollection<OrbitCue>? Cues
    {
        get => GetValue(CuesProperty);
        set => SetValue(CuesProperty, value);
    }

    public static readonly StyledProperty<double> CurrentPlayPositionProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(
            nameof(CurrentPlayPosition), 0.0);

    public double CurrentPlayPosition
    {
        get => GetValue(CurrentPlayPositionProperty);
        set => SetValue(CurrentPlayPositionProperty, value);
    }

    public static readonly StyledProperty<double> TrackDurationProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(
            nameof(TrackDuration), 300.0);

    public double TrackDuration
    {
        get => GetValue(TrackDurationProperty);
        set => SetValue(TrackDurationProperty, value);
    }

    public static readonly StyledProperty<int> BpmProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, int>(
            nameof(Bpm), 120);

    public int Bpm
    {
        get => GetValue(BpmProperty);
        set => SetValue(BpmProperty, value);
    }

    public static readonly StyledProperty<bool> SnapToGridProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, bool>(
            nameof(SnapToGrid), true);

    public bool SnapToGrid
    {
        get => GetValue(SnapToGridProperty);
        set => SetValue(SnapToGridProperty, value);
    }

    public static readonly StyledProperty<string> QuantizeBeatStringProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, string>(
            nameof(QuantizeBeatString), "16 beats");

    public string QuantizeBeatString
    {
        get => GetValue(QuantizeBeatStringProperty);
        set => SetValue(QuantizeBeatStringProperty, value);
    }

    public static readonly StyledProperty<float[]?> EnergyCurveProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, float[]?>(
            nameof(EnergyCurve), null);

    public float[]? EnergyCurve
    {
        get => GetValue(EnergyCurveProperty);
        set => SetValue(EnergyCurveProperty, value);
    }

    public static readonly StyledProperty<float[]?> VocalDensityCurveProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, float[]?>(
            nameof(VocalDensityCurve), null);

    public float[]? VocalDensityCurve
    {
        get => GetValue(VocalDensityCurveProperty);
        set => SetValue(VocalDensityCurveProperty, value);
    }

    /// <summary>Parse quantize string to beat count.</summary>
    private int ParseQuantizeBeatCount(string quantizeStr)
    {
        return quantizeStr switch
        {
            "Off" => 0,
            "1 beat" => 1,
            "2 beats" => 2,
            "4 beats" => 4,
            "8 beats" => 8,
            "16 beats" => 16,
            "32 beats" => 32,
            "64 beats (16 bars)" => 64,
            "128 beats (32 bars)" => 128,
            _ => 16
        };
    }

    // ── State ────────────────────────────────────────────────────────────

    private float[]? _waveformCache;
    private OrbitCue? _draggedCue;
    private bool _isDraggingLoopStart;
    private bool _isDraggingLoopEnd;
    private double _zoomLevel = 1.0;
    private double _scrollOffset = 0.0;
    private const double SnapThreshold = 20.0;

    public CueForgeWaveformControl()
    {
        InitializeWaveformCache();
    }

    // ── Rendering ────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0A0E27")), bounds);

        DrawBeatGrid(context, bounds);
        DrawWaveform(context, bounds);
        DrawEnergyOverlay(context, bounds);
        DrawVocalOverlay(context, bounds);
        DrawLoopBlocks(context, bounds);
        DrawCueMarkers(context, bounds);
        DrawPlayhead(context, bounds);
    }

    private void DrawBeatGrid(DrawingContext context, Rect bounds)
    {
        if (Bpm <= 0) return;

        double beatDurationSeconds = 60.0 / Bpm;
        double barDurationSeconds = beatDurationSeconds * 4;

        for (double t = 0; t < TrackDuration; t += barDurationSeconds)
        {
            double x = TimeToPixel(t, bounds);
            if (x < 0 || x > bounds.Width) continue;

            var pen = new Pen(new SolidColorBrush(Color.Parse("#222222")));
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));

            for (int i = 1; i < 4; i++)
            {
                double beatX = TimeToPixel(t + beatDurationSeconds * i, bounds);
                if (beatX >= 0 && beatX <= bounds.Width)
                {
                    var beatPen = new Pen(new SolidColorBrush(Color.Parse("#1A1A1A")));
                    context.DrawLine(beatPen, new Point(beatX, bounds.Top), new Point(beatX, bounds.Bottom));
                }
            }
        }
    }

    private void DrawWaveform(DrawingContext context, Rect bounds)
    {
        if (_waveformCache == null || _waveformCache.Length == 0) return;

        int width = (int)bounds.Width;
        for (int x = 0; x < width - 1; x++)
        {
            int sampleIdx = x * 3;
            if (sampleIdx >= _waveformCache.Length - 3) break;

            byte r = (byte)(_waveformCache[sampleIdx] * 255);
            byte g = (byte)(_waveformCache[sampleIdx + 1] * 255);
            byte b = (byte)(_waveformCache[sampleIdx + 2] * 255);

            var color = Color.FromArgb(255, r, g, b);
            var pen = new Pen(new SolidColorBrush(color));

            double y1 = bounds.Height * 0.5 - (r + g + b) / 3.0 / 255.0 * bounds.Height * 0.4;
            double y2 = bounds.Height * 0.5 + (r + g + b) / 3.0 / 255.0 * bounds.Height * 0.4;

            context.DrawLine(pen, new Point(x, y1), new Point(x, y2));
        }
    }

    private void DrawEnergyOverlay(DrawingContext context, Rect bounds)
    {
        if (EnergyCurve == null || EnergyCurve.Length < 2) return;

        int width = (int)bounds.Width;
        var energyPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 100, 200, 100)), 2.0);

        for (int x = 0; x < width - 1; x++)
        {
            int idx1 = Math.Min(x, EnergyCurve.Length - 1);
            int idx2 = Math.Min(x + 1, EnergyCurve.Length - 1);

            float energy1 = EnergyCurve[idx1];
            float energy2 = EnergyCurve[idx2];

            double y1 = bounds.Bottom - energy1 * bounds.Height * 0.3 - bounds.Height * 0.1;
            double y2 = bounds.Bottom - energy2 * bounds.Height * 0.3 - bounds.Height * 0.1;

            context.DrawLine(energyPen, new Point(x, y1), new Point(x + 1, y2));
        }
    }

    private void DrawVocalOverlay(DrawingContext context, Rect bounds)
    {
        if (VocalDensityCurve == null || VocalDensityCurve.Length < 2) return;

        int width = (int)bounds.Width;

        // Paint vertical strips where vocals are detected (>0.3 threshold = vocal presence)
        for (int x = 0; x < width; x++)
        {
            int idx = Math.Min(x, VocalDensityCurve.Length - 1);
            float vocalDensity = VocalDensityCurve[idx];

            if (vocalDensity > 0.3f)
            {
                // Higher vocal density = deeper red tint, higher opacity
                byte alpha = (byte)(vocalDensity * 60); // 0-60 opacity
                var vocalBrush = new SolidColorBrush(Color.FromArgb(alpha, 255, 80, 80)); // Red-tinted
                context.FillRectangle(vocalBrush, new Rect(x, bounds.Top, 1, bounds.Height));
            }
        }
    }

    private void DrawLoopBlocks(DrawingContext context, Rect bounds)
    {
        if (Cues == null) return;

        foreach (var loop in Cues.Where(c => c.IsLoop))
        {
            double x1 = TimeToPixel(loop.Timestamp, bounds);
            double x2 = TimeToPixel(loop.LoopEndSeconds, bounds);

            if (x1 < 0 || x1 > bounds.Width) continue;

            var color = ParseColor(loop.Color, 0x00FF88);
            var brush = new SolidColorBrush(color) { Opacity = 0.2 };
            var rect = new Rect(x1, bounds.Top, Math.Max(1, x2 - x1), bounds.Height);

            context.FillRectangle(brush, rect);

            var outlineBrush = new SolidColorBrush(color);
            var outlinePen = new Pen(outlineBrush);
            context.DrawRectangle(outlinePen, rect);
        }
    }

    private void DrawCueMarkers(DrawingContext context, Rect bounds)
    {
        if (Cues == null) return;

        foreach (var cue in Cues.Where(c => !c.IsLoop))
        {
            double x = TimeToPixel(cue.Timestamp, bounds);
            if (x < 0 || x > bounds.Width) continue;

            var color = ParseColor(cue.Color, 0xFFFF00);
            var pen = new Pen(new SolidColorBrush(color));

            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));

            var flagBrush = new SolidColorBrush(color);
            context.FillRectangle(flagBrush, new Rect(x - 4, bounds.Top, 8, 12));
        }
    }

    private void DrawPlayhead(DrawingContext context, Rect bounds)
    {
        double x = TimeToPixel(CurrentPlayPosition, bounds);
        if (x < 0 || x > bounds.Width) return;

        var pen = new Pen(new SolidColorBrush(Color.Parse("#00D7FF")));
        context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
    }

    // ── Mouse Interaction ────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);

        if (Cues != null)
        {
            var activeLoop = Cues.FirstOrDefault(c => c.IsLoop);
            if (activeLoop != null)
            {
                double loopX1 = TimeToPixel(activeLoop.Timestamp, bounds);
                double loopX2 = TimeToPixel(activeLoop.LoopEndSeconds, bounds);

                if (Math.Abs(point.X - loopX1) < SnapThreshold)
                {
                    _isDraggingLoopStart = true;
                    e.Pointer.Capture(this);
                    return;
                }

                if (Math.Abs(point.X - loopX2) < SnapThreshold)
                {
                    _isDraggingLoopEnd = true;
                    e.Pointer.Capture(this);
                    return;
                }
            }

            foreach (var cue in Cues.Where(c => !c.IsLoop))
            {
                double cueX = TimeToPixel(cue.Timestamp, bounds);
                if (Math.Abs(point.X - cueX) < SnapThreshold)
                {
                    _draggedCue = cue;
                    e.Pointer.Capture(this);
                    return;
                }
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);

        if (_draggedCue != null)
        {
            double newTime = PixelToTime(point.X, bounds);
            newTime = ApplySnapping(newTime);
            _draggedCue.Timestamp = Math.Clamp(newTime, 0, TrackDuration);
            InvalidateVisual();
        }

        if (_isDraggingLoopStart || _isDraggingLoopEnd)
        {
            var activeLoop = Cues?.FirstOrDefault(c => c.IsLoop);
            if (activeLoop != null)
            {
                double newTime = PixelToTime(point.X, bounds);
                newTime = ApplySnapping(newTime);
                newTime = Math.Clamp(newTime, 0, TrackDuration);

                if (_isDraggingLoopStart)
                    activeLoop.Timestamp = newTime;
                else
                    activeLoop.LoopEndSeconds = newTime;

                InvalidateVisual();
            }
        }
    }

    private double ApplySnapping(double timeSeconds)
    {
        if (!SnapToGrid || Bpm <= 0) return timeSeconds;

        double beatDurationSeconds = 60.0 / Bpm;
        int quantizeBeats = ParseQuantizeBeatCount(QuantizeBeatString);
        double snapDistance;

        if (quantizeBeats > 0)
        {
            double gridDurationSeconds = beatDurationSeconds * quantizeBeats;
            double nearestGrid = Math.Round(timeSeconds / gridDurationSeconds) * gridDurationSeconds;
            snapDistance = Math.Abs(timeSeconds - nearestGrid);
            if (snapDistance < 0.05)
                return nearestGrid;
        }

        double nearestBeat = Math.Round(timeSeconds / beatDurationSeconds) * beatDurationSeconds;
        snapDistance = Math.Abs(timeSeconds - nearestBeat);
        return snapDistance < 0.05 ? nearestBeat : timeSeconds;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _draggedCue = null;
        _isDraggingLoopStart = false;
        _isDraggingLoopEnd = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CuesProperty ||
            change.Property == CurrentPlayPositionProperty ||
            change.Property == BpmProperty ||
            change.Property == SnapToGridProperty ||
            change.Property == QuantizeBeatStringProperty ||
            change.Property == EnergyCurveProperty ||
            change.Property == VocalDensityCurveProperty)
        {
            InvalidateVisual();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void InitializeWaveformCache()
    {
        int samples = (int)(TrackDuration * 100);
        _waveformCache = new float[samples * 3];

        Random rng = new Random(42);
        for (int i = 0; i < samples; i++)
        {
            float freq = (float)i / samples;
            _waveformCache[i * 3] = Math.Max(0, 1.0f - freq) * (float)rng.NextDouble();
            _waveformCache[i * 3 + 1] = 0.7f * (float)rng.NextDouble();
            _waveformCache[i * 3 + 2] = Math.Min(1.0f, freq) * (float)rng.NextDouble();
        }
    }

    private double TimeToPixel(double seconds, Rect bounds)
    {
        return (seconds / (TrackDuration / _zoomLevel) - _scrollOffset) * bounds.Width;
    }

    private double PixelToTime(double pixelX, Rect bounds)
    {
        return (pixelX / bounds.Width + _scrollOffset) * (TrackDuration / _zoomLevel);
    }

    private Color ParseColor(string hexColor, uint fallback)
    {
        try
        {
            return Color.Parse(hexColor);
        }
        catch
        {
            byte r = (byte)((fallback >> 16) & 0xFF);
            byte g = (byte)((fallback >> 8) & 0xFF);
            byte b = (byte)(fallback & 0xFF);
            return Color.FromArgb(255, r, g, b);
        }
    }
}
