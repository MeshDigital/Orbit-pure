using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Lightweight Canvas-rendered sparkline that plots a data series as a line + fill area.
/// Bind <see cref="Values"/> to speed history or <see cref="Points"/> to energy curves.
/// </summary>
public sealed class SparklineControl : Control, global::Avalonia.Rendering.ICustomHitTest
{
    // Built geometry/pen are cached and only rebuilt when the data, size, or styling changes —
    // Render runs every time a recycled tracklist row is re-bound while scrolling, and each new
    // StreamGeometry/Pen wraps a native Skia object left for the finalizer.
    private StreamGeometry? _fillGeometry;
    private StreamGeometry? _lineGeometry;
    private IPen? _pen;
    private IReadOnlyList<double>? _builtFor;
    private double _builtFingerprint;
    private Size _builtSize;

    private static double Fingerprint(IReadOnlyList<double> values)
    {
        double h = values.Count;
        for (int i = 0; i < values.Count; i++) h = h * 1.000003 + values[i] * (i + 1);
        return h;
    }

    /// <summary>
    /// Rectangle hit-test: without this, Avalonia hit-tests against the stroked path itself
    /// (StrokeContains builds a stroked outline) on every pointer move over a row. The tooltip only
    /// needs "is the pointer over this control".
    /// </summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<SparklineControl, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>Alias for <see cref="Values"/> — use for energy curve bindings (EnergyCurvePoints).</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> PointsProperty =
        AvaloniaProperty.Register<SparklineControl, IReadOnlyList<double>?>(nameof(Points));

    public static readonly StyledProperty<IBrush> LineBrushProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush>(nameof(LineBrush),
            new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54))); // Spotify green

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<SparklineControl, double>(nameof(LineThickness), 1.5);

    /// <summary>Semi-transparent fill beneath the line. Null = no fill.</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(FillBrush), null);

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public double LineThickness
    {
        get => GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(ValuesProperty, PointsProperty, LineBrushProperty, FillBrushProperty, LineThicknessProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineBrushProperty || change.Property == LineThicknessProperty)
            _pen = null;
    }

    public override void Render(DrawingContext ctx)
    {
        try
        {
            RenderInternal(ctx);
        }
        catch (Exception ex)
        {
            // Same defensive guard applied to WaveformControl/LiveBackground/LibraryWaveformView
            // this session: an unhandled render-path exception can silently hard-crash the whole
            // process with zero trace. No live crash confirmed here, but this control is bound to
            // externally-owned collections (e.g. EnergyCurvePoints) with no guard at all — cheap
            // insurance against a future collection-mutated-during-render race.
            Serilog.Log.Warning(ex, "SparklineControl: render tick failed — skipping frame");
        }
    }

    private void RenderInternal(DrawingContext ctx)
    {
        base.Render(ctx);

        // Points takes priority; fall back to Values
        var values = Points ?? Values;
        if (values is null || values.Count < 2) return;

        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        // Keyed on content as well as reference: some callers (download speed histories) mutate a
        // fixed array in place and re-raise PropertyChanged with the same instance.
        var fingerprint = Fingerprint(values);
        if (_lineGeometry is null || !ReferenceEquals(values, _builtFor) || fingerprint != _builtFingerprint || size != _builtSize)
        {
            BuildGeometry(values, size);
            _builtFor = values;
            _builtFingerprint = fingerprint;
            _builtSize = size;
        }

        // Flat caps/bevel joins: round ones are markedly more expensive to stroke and invisible at
        // a ~1px line width.
        _pen ??= new Pen(LineBrush, LineThickness) { LineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Bevel };

        if (FillBrush is not null && _fillGeometry is not null)
            ctx.DrawGeometry(FillBrush, null, _fillGeometry);
        ctx.DrawGeometry(null, _pen, _lineGeometry!);
    }

    private void BuildGeometry(IReadOnlyList<double> values, Size size)
    {
        var w = size.Width;
        var h = size.Height;

        // At most ~1 point per pixel of width — callers bind series with thousands of samples
        // (a whole-track waveform) to a chart only tens of pixels wide; anything beyond that is
        // invisible but was still stroked every frame.
        var sampled = Downsample(values, Math.Max(2, (int)Math.Ceiling(w)));

        double min = sampled.Min();
        double max = sampled.Max();
        double range = max - min;
        if (range < 1e-9) range = 1.0;

        var n = sampled.Count;
        var xStep = w / (n - 1);
        double Y(int i) => h - ((sampled[i] - min) / range) * h;

        _fillGeometry = new StreamGeometry();
        using (var gc = _fillGeometry.Open())
        {
            gc.BeginFigure(new Point(0, h), true);
            for (int i = 0; i < n; i++) gc.LineTo(new Point(i * xStep, Y(i)));
            gc.LineTo(new Point((n - 1) * xStep, h));
            gc.EndFigure(true);
        }

        _lineGeometry = new StreamGeometry();
        using (var gc = _lineGeometry.Open())
        {
            gc.BeginFigure(new Point(0, Y(0)), false);
            for (int i = 1; i < n; i++) gc.LineTo(new Point(i * xStep, Y(i)));
            gc.EndFigure(false);
        }
    }

    /// <summary>Bucket-averages <paramref name="values"/> down to at most <paramref name="maxPoints"/>.</summary>
    internal static IReadOnlyList<double> Downsample(IReadOnlyList<double> values, int maxPoints)
    {
        var len = values.Count;
        if (len <= maxPoints) return values;

        var result = new double[maxPoints];
        for (int o = 0; o < maxPoints; o++)
        {
            var start = (int)((long)o * len / maxPoints);
            var end = Math.Max(start + 1, (int)((long)(o + 1) * len / maxPoints));
            double sum = 0;
            for (int i = start; i < end; i++) sum += values[i];
            result[o] = sum / (end - start);
        }
        return result;
    }
}

