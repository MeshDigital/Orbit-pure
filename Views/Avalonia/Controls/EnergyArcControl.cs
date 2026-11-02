using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Sets page energy arc (rebuild plan P2.8): the DJ's energy target per slot (accent) against the
/// tracks' analysed energy (neutral), 0-100 over slot order. Missing values break the line rather
/// than being drawn as a made-up middle value. Pens come from design tokens and are cached.
/// </summary>
public sealed class EnergyArcControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<double?>?> TargetsProperty =
        AvaloniaProperty.Register<EnergyArcControl, IReadOnlyList<double?>?>(nameof(Targets));

    public static readonly StyledProperty<IReadOnlyList<double?>?> ActualsProperty =
        AvaloniaProperty.Register<EnergyArcControl, IReadOnlyList<double?>?>(nameof(Actuals));

    public IReadOnlyList<double?>? Targets
    {
        get => GetValue(TargetsProperty);
        set => SetValue(TargetsProperty, value);
    }

    public IReadOnlyList<double?>? Actuals
    {
        get => GetValue(ActualsProperty);
        set => SetValue(ActualsProperty, value);
    }

    private Pen? _targetPen;
    private Pen? _actualPen;
    private Pen? _baselinePen;

    static EnergyArcControl()
    {
        AffectsRender<EnergyArcControl>(TargetsProperty, ActualsProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _targetPen = new Pen(Brush("BrushAccent"), 2);
        _actualPen = new Pen(Brush("BrushText2"), 1.5, new DashStyle(new double[] { 3, 2 }, 0));
        _baselinePen = new Pen(Brush("BrushBorder0"), 1);
    }

    private IBrush Brush(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (_targetPen is null || size.Width <= 0 || size.Height <= 0)
            return;

        context.DrawLine(_baselinePen!, new Point(0, size.Height - 0.5), new Point(size.Width, size.Height - 0.5));
        DrawSeries(context, Actuals, _actualPen!, size);
        DrawSeries(context, Targets, _targetPen, size);
    }

    private static void DrawSeries(DrawingContext context, IReadOnlyList<double?>? values, Pen pen, Size size)
    {
        if (values is null || values.Count == 0)
            return;

        // One point per slot, spread across the width; a lone slot sits in the middle.
        double X(int i) => values.Count == 1 ? size.Width / 2 : i * size.Width / (values.Count - 1);
        double Y(double v) => size.Height - (System.Math.Clamp(v, 0, 100) / 100.0 * (size.Height - 2)) - 1;

        Point? previous = null;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not double v)
            {
                previous = null; // gap: unknown value
                continue;
            }
            var point = new Point(X(i), Y(v));
            if (previous is { } p)
                context.DrawLine(pen, p, point);
            else
                context.DrawEllipse(pen.Brush, null, point, 1.5, 1.5);
            previous = point;
        }
    }
}
