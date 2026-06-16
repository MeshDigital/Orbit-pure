using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SLSKDONET.Models;

namespace SLSKDONET.Views.Avalonia.Controls
{
    /// <summary>
    /// Thin horizontal bar showing normalized energy over time with phrase boundary markers.
    /// Bind <see cref="Points"/> to energy curve data (0-1 values) and optionally
    /// <see cref="Segments"/> to overlay vertical dividers at phrase transitions.
    /// </summary>
    public sealed class EnergyCurveBar : Control
    {
        public static readonly StyledProperty<IReadOnlyList<double>?> PointsProperty =
            AvaloniaProperty.Register<EnergyCurveBar, IReadOnlyList<double>?>(nameof(Points));

        public static readonly StyledProperty<IEnumerable<PhraseSegment>?> SegmentsProperty =
            AvaloniaProperty.Register<EnergyCurveBar, IEnumerable<PhraseSegment>?>(nameof(Segments));

        public IReadOnlyList<double>? Points
        {
            get => GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        public IEnumerable<PhraseSegment>? Segments
        {
            get => GetValue(SegmentsProperty);
            set => SetValue(SegmentsProperty, value);
        }

        static EnergyCurveBar()
        {
            AffectsRender<EnergyCurveBar>(PointsProperty, SegmentsProperty);
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);

            var pts = Points;
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#111114")), null, new Rect(0, 0, w, h));

            if (pts is { Count: >= 2 })
            {
                int n = pts.Count;
                double xStep = w / (n - 1);

                var fillGeo = new StreamGeometry();
                using (var gc = fillGeo.Open())
                {
                    gc.BeginFigure(new Point(0, h), true);
                    for (int i = 0; i < n; i++)
                    {
                        double x = i * xStep;
                        double y = h - Math.Clamp(pts[i], 0.0, 1.0) * h;
                        gc.LineTo(new Point(x, y));
                    }
                    gc.LineTo(new Point((n - 1) * xStep, h));
                    gc.EndFigure(true);
                }
                ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#334EC9B0")), null, fillGeo);

                var lineGeo = new StreamGeometry();
                using (var gc = lineGeo.Open())
                {
                    for (int i = 0; i < n; i++)
                    {
                        double x = i * xStep;
                        double y = h - Math.Clamp(pts[i], 0.0, 1.0) * h;
                        if (i == 0) gc.BeginFigure(new Point(x, y), false);
                        else gc.LineTo(new Point(x, y));
                    }
                    gc.EndFigure(false);
                }
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#4EC9B0")), 1.0), lineGeo);
            }

            var segList = Segments?.ToList();
            if (segList is { Count: > 1 })
            {
                double totalDuration = segList.Max(s => (double)(s.Start + s.Duration));
                if (totalDuration > 0)
                {
                    var dividerPen = new Pen(new SolidColorBrush(Color.Parse("#55FFFFFF")), 1.0);
                    foreach (var seg in segList.Skip(1))
                    {
                        double ratio = seg.Start / totalDuration;
                        double x = Math.Clamp(ratio * w, 0, w);
                        ctx.DrawLine(dividerPen, new Point(x, 0), new Point(x, h));
                    }
                }
            }
        }
    }
}
