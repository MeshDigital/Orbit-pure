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
public sealed class SparklineControl : Control
{
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
        AffectsRender<SparklineControl>(ValuesProperty, PointsProperty, LineBrushProperty, FillBrushProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        // Points takes priority; fall back to Values
        var values = Points ?? Values;
        if (values is null || values.Count < 2) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double min = values.Min();
        double max = values.Max();
        double range = max - min;
        if (range < 1e-9) range = 1.0;

        var pen = new Pen(LineBrush, LineThickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        var n = values.Count;
        var xStep = w / (n - 1);

        // Area fill geometry
        if (FillBrush is not null)
        {
            var fillGeo = new StreamGeometry();
            using (var gc = fillGeo.Open())
            {
                gc.BeginFigure(new Point(0, h), true);
                for (int i = 0; i < n; i++)
                {
                    double x = i * xStep;
                    double y = h - ((values[i] - min) / range) * h;
                    gc.LineTo(new Point(x, y));
                }
                gc.LineTo(new Point((n - 1) * xStep, h));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(FillBrush, null, fillGeo);
        }

        // Line geometry
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double x = i * xStep;
                double y = h - ((values[i] - min) / range) * h;
                if (i == 0) gc.BeginFigure(new Point(x, y), false);
                else gc.LineTo(new Point(x, y));
            }
            gc.EndFigure(false);
        }

        ctx.DrawGeometry(null, pen, geo);
    }
}

