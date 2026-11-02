using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.StoryGraph;
using SLSKDONET.ViewModels.StoryGraph;

namespace SLSKDONET.Views.Avalonia.StoryGraph;

/// <summary>
/// Slice K5: native Avalonia Story Graph canvas. Renders <see cref="StoryGraphViewModel.Layout"/>
/// (computed by the pure <see cref="SLSKDONET.Services.StoryGraph.StoryGraphLayout"/>) — a primary
/// spine plus branch arcs — using <see cref="DrawingContext"/>. No node-graph library; the view only
/// draws geometry and maps clicks to slot selection, never computes layout.
/// </summary>
public sealed class StoryGraphView : Control
{
    private const double NodeWidth = 136.0;   // StoryGraphLayout.ColumnWidth (160) leaves a 24px gap
    private const double NodeHeight = 52.0;
    private const double TopPad = 96.0;       // room for upward branch arcs above the spine
    private const double LeftPad = 16.0;
    private const double TextInset = 8.0;

    private readonly Dictionary<string, FormattedText> _textCache = new();
    private bool _resourcesReady;
    private IBrush _text0 = Brushes.White;
    private IBrush _text2 = Brushes.Gray;
    private IBrush _nodeFill = Brushes.Transparent;
    private IBrush _energyBrush = Brushes.Orange;
    private Pen _primaryEdgePen = new(Brushes.Gray, 2.0);
    private Pen _branchEdgePen = new(Brushes.Gray, 1.5);
    private Pen _selectedPen = new(Brushes.White, 2.0);
    private readonly Dictionary<SetSlotRole, Pen> _rolePens = new();
    private Typeface _typeface = Typeface.Default;

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
        AffectsMeasure<StoryGraphView>(ViewModelProperty);
    }

    public StoryGraphView()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
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

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (ViewModel is { } vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ViewModel is { } vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
        _resourcesReady = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StoryGraphViewModel.Layout))
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (e.PropertyName == nameof(StoryGraphViewModel.SelectedSlotId))
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = ViewModel?.Layout?.Nodes.Count ?? 0;
        var width = count == 0 ? 0 : LeftPad * 2 + (count - 1) * StoryGraphLayout.ColumnWidth + NodeWidth;
        return new Size(width, TopPad * 2);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var vm = ViewModel;
        var layout = vm?.Layout;
        if (vm is null || layout is null)
            return;

        var point = e.GetPosition(this);
        foreach (var node in layout.Nodes)
        {
            if (NodeRect(node).Contains(point))
            {
                vm.SelectedSlotId = node.SlotId;
                e.Handled = true;
                return;
            }
        }

        vm.SelectedSlotId = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var vm = ViewModel;
        var layout = vm?.Layout;
        if (vm is null || layout is null)
            return;

        EnsureResources();

        foreach (var edge in layout.Edges)
        {
            var pen = edge.IsPrimary ? _primaryEdgePen : _branchEdgePen;
            var start = new Point(LeftPad + edge.StartX + NodeWidth, TopPad);
            var end = new Point(LeftPad + edge.EndX, TopPad);

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

        var selected = vm.SelectedSlotId;
        foreach (var node in layout.Nodes)
        {
            var rect = NodeRect(node);
            var border = node.SlotId == selected ? _selectedPen : RolePen(node.Role);
            context.DrawRectangle(_nodeFill, border, rect, 4, 4);

            var role = Text(node.Role.ToString().ToUpperInvariant(), 11, _text2);
            context.DrawText(role, new Point(rect.X + TextInset, rect.Y + 6));

            var label = Text(vm.GetSlotLabel(node.SlotId), 12, _text0);
            context.DrawText(label, new Point(rect.X + TextInset, rect.Y + 22));

            if (node.EnergyTarget is double energy)
            {
                var barWidth = (NodeWidth - TextInset * 2) * (energy / 100.0);
                var bar = new Rect(rect.X + TextInset, rect.Bottom - 8, barWidth, 3.0);
                context.DrawRectangle(_energyBrush, null, bar);
            }
        }
    }

    private static Rect NodeRect(StoryGraphNode node)
        => new(LeftPad + node.X, TopPad - NodeHeight / 2.0, NodeWidth, NodeHeight);

    private Pen RolePen(SetSlotRole role)
        => _rolePens.TryGetValue(role, out var pen) ? pen : _primaryEdgePen;

    private IBrush Res(string key, IBrush fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    /// <summary>Resolves design tokens once (and again after re-attach); Render never allocates pens.</summary>
    private void EnsureResources()
    {
        if (_resourcesReady)
            return;
        _resourcesReady = true;

        _text0 = Res("BrushText0", Brushes.White);
        _text2 = Res("BrushText2", Brushes.Gray);
        _nodeFill = Res("BrushBg3", Brushes.Transparent);
        _energyBrush = Res("BrushAccent", Brushes.Orange);
        _primaryEdgePen = new Pen(Res("BrushBorder2", Brushes.Gray), 2.0);
        _branchEdgePen = new Pen(Res("BrushInfo", Brushes.Gray), 1.5);
        _selectedPen = new Pen(Res("BrushAccent", Brushes.White), 2.0);

        // Interim role colours from existing tokens; dedicated role tokens arrive with the Sets page (P2.12).
        _rolePens.Clear();
        _rolePens[SetSlotRole.Opening] = new Pen(Res("BrushSuccess", Brushes.Gray), 1.0);
        _rolePens[SetSlotRole.Builder] = new Pen(Res("BrushBorder2", Brushes.Gray), 1.0);
        _rolePens[SetSlotRole.Atmosphere] = new Pen(Res("BrushStemOther", Brushes.Gray), 1.0);
        _rolePens[SetSlotRole.Peak] = new Pen(Res("BrushWarning", Brushes.Gray), 1.0);
        _rolePens[SetSlotRole.Reset] = new Pen(Res("BrushInfo", Brushes.Gray), 1.0);
        _rolePens[SetSlotRole.Closer] = new Pen(Res("BrushError", Brushes.Gray), 1.0);
        _rolePens[SetSlotRole.Wildcard] = new Pen(Res("BrushText3", Brushes.Gray), 1.0);

        _typeface = this.TryFindResource("FontUI", out var f) && f is FontFamily family
            ? new Typeface(family)
            : Typeface.Default;
        _textCache.Clear();
    }

    private FormattedText Text(string text, double size, IBrush brush)
    {
        var key = size.ToString(CultureInfo.InvariantCulture) + "|" + brush.GetHashCode() + "|" + text;
        if (_textCache.TryGetValue(key, out var cached))
            return cached;
        if (_textCache.Count > 500)
            _textCache.Clear();

        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, size, brush)
        {
            MaxTextWidth = NodeWidth - TextInset * 2,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        _textCache[key] = formatted;
        return formatted;
    }
}
