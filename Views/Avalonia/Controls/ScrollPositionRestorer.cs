using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Applies a saved vertical offset after ItemsRepeater has measured its new ItemsSource. A direct
/// Offset assignment during project selection is too early: the new extent is still zero and the
/// ScrollViewer clamps the requested position away. A bounded, generation-based retry avoids that
/// race without allowing an old request to fight a newer project or the user's next scroll action.
/// </summary>
internal static class ScrollPositionRestorer
{
    private const int MaxAttempts = 6;
    private static readonly ConditionalWeakTable<ScrollViewer, RequestState> Requests = new();

    public static void Restore(ScrollViewer scroller, double offsetY)
    {
        var state = Requests.GetOrCreateValue(scroller);
        var generation = ++state.Generation;
        var requested = double.IsFinite(offsetY) ? Math.Max(0, offsetY) : 0;
        Dispatcher.UIThread.Post(
            () => Apply(scroller, requested, generation, attempt: 1),
            DispatcherPriority.Loaded);
    }

    private static void Apply(ScrollViewer scroller, double requested, int generation, int attempt)
    {
        if (!Requests.TryGetValue(scroller, out var state) || state.Generation != generation) return;

        var extent = scroller.Extent.Height;
        var viewport = scroller.Viewport.Height;
        var layoutPending = extent <= 0 || viewport <= 0;
        var target = ClampOffset(requested, extent, viewport);

        if (!layoutPending)
            scroller.Offset = new Vector(scroller.Offset.X, target);

        var extentStillGrowing = requested > 0 && target + 1 < requested;
        if (attempt < MaxAttempts && (layoutPending || extentStillGrowing))
        {
            Dispatcher.UIThread.Post(
                () => Apply(scroller, requested, generation, attempt + 1),
                DispatcherPriority.Background);
        }
    }

    internal static double ClampOffset(double requested, double extent, double viewport)
    {
        if (!double.IsFinite(requested) || requested <= 0) return 0;
        if (!double.IsFinite(extent) || !double.IsFinite(viewport)) return 0;
        return Math.Clamp(requested, 0, Math.Max(0, extent - viewport));
    }

    private sealed class RequestState
    {
        public int Generation { get; set; }
    }
}
