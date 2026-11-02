using System.ComponentModel;
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

    // Cached brushes/pens — never allocate in Render (CLAUDE.md known pitfall).
    private static readonly Pen PrimaryEdgePen = new(Brushes.DimGray, 2.0);
    private static readonly Pen BranchEdgePen = new(Brushes.SteelBlue, 1.5);
    private static readonly Pen NodeBorderPen = new(Brushes.White, 1.0);
    private static readonly IBrush EnergyBarBrush = Brushes.Orange;

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

    public StoryGraphView()
    {
        // The canvas must redraw when the ViewModel's computed Layout changes, not only when the
        // ViewModel reference is swapped in. Without this the graph stays blank after LoadSetAsync.
        var vm = ViewModel;
        if (vm is not null)
            vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is StoryGraphViewModel oldVm)
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            if (change.NewValue is StoryGraphViewModel newVm)
                newVm.PropertyChanged += OnViewModelPropertyChanged;
            InvalidateVisual();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StoryGraphViewModel.Layout))
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var layout = ViewModel?.Layout;
        if (layout is null)
            return;

        foreach (var edge in layout.Edges)
        {
            var pen = edge.IsPrimary ? PrimaryEdgePen : BranchEdgePen;
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
            context.DrawRectangle(BrushForRole(node.Role), NodeBorderPen, rect);

            if (node.EnergyTarget is double energy)
            {
                var barWidth = (NodeWidth - 8.0) * (energy / 100.0);
                var bar = new Rect(node.X + 4.0, TopPad + NodeHeight / 2.0 - 6.0, barWidth, 3.0);
                context.DrawRectangle(EnergyBarBrush, null, bar);
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
