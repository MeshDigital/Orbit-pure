using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SLSKDONET.Data.Entities;
using SLSKDONET.ViewModels.StoryGraph;

namespace SLSKDONET.Views.Avalonia.StoryGraph;

/// <summary>
/// Slice K5: native Avalonia Story Graph canvas. Renders <see cref="StoryGraphViewModel.Layout"/>
/// (computed by the pure <see cref="SLSKDONET.Services.StoryGraph.StoryGraphLayout"/>) — a primary
/// spine plus branch arcs — using <see cref="DrawingContext"/>. No node-graph library; the view only
/// draws geometry, never computes layout.
/// </summary>
public sealed class StoryGraphView : Control
{
    private const double NodeWidth = 96.0;
    private const double NodeHeight = 40.0;
    private const double TopPad = 96.0;   // room for upward branch arcs above the spine

    public static readonly StyledProperty<StoryGraphViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<StoryGraphView, StoryGraphViewModel?>(nameof(ViewModel));

    public StoryGraphViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    static StoryGraphView()
    {
        AffectsRender<StoryGraphView>(ViewModelProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var layout = ViewModel?.Layout;
        if (layout is null)
            return;

        foreach (var edge in layout.Edges)
        {
            var pen = new Pen(
                edge.IsPrimary ? Brushes.DimGray : Brushes.SteelBlue,
                edge.IsPrimary ? 2.0 : 1.5);

            var start = new Point(edge.StartX + NodeWidth, TopPad);
            var end = new Point(edge.EndX, TopPad);

            if (edge.ControlY == 0.0)
            {
                context.DrawLine(pen, start, end);
            }
            else
            {
                var mid = new Point((start.X + end.X) / 2.0, TopPad + edge.ControlY);
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(start, isFilled: false);
                    ctx.QuadraticBezierTo(mid, end);
                    ctx.EndFigure(isClosed: false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
        }

        foreach (var node in layout.Nodes)
        {
            var rect = new Rect(node.X, TopPad - NodeHeight / 2.0, NodeWidth, NodeHeight);
            context.DrawRectangle(BrushForRole(node.Role), new Pen(Brushes.White, 1.0), rect);

            if (node.EnergyTarget is double energy)
            {
                var barWidth = (NodeWidth - 8.0) * (energy / 100.0);
                var bar = new Rect(node.X + 4.0, TopPad + NodeHeight / 2.0 - 6.0, barWidth, 3.0);
                context.DrawRectangle(Brushes.Orange, null, bar);
            }
        }
    }

    private static IBrush BrushForRole(SetSlotRole role) => role switch
    {
        SetSlotRole.Opening => Brushes.SeaGreen,
        SetSlotRole.Peak => Brushes.OrangeRed,
        SetSlotRole.Reset => Brushes.SteelBlue,
        SetSlotRole.Closer => Brushes.Goldenrod,
        SetSlotRole.Atmosphere => Brushes.MediumPurple,
        SetSlotRole.Builder => Brushes.DarkSlateGray,
        _ => Brushes.DimGray,
    };
}
