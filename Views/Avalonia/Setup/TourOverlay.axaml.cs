using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private const double CardWidth = 320;
    private const double CardHeight = 180;

    private QuickTourViewModel? _vm;
    private Rect _lastTarget = new(-1, -1, 0, 0);
    private Size _lastSize;
    private bool _repositioning;

    // Resolved once from the XAML name scope, then cached. They are plain children of this control,
    // so they are always present after InitializeComponent; a miss is handled gracefully instead of
    // a null-forgiving crash on the layout thread (which took the app down on fresh installs).
    private Control? _scrimTop;
    private Control? _scrimBottom;
    private Control? _scrimLeft;
    private Control? _scrimRight;
    private Control? _highlight;
    private Control? _card;

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
        if (e.PropertyName is not (nameof(QuickTourViewModel.CurrentTarget) or nameof(QuickTourViewModel.IsOpen))) return;

        // Keep the highlighted nav item in view (the sidebar scrolls), then re-place the coach mark.
        var target = FindTarget(_vm?.CurrentTarget);
        if (target is { IsEffectivelyVisible: true })
            target.BringIntoView();
        Dispatcher.UIThread.Post(() => Reposition(force: true), DispatcherPriority.Loaded);
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

    /// <summary>Caches the six named children; false when any is missing (nothing to position, never a crash).</summary>
    private bool TryEnsureParts()
    {
        _scrimTop ??= this.FindControl<Control>("ScrimTop");
        _scrimBottom ??= this.FindControl<Control>("ScrimBottom");
        _scrimLeft ??= this.FindControl<Control>("ScrimLeft");
        _scrimRight ??= this.FindControl<Control>("ScrimRight");
        _highlight ??= this.FindControl<Control>("Highlight");
        _card ??= this.FindControl<Control>("Card");
        return _scrimTop != null && _scrimBottom != null && _scrimLeft != null
               && _scrimRight != null && _highlight != null && _card != null;
    }

    private void Reposition(bool force)
    {
        if (_vm is not { IsOpen: true }) return;
        if (_repositioning) return; // LayoutUpdated re-entrancy: one pass at a time.
        _repositioning = true;
        try
        {
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0) return; // not measured yet; LayoutUpdated will call back

            var target = FindTarget(_vm.CurrentTarget);
            Rect rect;
            if (target is { IsEffectivelyVisible: true }
                && target.TranslatePoint(new Point(0, 0), this) is { } origin
                && target.Bounds.Width > 0 && target.Bounds.Height > 0)
            {
                rect = new Rect(origin, target.Bounds.Size).Inflate(HighlightInflate);
            }
            else
            {
                // Target hidden mid-tour: centre the card rather than throwing on a missing target.
                rect = new Rect(size.Width / 2, size.Height / 2, 0, 0);
            }

            if (!force && rect == _lastTarget && size == _lastSize) return;
            _lastTarget = rect;
            _lastSize = size;

            if (!TryEnsureParts()) return;

            Place(_scrimTop!, 0, 0, size.Width, Math.Max(0, rect.Top));
            Place(_scrimBottom!, 0, rect.Bottom, size.Width, Math.Max(0, size.Height - rect.Bottom));
            Place(_scrimLeft!, 0, rect.Top, Math.Max(0, rect.Left), rect.Height);
            Place(_scrimRight!, rect.Right, rect.Top, Math.Max(0, size.Width - rect.Right), rect.Height);

            _highlight!.IsVisible = rect.Width > 0;
            Place(_highlight, rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));

            PlaceCard(_card!, rect, size);
        }
        catch (Exception ex)
        {
            // Best-effort positioning must never take the app down; surface it for diagnosis.
            Serilog.Log.Warning(ex, "Quick tour: positioning the coach mark failed");
        }
        finally
        {
            _repositioning = false;
        }
    }

    /// <summary>Places the card next to the target: right, then left, then below, then above.</summary>
    private void PlaceCard(Control card, Rect rect, Size size)
    {
        var cardWidth = card.Bounds.Width > 0 ? card.Bounds.Width : CardWidth;
        var cardHeight = card.Bounds.Height > 0 ? card.Bounds.Height : CardHeight;

        double x, y;
        if (rect.Width <= 0)
        {
            x = (size.Width - cardWidth) / 2;
            y = (size.Height - cardHeight) / 2;
        }
        else if (size.Width - (rect.Right + Gap) >= cardWidth)
        {
            x = rect.Right + Gap;
            y = rect.Top;
        }
        else if (rect.Left - Gap >= cardWidth)
        {
            x = rect.Left - Gap - cardWidth;
            y = rect.Top;
        }
        else if (size.Height - (rect.Bottom + Gap) >= cardHeight)
        {
            x = rect.Left;
            y = rect.Bottom + Gap;
        }
        else
        {
            x = rect.Left;
            y = rect.Top - Gap - cardHeight;
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

    /// <summary>Clicking the dimmed backdrop advances the tour (button clicks are handled and never reach here).</summary>
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _vm is not { IsOpen: true }) return;
        // The card (Next / Back / Skip) is its own click target.
        if (_card != null && e.Source is Visual source && _card.IsVisualAncestorOf(source)) return;
        _vm.Next();
        e.Handled = true;
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
