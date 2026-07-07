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
/// Full-canvas RGB waveform renderer with interactive cue/loop overlays,
/// phrase-map strip, energy curve, onset density curve, and vocal overlay.
///
/// Rendering layer order (back → front):
///   1. Background fill
///   2. Phrase-map strip (bottom 12px colored bands by section type)
///   3. Beat grid (bar + beat lines)
///   4. Onset density fill (translucent fill showing activity density)
///   5. RGB waveform (Low=Red, Mid=Green, High=Blue — Rekordbox convention)
///   6. Energy curve overlay (bright green line)
///   7. Vocal density overlay (red tint strips)
///   8. Loop blocks (translucent shaded regions with edge handles)
///   9. Cue markers (vertical colored lines + flag triangles)
///  10. Playhead (bright cyan)
/// </summary>
public class CueForgeWaveformControl : Control
{
    // ── Styled Properties ──────────────────────────────────────────────────

    public static readonly StyledProperty<ObservableCollection<OrbitCue>?> CuesProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, ObservableCollection<OrbitCue>?>(nameof(Cues));
    public ObservableCollection<OrbitCue>? Cues { get => GetValue(CuesProperty); set => SetValue(CuesProperty, value); }

    public static readonly StyledProperty<ObservableCollection<PhraseSegment>?> PhraseSegmentsProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, ObservableCollection<PhraseSegment>?>(nameof(PhraseSegments));
    public ObservableCollection<PhraseSegment>? PhraseSegments { get => GetValue(PhraseSegmentsProperty); set => SetValue(PhraseSegmentsProperty, value); }

    public static readonly StyledProperty<double> CurrentPlayPositionProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(CurrentPlayPosition));
    public double CurrentPlayPosition { get => GetValue(CurrentPlayPositionProperty); set => SetValue(CurrentPlayPositionProperty, value); }

    public static readonly StyledProperty<double> TrackDurationProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(TrackDuration), 300.0);
    public double TrackDuration { get => GetValue(TrackDurationProperty); set => SetValue(TrackDurationProperty, value); }

    public static readonly StyledProperty<int> BpmProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, int>(nameof(Bpm), 120);
    public int Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }

    public static readonly StyledProperty<bool> SnapToGridProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, bool>(nameof(SnapToGrid), true);
    public bool SnapToGrid { get => GetValue(SnapToGridProperty); set => SetValue(SnapToGridProperty, value); }

    public static readonly StyledProperty<string> QuantizeBeatStringProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, string>(nameof(QuantizeBeatString), "16 beats");
    public string QuantizeBeatString { get => GetValue(QuantizeBeatStringProperty); set => SetValue(QuantizeBeatStringProperty, value); }

    // Waveform data channels (0-255 per sample)
    public static readonly StyledProperty<byte[]?> WaveformLowProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, byte[]?>(nameof(WaveformLow));
    public byte[]? WaveformLow { get => GetValue(WaveformLowProperty); set => SetValue(WaveformLowProperty, value); }

    public static readonly StyledProperty<byte[]?> WaveformMidProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, byte[]?>(nameof(WaveformMid));
    public byte[]? WaveformMid { get => GetValue(WaveformMidProperty); set => SetValue(WaveformMidProperty, value); }

    public static readonly StyledProperty<byte[]?> WaveformHighProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, byte[]?>(nameof(WaveformHigh));
    public byte[]? WaveformHigh { get => GetValue(WaveformHighProperty); set => SetValue(WaveformHighProperty, value); }

    public static readonly StyledProperty<float[]?> EnergyCurveProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, float[]?>(nameof(EnergyCurve));
    public float[]? EnergyCurve { get => GetValue(EnergyCurveProperty); set => SetValue(EnergyCurveProperty, value); }

    public static readonly StyledProperty<float[]?> VocalDensityCurveProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, float[]?>(nameof(VocalDensityCurve));
    public float[]? VocalDensityCurve { get => GetValue(VocalDensityCurveProperty); set => SetValue(VocalDensityCurveProperty, value); }

    public static readonly StyledProperty<float[]?> OnsetDensityCurveProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, float[]?>(nameof(OnsetDensityCurve));
    public float[]? OnsetDensityCurve { get => GetValue(OnsetDensityCurveProperty); set => SetValue(OnsetDensityCurveProperty, value); }

    // ── Drag State ─────────────────────────────────────────────────────────

    private OrbitCue? _draggedCue;
    private bool _isDraggingLoopStart;
    private bool _isDraggingLoopEnd;
    private double _zoomLevel = 1.0;
    private double _scrollOffset = 0.0;
    private const double HitRadius = 18.0;
    private const double PhraseMapHeight = 12.0;

    // ── Rendering ──────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var b = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (b.Width < 1 || b.Height < 1) return;

        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#0A0E27")), b);

        var waveformBounds = new Rect(b.X, b.Y, b.Width, b.Height - PhraseMapHeight);

        DrawPhraseMap(ctx, b);
        DrawBeatGrid(ctx, waveformBounds);
        DrawOnsetDensityFill(ctx, waveformBounds);
        DrawRgbWaveform(ctx, waveformBounds);
        DrawEnergyOverlay(ctx, waveformBounds);
        DrawVocalOverlay(ctx, waveformBounds);
        DrawDoubleDropZone(ctx, waveformBounds);
        DrawLoopBlocks(ctx, waveformBounds);
        DrawCueMarkers(ctx, waveformBounds);
        DrawPlayhead(ctx, waveformBounds);
    }

    // ── Layer Drawers ──────────────────────────────────────────────────────

    private void DrawPhraseMap(DrawingContext ctx, Rect fullBounds)
    {
        var stripBounds = new Rect(0, fullBounds.Height - PhraseMapHeight, fullBounds.Width, PhraseMapHeight);
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#111111")), stripBounds);

        if (PhraseSegments is null || PhraseSegments.Count == 0) return;

        foreach (var seg in PhraseSegments)
        {
            double x1 = TimeToPixel(seg.Start, fullBounds);
            double x2 = TimeToPixel(seg.Start + seg.Duration, fullBounds);
            if (x2 <= 0 || x1 >= fullBounds.Width) continue;
            x1 = Math.Max(0, x1); x2 = Math.Min(fullBounds.Width, x2);

            var color = GetPhraseColor(seg.Label);
            ctx.FillRectangle(new SolidColorBrush(color), new Rect(x1, stripBounds.Top, x2 - x1, PhraseMapHeight));
        }

        // Divider line
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#333333"))),
            new Point(0, stripBounds.Top), new Point(fullBounds.Width, stripBounds.Top));
    }

    private void DrawBeatGrid(DrawingContext ctx, Rect b)
    {
        if (Bpm <= 0 || TrackDuration <= 0) return;
        double beatSec = 60.0 / Bpm;
        double barSec = beatSec * 4;
        var barPen = new Pen(new SolidColorBrush(Color.Parse("#252530")));
        var beatPen = new Pen(new SolidColorBrush(Color.Parse("#161620")));

        for (double t = 0; t < TrackDuration; t += barSec)
        {
            double x = TimeToPixel(t, b);
            if (x < 0 || x > b.Width) continue;
            ctx.DrawLine(barPen, new Point(x, b.Top), new Point(x, b.Bottom));
            for (int i = 1; i < 4; i++)
            {
                double bx = TimeToPixel(t + beatSec * i, b);
                if (bx >= 0 && bx <= b.Width) ctx.DrawLine(beatPen, new Point(bx, b.Top), new Point(bx, b.Bottom));
            }
        }
    }

    private void DrawOnsetDensityFill(DrawingContext ctx, Rect b)
    {
        var density = OnsetDensityCurve;
        if (density is null || density.Length == 0) return;

        int width = (int)b.Width;
        for (int x = 0; x < width; x++)
        {
            int idx = (int)Math.Clamp((double)x / width * density.Length, 0, density.Length - 1);
            float d = density[idx];
            if (d < 0.05f) continue;
            byte alpha = (byte)(d * 28); // very subtle fill
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, 80, 120, 200)),
                new Rect(x, b.Top, 1, b.Height));
        }
    }

    private void DrawRgbWaveform(DrawingContext ctx, Rect b)
    {
        var low = WaveformLow; var mid = WaveformMid; var high = WaveformHigh;

        // Fall back to placeholder gradient if no real data
        if (low is null || mid is null || high is null || low.Length == 0)
        {
            DrawPlaceholderWaveform(ctx, b);
            return;
        }

        int n = low.Length;
        int width = (int)b.Width;
        double centerY = b.Top + b.Height * 0.5;
        double maxHalf = b.Height * 0.45;

        for (int px = 0; px < width; px++)
        {
            // Map pixel to sample index respecting zoom/scroll
            double t = PixelToTime(px, b);
            int idx = (int)Math.Clamp(t / TrackDuration * n, 0, n - 1);

            byte r = low[idx];  // sub-bass → Red (Rekordbox low-freq = red)
            byte g = mid[idx];  // mids → Green
            byte bv = high[idx]; // highs → Blue

            float amp = (r + g + bv) / 3f / 255f; // average amplitude for height
            double halfH = amp * maxHalf;

            var color = Color.FromArgb(220, r, g, bv);
            var pen = new Pen(new SolidColorBrush(color));
            ctx.DrawLine(pen, new Point(px, centerY - halfH), new Point(px, centerY + halfH));
        }
    }

    private static void DrawPlaceholderWaveform(DrawingContext ctx, Rect b)
    {
        // Simple placeholder — horizontal bar to show the control is alive
        var brush = new SolidColorBrush(Color.FromArgb(40, 100, 140, 200));
        ctx.FillRectangle(brush, new Rect(b.Left, b.Top + b.Height * 0.3, b.Width, b.Height * 0.4));
        var textBrush = new SolidColorBrush(Color.Parse("#444466"));
        var text = new FormattedText("Load a track to see the waveform",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default, 12, textBrush);
        ctx.DrawText(text, new Point(b.Width / 2 - text.Width / 2, b.Height / 2 - 8));
    }

    private void DrawEnergyOverlay(DrawingContext ctx, Rect b)
    {
        var curve = EnergyCurve;
        if (curve is null || curve.Length < 2) return;

        int width = (int)b.Width;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(80, 80, 220, 100)), 1.5);

        for (int x = 0; x < width - 1; x++)
        {
            int i1 = (int)Math.Clamp((double)x / width * curve.Length, 0, curve.Length - 1);
            int i2 = (int)Math.Clamp((double)(x + 1) / width * curve.Length, 0, curve.Length - 1);
            double y1 = b.Bottom - curve[i1] * b.Height * 0.35 - b.Height * 0.08;
            double y2 = b.Bottom - curve[i2] * b.Height * 0.35 - b.Height * 0.08;
            ctx.DrawLine(pen, new Point(x, y1), new Point(x + 1, y2));
        }
    }

    private void DrawVocalOverlay(DrawingContext ctx, Rect b)
    {
        var curve = VocalDensityCurve;
        if (curve is null || curve.Length < 2) return;

        int width = (int)b.Width;
        for (int x = 0; x < width; x++)
        {
            int idx = (int)Math.Clamp((double)x / width * curve.Length, 0, curve.Length - 1);
            float v = curve[idx];
            if (v <= 0.3f) continue;
            byte alpha = (byte)(v * 55);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, 255, 80, 80)), new Rect(x, b.Top, 1, b.Height));
        }
    }

    private void DrawDoubleDropZone(DrawingContext ctx, Rect b)
    {
        if (Cues is null) return;
        var drops = Cues
            .Where(c => !c.IsLoop && c.Role == CueRole.Drop)
            .OrderBy(c => c.Timestamp)
            .Take(2)
            .ToList();
        if (drops.Count < 2) return;

        double x1 = TimeToPixel(drops[0].Timestamp, b);
        double x2 = TimeToPixel(drops[1].Timestamp, b);
        double w = x2 - x1;
        if (w < 2) return;

        // Translucent purple fill between the two drops
        var fillBrush = new SolidColorBrush(Color.FromArgb(28, 180, 60, 255));
        ctx.FillRectangle(fillBrush, new Rect(x1, b.Top, w, b.Height));

        // Dashed border on each edge using short segments
        var edgePen = new Pen(new SolidColorBrush(Color.FromArgb(160, 180, 60, 255)), 1.5,
            new DashStyle(new double[] { 5, 4 }, 0));
        ctx.DrawLine(edgePen, new Point(x1, b.Top), new Point(x1, b.Bottom));
        ctx.DrawLine(edgePen, new Point(x2, b.Top), new Point(x2, b.Bottom));

        // "DOUBLE DROP" label at top center
        var label = new FormattedText("DOUBLE DROP",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 8,
            new SolidColorBrush(Color.FromArgb(180, 200, 100, 255)));
        double lx = x1 + (w - label.Width) / 2;
        if (lx >= 0 && lx + label.Width <= b.Width)
            ctx.DrawText(label, new Point(lx, b.Top + 16));
    }

    private void DrawLoopBlocks(DrawingContext ctx, Rect b)
    {
        if (Cues is null) return;
        foreach (var loop in Cues.Where(c => c.IsLoop))
        {
            double x1 = TimeToPixel(loop.Timestamp, b);
            double x2 = TimeToPixel(loop.LoopEndSeconds, b);
            if (x2 <= 0 || x1 >= b.Width) continue;
            x1 = Math.Max(b.Left, x1); x2 = Math.Min(b.Right, x2);

            var col = ParseColor(loop.Color, 0x00FF88);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(45, col.R, col.G, col.B)), new Rect(x1, b.Top, x2 - x1, b.Height));

            // Edge handles
            var edgePen = new Pen(new SolidColorBrush(col), 2);
            ctx.DrawLine(edgePen, new Point(x1, b.Top), new Point(x1, b.Bottom));
            ctx.DrawLine(edgePen, new Point(x2, b.Top), new Point(x2, b.Bottom));

            // Grip triangles
            DrawGripTriangle(ctx, col, x1, b.Top, false);
            DrawGripTriangle(ctx, col, x2, b.Top, true);
        }
    }

    private void DrawCueMarkers(DrawingContext ctx, Rect b)
    {
        if (Cues is null) return;
        foreach (var cue in Cues.Where(c => !c.IsLoop))
        {
            double x = TimeToPixel(cue.Timestamp, b);
            if (x < 0 || x > b.Width) continue;

            var col = ParseColor(cue.Color, 0xFFFF00);
            var pen = new Pen(new SolidColorBrush(col), 1.5);
            ctx.DrawLine(pen, new Point(x, b.Top + 14), new Point(x, b.Bottom));

            // Flag rectangle
            ctx.FillRectangle(new SolidColorBrush(col), new Rect(x, b.Top, 2, b.Height * 0.85));

            // Slot number badge
            if (cue.SlotIndex >= 0)
            {
                var badge = new SolidColorBrush(col);
                ctx.FillRectangle(badge, new Rect(x + 2, b.Top, 14, 13));
                var txt = new FormattedText((cue.SlotIndex + 1).ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 9,
                    new SolidColorBrush(Colors.Black));
                ctx.DrawText(txt, new Point(x + 3, b.Top + 1));
            }

            // Confidence indicator (small dot at flag top)
            if (cue.Confidence > 0f)
            {
                byte alpha = (byte)(cue.Confidence * 255);
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)),
                    new Rect(x - 1, b.Top, 4, 3));
            }
        }
    }

    private void DrawPlayhead(DrawingContext ctx, Rect b)
    {
        double x = TimeToPixel(CurrentPlayPosition, b);
        if (x < 0 || x > b.Width) return;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#00D7FF")), 1.5);
        ctx.DrawLine(pen, new Point(x, b.Top), new Point(x, b.Bottom));
        // Triangle head
        var pts = new[] { new Point(x - 5, b.Top), new Point(x + 5, b.Top), new Point(x, b.Top + 8) };
        var geo = new StreamGeometry();
        using (var sgc = geo.Open()) { sgc.BeginFigure(pts[0], true); sgc.LineTo(pts[1]); sgc.LineTo(pts[2]); sgc.EndFigure(true); }
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#00D7FF")), null, geo);
    }

    private static void DrawGripTriangle(DrawingContext ctx, Color col, double x, double y, bool flipped)
    {
        double dir = flipped ? -1 : 1;
        var pts = new[] { new Point(x, y + 2), new Point(x + dir * 6, y + 2), new Point(x, y + 9) };
        var geo = new StreamGeometry();
        using (var sgc = geo.Open()) { sgc.BeginFigure(pts[0], true); sgc.LineTo(pts[1]); sgc.LineTo(pts[2]); sgc.EndFigure(true); }
        ctx.DrawGeometry(new SolidColorBrush(col), null, geo);
    }

    // ── Mouse Interaction ──────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetPosition(this);
        var b = new Rect(0, 0, Bounds.Width, Bounds.Height - PhraseMapHeight);

        if (Cues is null) return;

        // Loop handle hit test
        var loop = Cues.FirstOrDefault(c => c.IsLoop);
        if (loop is not null)
        {
            double lx1 = TimeToPixel(loop.Timestamp, b), lx2 = TimeToPixel(loop.LoopEndSeconds, b);
            if (Math.Abs(pt.X - lx1) < HitRadius) { _isDraggingLoopStart = true; e.Pointer.Capture(this); return; }
            if (Math.Abs(pt.X - lx2) < HitRadius) { _isDraggingLoopEnd = true; e.Pointer.Capture(this); return; }
        }

        // Cue hit test
        foreach (var cue in Cues.Where(c => !c.IsLoop))
        {
            if (Math.Abs(pt.X - TimeToPixel(cue.Timestamp, b)) < HitRadius)
            { _draggedCue = cue; e.Pointer.Capture(this); return; }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        var b = new Rect(0, 0, Bounds.Width, Bounds.Height - PhraseMapHeight);

        if (_draggedCue is not null)
        {
            _draggedCue.Timestamp = Math.Clamp(ApplySnapping(PixelToTime(pt.X, b)), 0, TrackDuration);
            InvalidateVisual();
        }
        else if (_isDraggingLoopStart || _isDraggingLoopEnd)
        {
            var loop = Cues?.FirstOrDefault(c => c.IsLoop);
            if (loop is not null)
            {
                double t = Math.Clamp(ApplySnapping(PixelToTime(pt.X, b)), 0, TrackDuration);
                if (_isDraggingLoopStart) loop.Timestamp = t; else loop.LoopEndSeconds = t;
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _draggedCue = null; _isDraggingLoopStart = false; _isDraggingLoopEnd = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Zoom with Ctrl + wheel
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            _zoomLevel = Math.Clamp(_zoomLevel * (e.Delta.Y > 0 ? 1.25 : 0.8), 1.0, 32.0);
        }
        else
        {
            // Scroll
            double range = 1.0 / _zoomLevel;
            _scrollOffset = Math.Clamp(_scrollOffset - e.Delta.Y * range * 0.05, 0, 1.0 - range);
        }
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CuesProperty || change.Property == PhraseSegmentsProperty ||
            change.Property == CurrentPlayPositionProperty || change.Property == BpmProperty ||
            change.Property == TrackDurationProperty || change.Property == WaveformLowProperty ||
            change.Property == WaveformMidProperty || change.Property == WaveformHighProperty ||
            change.Property == EnergyCurveProperty || change.Property == VocalDensityCurveProperty ||
            change.Property == OnsetDensityCurveProperty)
        {
            InvalidateVisual();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private double ApplySnapping(double t)
    {
        if (!SnapToGrid || Bpm <= 0) return t;
        int q = ParseQuantize();
        double beat = 60.0 / Bpm;
        if (q > 0) { double grid = beat * q; double ng = Math.Round(t / grid) * grid; if (Math.Abs(t - ng) < 0.05) return ng; }
        double nb = Math.Round(t / beat) * beat; return Math.Abs(t - nb) < 0.05 ? nb : t;
    }

    private int ParseQuantize() => QuantizeBeatString switch
    { "Off" => 0, "1 beat" => 1, "2 beats" => 2, "4 beats" => 4, "8 beats" => 8,
      "16 beats" => 16, "32 beats" => 32, "64 beats (16 bars)" => 64, "128 beats (32 bars)" => 128, _ => 16 };

    private double TimeToPixel(double seconds, Rect b)
    {
        double visibleDuration = TrackDuration / _zoomLevel;
        return (seconds / visibleDuration - _scrollOffset) * b.Width;
    }

    private double PixelToTime(double px, Rect b)
    {
        double visibleDuration = TrackDuration / _zoomLevel;
        return (px / b.Width + _scrollOffset) * visibleDuration;
    }

    private static Color ParseColor(string hex, uint fallback)
    {
        try { return Color.Parse(hex); }
        catch { return Color.FromArgb(255, (byte)((fallback >> 16) & 0xFF), (byte)((fallback >> 8) & 0xFF), (byte)(fallback & 0xFF)); }
    }

    private static Color GetPhraseColor(string label) => label?.ToLowerInvariant() switch
    {
        "intro"                 => Color.FromArgb(180, 0, 200, 200),
        "build" or "buildup"   => Color.FromArgb(180, 220, 180, 0),
        "drop"                  => Color.FromArgb(180, 220, 40, 40),
        "breakdown"             => Color.FromArgb(180, 140, 50, 200),
        "outro"                 => Color.FromArgb(180, 0, 160, 180),
        "bridge"                => Color.FromArgb(180, 80, 140, 220),
        _                       => Color.FromArgb(100, 100, 100, 120),
    };
}
