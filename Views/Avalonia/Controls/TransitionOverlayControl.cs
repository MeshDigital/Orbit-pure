using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// The transition editor's overlay: the outgoing track A and the incoming track B drawn on top of
/// each other on one shared, tempo-corrected timeline (<see cref="TransitionOverlayModel"/>), with
/// the blend window shaded, both gain curves, EQ swap points, bar numbers and the preview playhead.
/// Drag horizontally to shift where B comes in (snapped to B's bars, Shift = beats); the result goes
/// to <see cref="ShiftIncomingCommand"/> (the same mix-in state as the nudge buttons, undoable).
///
/// Performance: the two waveform envelopes and the two gain curves are built as geometries only
/// when the model, the waveform data or the size change. A frame (playhead moves during preview)
/// draws cached geometries, cached pens/brushes and cached labels — no allocations. Control.Render
/// runs on the UI thread, so reading styled properties here is safe.
/// </summary>
public sealed class TransitionOverlayControl : Control
{
    public static readonly StyledProperty<TransitionOverlayModel?> ModelProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, TransitionOverlayModel?>(nameof(Model));
    public static readonly StyledProperty<WaveformAnalysisData?> OutgoingWaveformProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, WaveformAnalysisData?>(nameof(OutgoingWaveform));
    public static readonly StyledProperty<WaveformAnalysisData?> IncomingWaveformProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, WaveformAnalysisData?>(nameof(IncomingWaveform));
    public static readonly StyledProperty<double?> PlayheadSecondsProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, double?>(nameof(PlayheadSeconds));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> ShiftIncomingCommandProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, System.Windows.Input.ICommand?>(nameof(ShiftIncomingCommand));

    public static readonly StyledProperty<IBrush?> OutgoingFillProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(OutgoingFill));
    public static readonly StyledProperty<IBrush?> IncomingFillProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(IncomingFill));
    public static readonly StyledProperty<IBrush?> OutgoingCurveBrushProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(OutgoingCurveBrush));
    public static readonly StyledProperty<IBrush?> IncomingCurveBrushProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(IncomingCurveBrush));
    public static readonly StyledProperty<IBrush?> OverlapFillProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(OverlapFill));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(LabelBrush));
    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(MarkerBrush));
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<TransitionOverlayControl, IBrush?>(nameof(PlayheadBrush));

    public TransitionOverlayModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public WaveformAnalysisData? OutgoingWaveform { get => GetValue(OutgoingWaveformProperty); set => SetValue(OutgoingWaveformProperty, value); }
    public WaveformAnalysisData? IncomingWaveform { get => GetValue(IncomingWaveformProperty); set => SetValue(IncomingWaveformProperty, value); }
    public double? PlayheadSeconds { get => GetValue(PlayheadSecondsProperty); set => SetValue(PlayheadSecondsProperty, value); }
    public System.Windows.Input.ICommand? ShiftIncomingCommand { get => GetValue(ShiftIncomingCommandProperty); set => SetValue(ShiftIncomingCommandProperty, value); }
    public IBrush? OutgoingFill { get => GetValue(OutgoingFillProperty); set => SetValue(OutgoingFillProperty, value); }
    public IBrush? IncomingFill { get => GetValue(IncomingFillProperty); set => SetValue(IncomingFillProperty, value); }
    public IBrush? OutgoingCurveBrush { get => GetValue(OutgoingCurveBrushProperty); set => SetValue(OutgoingCurveBrushProperty, value); }
    public IBrush? IncomingCurveBrush { get => GetValue(IncomingCurveBrushProperty); set => SetValue(IncomingCurveBrushProperty, value); }
    public IBrush? OverlapFill { get => GetValue(OverlapFillProperty); set => SetValue(OverlapFillProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? MarkerBrush { get => GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    private const double RulerHeight = 14;
    private const double ColumnStep = 2; // px per envelope sample

    // Cached geometry (rebuilt on model / data / size change only).
    private StreamGeometry? _outgoingEnvelope, _incomingEnvelope, _outgoingCurve, _incomingCurve;
    private Size _builtFor;
    private bool _geometryDirty = true;

    // Cached pens and labels (rebuilt when a brush changes).
    private IPen? _gridPen, _barPen, _edgePen, _markerPen, _playheadPen, _outCurvePen, _inCurvePen;
    private bool _pensDirty = true;
    private readonly Dictionary<int, FormattedText> _barLabels = new();
    private readonly Dictionary<string, FormattedText> _textLabels = new();
    private static readonly Typeface MonoTypeface = new(new FontFamily("Cascadia Mono, Consolas, monospace"));
    private static readonly Typeface UiTypeface = new("Inter");

    // Drag state (shift B).
    private bool _pressed, _dragging;
    private double _pressX, _dragOffsetSeconds;
    private double _ghostTarget;

    static TransitionOverlayControl()
    {
        AffectsRender<TransitionOverlayControl>(PlayheadSecondsProperty);
        FocusableProperty.OverrideDefaultValue<TransitionOverlayControl>(false);
    }

    public TransitionOverlayControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty || change.Property == OutgoingWaveformProperty || change.Property == IncomingWaveformProperty)
        {
            _geometryDirty = true;
            InvalidateVisual();
        }
        else if (change.Property == OutgoingCurveBrushProperty || change.Property == IncomingCurveBrushProperty ||
                 change.Property == GridBrushProperty || change.Property == LabelBrushProperty ||
                 change.Property == MarkerBrushProperty || change.Property == PlayheadBrushProperty ||
                 change.Property == OutgoingFillProperty || change.Property == IncomingFillProperty ||
                 change.Property == OverlapFillProperty)
        {
            _pensDirty = true;
            _barLabels.Clear();
            _textLabels.Clear();
            InvalidateVisual();
        }
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────────────

    /// <summary>Shared-timeline seconds → x (pure, for tests and drawing).</summary>
    public static double TimeToX(double seconds, TransitionOverlayModel model, double width) =>
        width <= 0 ? 0 : (seconds - model.StartTime) / model.SpanSeconds * width;

    /// <summary>x → shared-timeline seconds.</summary>
    public static double XToTime(double x, TransitionOverlayModel model, double width) =>
        width <= 0 ? model.StartTime : model.StartTime + x / width * model.SpanSeconds;

    // ── Rendering ────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var size = Bounds.Size;
        var model = Model;
        if (model == null || size.Width < 2 || size.Height < RulerHeight + 8) return;

        if (_pensDirty) BuildPens();
        if (_geometryDirty || Math.Abs(_builtFor.Width - size.Width) > 0.5 || Math.Abs(_builtFor.Height - size.Height) > 0.5)
            BuildGeometry(model, size);

        double w = size.Width, top = RulerHeight, h = size.Height;

        // Blend window.
        double ox0 = TimeToX(0, model, w), ox1 = TimeToX(model.WindowSeconds, model, w);
        if (OverlapFill != null) ctx.FillRectangle(OverlapFill, new Rect(ox0, top, Math.Max(0, ox1 - ox0), h - top));

        DrawGrid(ctx, model, w, h);

        // A, then B (B shifted while dragging).
        if (_outgoingEnvelope != null && OutgoingFill != null) ctx.DrawGeometry(OutgoingFill, null, _outgoingEnvelope);
        double shiftPx = _dragging ? _dragOffsetSeconds / model.SpanSeconds * w : 0;
        using (ctx.PushTransform(Matrix.CreateTranslation(shiftPx, 0)))
        {
            if (_incomingEnvelope != null && IncomingFill != null) ctx.DrawGeometry(IncomingFill, null, _incomingEnvelope);
            if (_incomingCurve != null && _inCurvePen != null) ctx.DrawGeometry(null, _inCurvePen, _incomingCurve);
        }
        if (_outgoingCurve != null && _outCurvePen != null) ctx.DrawGeometry(null, _outCurvePen, _outgoingCurve);

        // Window edges.
        if (_edgePen != null)
        {
            ctx.DrawLine(_edgePen, new Point(ox0, top), new Point(ox0, h));
            ctx.DrawLine(_edgePen, new Point(ox1, top), new Point(ox1, h));
        }

        // EQ swap points.
        if (_markerPen != null)
        {
            foreach (var m in model.Markers)
            {
                double mx = TimeToX(m.Seconds, model, w);
                ctx.DrawLine(_markerPen, new Point(mx, top), new Point(mx, h));
                var label = Label(m.Label);
                if (label != null) ctx.DrawText(label, new Point(Math.Min(mx + 4, w - label.Width - 2), h - label.Height - 2));
            }
        }

        // Ghost readout while dragging: where B would come in.
        if (_dragging && Label("B in " + FormatTime(_ghostTarget)) is { } ghost)
            ctx.DrawText(ghost, new Point(Math.Clamp(ox0 + shiftPx + 4, 0, Math.Max(0, w - ghost.Width)), top + 2));

        // Preview playhead.
        if (PlayheadSeconds is { } p && _playheadPen != null && p >= model.StartTime && p <= model.EndTime)
        {
            double px = TimeToX(p, model, w);
            ctx.DrawLine(_playheadPen, new Point(px, 0), new Point(px, h));
        }
    }

    private void DrawGrid(DrawingContext ctx, TransitionOverlayModel model, double w, double h)
    {
        if (_barPen == null || _gridPen == null) return;
        double bar = model.BarSeconds;
        double barPx = bar / model.SpanSeconds * w;
        if (barPx <= 0) return;
        int labelEvery = 1;
        while (labelEvery * barPx < 28 && labelEvery < 64) labelEvery *= 2;
        int first = (int)Math.Ceiling(model.StartTime / bar - 1e-6);
        int last = (int)Math.Floor(model.EndTime / bar + 1e-6);
        for (int k = first; k <= last; k++)
        {
            double x = Math.Round(TimeToX(k * bar, model, w)) + 0.5;
            bool phrase = k % 4 == 0;
            ctx.DrawLine(phrase ? _barPen : _gridPen, new Point(x, RulerHeight), new Point(x, h));
            if (k % labelEvery == 0 && BarLabel(TransitionOverlayModel.BarLabel(k)) is { } label)
                ctx.DrawText(label, new Point(x + 3, (RulerHeight - label.Height) / 2));
        }
    }

    private void BuildPens()
    {
        _pensDirty = false;
        _gridPen = GridBrush != null ? new Pen(GridBrush, 1).ToImmutable() : null;
        _barPen = GridBrush != null ? new Pen(GridBrush, 2).ToImmutable() : null; // every 4th bar
        _edgePen = LabelBrush != null ? new Pen(LabelBrush, 1).ToImmutable() : null;
        _markerPen = MarkerBrush != null ? new Pen(MarkerBrush, 1, dashStyle: new DashStyle(new double[] { 3, 3 }, 0)).ToImmutable() : null;
        _playheadPen = PlayheadBrush != null ? new Pen(PlayheadBrush, 1.5).ToImmutable() : null;
        _outCurvePen = OutgoingCurveBrush != null ? new Pen(OutgoingCurveBrush, 1.5).ToImmutable() : null;
        _inCurvePen = IncomingCurveBrush != null ? new Pen(IncomingCurveBrush, 1.5).ToImmutable() : null;
    }

    private FormattedText? BarLabel(int bar)
    {
        if (LabelBrush == null) return null;
        if (!_barLabels.TryGetValue(bar, out var ft))
        {
            if (_barLabels.Count > 512) _barLabels.Clear();
            _barLabels[bar] = ft = new FormattedText(bar.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoTypeface, 10, LabelBrush);
        }
        return ft;
    }

    private FormattedText? Label(string text)
    {
        if (LabelBrush == null) return null;
        if (!_textLabels.TryGetValue(text, out var ft))
        {
            if (_textLabels.Count > 256) _textLabels.Clear();
            _textLabels[text] = ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, 10, LabelBrush);
        }
        return ft;
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Envelopes (mirrored around the centre of the lane) and gain curves for the current
    /// model and size — the only place geometry is allocated.</summary>
    private void BuildGeometry(TransitionOverlayModel model, Size size)
    {
        _geometryDirty = false;
        _builtFor = size;
        double w = size.Width, top = RulerHeight + 2, bottom = size.Height - 2;
        double mid = (top + bottom) / 2, half = (bottom - top) / 2 * 0.92;

        _outgoingEnvelope = Envelope(OutgoingWaveform, model, w, mid, half, outgoing: true);
        _incomingEnvelope = Envelope(IncomingWaveform, model, w, mid, half, outgoing: false);
        _outgoingCurve = Curve(model, w, top, bottom, outgoing: true);
        _incomingCurve = Curve(model, w, top, bottom, outgoing: false);
    }

    private static StreamGeometry? Envelope(WaveformAnalysisData? data, TransitionOverlayModel model, double w, double mid, double half, bool outgoing)
    {
        if (data == null || data.PeakData.Length < 2 || data.DurationSeconds <= 0) return null;
        int cols = Math.Max(2, (int)(w / ColumnStep));
        var amps = new double[cols + 1];
        var sounds = new bool[cols + 1];
        bool any = false;
        double perSecond = data.PeakData.Length / data.DurationSeconds;
        for (int c = 0; c <= cols; c++)
        {
            double t0 = XToTime(c * ColumnStep, model, w), t1 = XToTime((c + 1) * ColumnStep, model, w);
            bool on = outgoing ? model.OutgoingSoundsAt(t0) : model.IncomingSoundsAt(t0);
            sounds[c] = on;
            if (!on) continue;
            double a0 = outgoing ? model.OutgoingTrackTime(t0) : model.IncomingTrackTime(t0);
            double a1 = outgoing ? model.OutgoingTrackTime(t1) : model.IncomingTrackTime(t1);
            int i0 = (int)Math.Clamp(a0 * perSecond, 0, data.PeakData.Length - 1);
            int i1 = (int)Math.Clamp(Math.Ceiling(a1 * perSecond), i0 + 1, data.PeakData.Length);
            int peak = 0;
            for (int i = i0; i < i1; i++) if (data.PeakData[i] > peak) peak = data.PeakData[i];
            amps[c] = peak / 255.0 * half;
            any = true;
        }
        if (!any) return null;

        // One closed figure per contiguous run where the track sounds.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            int c = 0;
            while (c <= cols)
            {
                while (c <= cols && !sounds[c]) c++;
                if (c > cols) break;
                int start = c;
                while (c <= cols && sounds[c]) c++;
                int end = c - 1;
                g.BeginFigure(new Point(start * ColumnStep, mid - amps[start]), true);
                for (int k = start + 1; k <= end; k++) g.LineTo(new Point(k * ColumnStep, mid - amps[k]));
                for (int k = end; k >= start; k--) g.LineTo(new Point(k * ColumnStep, mid + amps[k]));
                g.EndFigure(true);
            }
        }
        return geo;
    }

    private static StreamGeometry Curve(TransitionOverlayModel model, double w, double top, double bottom, bool outgoing)
    {
        int cols = Math.Max(2, (int)(w / ColumnStep));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool started = false;
            for (int c = 0; c <= cols; c++)
            {
                double x = c * ColumnStep;
                double t = XToTime(x, model, w);
                if (outgoing ? t > model.WindowSeconds + model.SpanSeconds / cols : t < -model.SpanSeconds / cols) { if (started) break; continue; }
                double gain = outgoing ? model.OutgoingGainAt(t) : model.IncomingGainAt(t);
                var p = new Point(x, bottom - Math.Clamp(gain, 0, 1) * (bottom - top));
                if (!started) { g.BeginFigure(p, false); started = true; }
                else g.LineTo(p);
            }
            if (started) g.EndFigure(false);
        }
        return geo;
    }

    // ── Drag B ───────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Model == null || ShiftIncomingCommand == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _dragging = false;
        _pressX = e.GetPosition(this).X;
        _dragOffsetSeconds = 0;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed || Model is not { } model || Bounds.Width <= 0) return;
        double x = e.GetPosition(this).X;
        if (!_dragging && Math.Abs(x - _pressX) < MarkerGesture<object>.DragThresholdPixels) return;
        _dragging = true;
        double raw = (x - _pressX) / Bounds.Width * model.SpanSeconds;
        _ghostTarget = model.ShiftedTargetTrigger(raw, snapToBar: (e.KeyModifiers & KeyModifiers.Shift) == 0);
        _dragOffsetSeconds = model.DrawOffsetFor(_ghostTarget); // B jumps bar by bar (snapped)
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        bool dragged = _dragging;
        double target = _ghostTarget;
        EndDrag();
        e.Pointer.Capture(null);
        if (dragged && ShiftIncomingCommand?.CanExecute(target) == true) ShiftIncomingCommand.Execute(target);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _pressed = false;
        _dragging = false;
        _dragOffsetSeconds = 0;
        InvalidateVisual();
    }
}
