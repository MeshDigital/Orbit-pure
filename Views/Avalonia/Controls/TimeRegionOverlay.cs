using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Shades a time range over a <see cref="WaveformControl"/> that uses the same
/// <see cref="ZoomLevel"/>/<see cref="ViewOffset"/> mapping (fraction of the track → x), so the
/// Flow Builder transition editor can show the overlap window on both decks without changing the
/// shared waveform control. Drawn in <see cref="Render"/> on the UI thread (not a custom draw
/// operation), so reading styled properties here is safe.
/// </summary>
public sealed class TimeRegionOverlay : Control
{
    public static readonly StyledProperty<double> DurationSecondsProperty =
        AvaloniaProperty.Register<TimeRegionOverlay, double>(nameof(DurationSeconds));
    public static readonly StyledProperty<double> StartSecondsProperty =
        AvaloniaProperty.Register<TimeRegionOverlay, double>(nameof(StartSeconds));
    public static readonly StyledProperty<double> EndSecondsProperty =
        AvaloniaProperty.Register<TimeRegionOverlay, double>(nameof(EndSeconds));
    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<TimeRegionOverlay, double>(nameof(ZoomLevel), 1.0);
    public static readonly StyledProperty<double> ViewOffsetProperty =
        AvaloniaProperty.Register<TimeRegionOverlay, double>(nameof(ViewOffset));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TimeRegionOverlay, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> EdgeBrushProperty =
        AvaloniaProperty.Register<TimeRegionOverlay, IBrush?>(nameof(EdgeBrush));
    /// <summary>Draw the edge line at the region start instead of its end (an outro region starts at its marker).</summary>
    public static readonly StyledProperty<bool> EdgeAtStartProperty =
        AvaloniaProperty.Register<TimeRegionOverlay, bool>(nameof(EdgeAtStart));

    static TimeRegionOverlay()
    {
        AffectsRender<TimeRegionOverlay>(DurationSecondsProperty, StartSecondsProperty, EndSecondsProperty,
            ZoomLevelProperty, ViewOffsetProperty, FillProperty, EdgeBrushProperty, EdgeAtStartProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<TimeRegionOverlay>(false);
    }

    public double DurationSeconds { get => GetValue(DurationSecondsProperty); set => SetValue(DurationSecondsProperty, value); }
    public double StartSeconds { get => GetValue(StartSecondsProperty); set => SetValue(StartSecondsProperty, value); }
    public double EndSeconds { get => GetValue(EndSecondsProperty); set => SetValue(EndSecondsProperty, value); }
    public double ZoomLevel { get => GetValue(ZoomLevelProperty); set => SetValue(ZoomLevelProperty, value); }
    public double ViewOffset { get => GetValue(ViewOffsetProperty); set => SetValue(ViewOffsetProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? EdgeBrush { get => GetValue(EdgeBrushProperty); set => SetValue(EdgeBrushProperty, value); }
    public bool EdgeAtStart { get => GetValue(EdgeAtStartProperty); set => SetValue(EdgeAtStartProperty, value); }

    private IBrush? _penBrush;
    private Pen? _edgePen;

    /// <summary>Maps a time to x the same way WaveformControl.FractionToX does.</summary>
    public static double TimeToX(double seconds, double duration, double zoom, double viewOffset, double width)
    {
        if (duration <= 0 || width <= 0) return 0;
        zoom = Math.Max(1.0, zoom);
        return (seconds / duration - viewOffset) * zoom * width;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (DurationSeconds <= 0 || EndSeconds <= StartSeconds || width <= 0) return;

        var x0 = Math.Clamp(TimeToX(StartSeconds, DurationSeconds, ZoomLevel, ViewOffset, width), 0, width);
        var x1 = Math.Clamp(TimeToX(EndSeconds, DurationSeconds, ZoomLevel, ViewOffset, width), 0, width);
        if (x1 - x0 < 0.5) return;

        if (Fill != null) context.FillRectangle(Fill, new Rect(x0, 0, x1 - x0, height));
        if (EdgeBrush != null)
        {
            if (!ReferenceEquals(_penBrush, EdgeBrush)) { _penBrush = EdgeBrush; _edgePen = new Pen(EdgeBrush, 1); }
            double ex = EdgeAtStart ? x0 : x1;
            context.DrawLine(_edgePen!, new Point(ex, 0), new Point(ex, height));
        }
    }
}
