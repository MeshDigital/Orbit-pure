using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SLSKDONET.ViewModels.Setup;

namespace SLSKDONET.Views.Avalonia.Setup;

/// <summary>
/// Positions the tour's highlight and card over the real navigation controls of the main window
/// (found by x:Name), and recomputes on every layout pass so resizing or collapsing the sidebar
/// keeps the highlight on target.
/// </summary>
public partial class TourOverlay : UserControl
{
    private const double Gap = 16;
    private const double HighlightInflate = 4;

    private QuickTourViewModel? _vm;
    private Rect _lastTarget = new(-1, -1, 0, 0);
    private Size _lastSize;

    public TourOverlay()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) => Attach(DataContext as QuickTourViewModel);
        LayoutUpdated += (_, _) => Reposition(force: false);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Attach(QuickTourViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.StartRequested -= OnStartRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.StartRequested += OnStartRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnStartRequested(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        if (_vm.Start(IsTargetVisible))
            Dispatcher.UIThread.Post(() => { Reposition(force: true); Focus(); }, DispatcherPriority.Loaded);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QuickTourViewModel.CurrentTarget) or nameof(QuickTourViewModel.IsOpen))
        {
            var target = FindTarget(_vm?.CurrentTarget);
            target?.BringIntoView();
            Dispatcher.UIThread.Post(() => Reposition(force: true), DispatcherPriority.Loaded);
        }
    }

    private Control? FindTarget(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return TopLevel.GetTopLevel(this) is Window window ? window.FindControl<Control>(name) : null;
    }

    private bool IsTargetVisible(string name)
    {
        var control = FindTarget(name);
        return control is { IsEffectivelyVisible: true } && control.Bounds.Width > 0 && control.Bounds.Height > 0
               && TopLevel.GetTopLevel(this) is { } top && control.TranslatePoint(new Point(0, 0), top) != null;
    }

    private void Reposition(bool force)
    {
        if (_vm is not { IsOpen: true }) return;

        var size = Bounds.Size;
        var target = FindTarget(_vm.CurrentTarget);
        Rect rect;
        if (target is { IsEffectivelyVisible: true } && target.TranslatePoint(new Point(0, 0), this) is { } origin
            && target.Bounds.Width > 0)
        {
            rect = new Rect(origin, target.Bounds.Size).Inflate(HighlightInflate);
        }
        else
        {
            rect = new Rect(size.Width / 2, size.Height / 2, 0, 0); // target hidden mid-tour: just centre the card
        }

        if (!force && rect == _lastTarget && size == _lastSize) return;
        _lastTarget = rect;
        _lastSize = size;

        var scrimTop = this.FindControl<Control>("ScrimTop")!;
        var scrimBottom = this.FindControl<Control>("ScrimBottom")!;
        var scrimLeft = this.FindControl<Control>("ScrimLeft")!;
        var scrimRight = this.FindControl<Control>("ScrimRight")!;
        var highlight = this.FindControl<Control>("Highlight")!;
        var card = this.FindControl<Control>("Card")!;

        Place(scrimTop, 0, 0, size.Width, Math.Max(0, rect.Top));
        Place(scrimBottom, 0, rect.Bottom, size.Width, Math.Max(0, size.Height - rect.Bottom));
        Place(scrimLeft, 0, rect.Top, Math.Max(0, rect.Left), rect.Height);
        Place(scrimRight, rect.Right, rect.Top, Math.Max(0, size.Width - rect.Right), rect.Height);

        highlight.IsVisible = rect.Width > 0;
        Place(highlight, rect.X, rect.Y, rect.Width, rect.Height);

        var cardWidth = card.Bounds.Width > 0 ? card.Bounds.Width : 320;
        var cardHeight = card.Bounds.Height > 0 ? card.Bounds.Height : 180;
        double x, y;
        if (rect.Width <= 0)
        {
            x = (size.Width - cardWidth) / 2;
            y = (size.Height - cardHeight) / 2;
        }
        else if (rect.Right + Gap + cardWidth <= size.Width - Gap)
        {
            x = rect.Right + Gap;
            y = rect.Top;
        }
        else if (rect.Left - Gap - cardWidth >= Gap)
        {
            x = rect.Left - Gap - cardWidth;
            y = rect.Top;
        }
        else
        {
            x = rect.Left;
            y = rect.Bottom + Gap;
        }
        Canvas.SetLeft(card, Math.Clamp(x, Gap, Math.Max(Gap, size.Width - cardWidth - Gap)));
        Canvas.SetTop(card, Math.Clamp(y, Gap, Math.Max(Gap, size.Height - cardHeight - Gap)));
    }

    private static void Place(Control control, double x, double y, double width, double height)
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        control.Width = Math.Max(0, width);
        control.Height = Math.Max(0, height);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm is not { IsOpen: true }) return;
        switch (e.Key)
        {
            case Key.Escape:
                _vm.SkipCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Enter when e.Source is not Button:
                _vm.Next();
                e.Handled = true;
                break;
            case Key.Left:
                _vm.Back();
                e.Handled = true;
                break;
        }
    }
}
