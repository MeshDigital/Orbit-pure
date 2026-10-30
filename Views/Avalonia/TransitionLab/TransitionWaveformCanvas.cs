using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Views.Avalonia.TransitionLab;

/// <summary>
/// Slice K6: native Avalonia dual-track overlap canvas. Renders Track A (outgoing) on top and
/// Track B (incoming) below, with beat-grid ticks and a highlighted bounding box marking the
/// <c>BarsBeforeMix</c> mix window. All geometry is computed by the pure
/// <see cref="TransitionWaveformLayout"/>; this control only paints.
/// </summary>
public sealed class TransitionWaveformCanvas : Control
{
    public static readonly StyledProperty<TransitionWaveformData?> SourceProperty =
        AvaloniaProperty.Register<TransitionWaveformCanvas, TransitionWaveformData?>(nameof(Source));

    public TransitionWaveformData? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    static TransitionWaveformCanvas()
    {
        AffectsRender<TransitionWaveformCanvas>(SourceProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var source = Source;
        if (source is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var width = Bounds.Width;
        var laneHeight = Bounds.Height / 2.0;

        DrawLane(context, source.Outgoing, 0.0, laneHeight, width, source.BarsBeforeMix, anchorIsEnd: true);
        DrawLane(context, source.Incoming, laneHeight, laneHeight, width, source.BarsBeforeMix, anchorIsEnd: false);
    }

    private static void DrawLane(
        DrawingContext context,
        TrackWaveformData track,
        double y,
        double height,
        double width,
        int barsBeforeMix,
        bool anchorIsEnd)
    {
        var profile = track.RmsProfile;
        if (profile is { Length: > 0 })
        {
            var half = height / 2.0;
            var barWidth = Math.Max(1.0, width / profile.Length);
            var wavePen = new Pen(Brushes.Teal, 1.0);
            for (var i = 0; i < profile.Length; i++)
            {
                var barHalf = profile[i] * half * 0.9;
                var x = i * barWidth;
                context.DrawLine(wavePen, new Point(x, y + half - barHalf), new Point(x, y + half + barHalf));
            }
        }

        var beatPen = new Pen(Brushes.Gray, 1.0);
        foreach (var x in TransitionWaveformLayout.BeatTickXs(track.BeatPositionsSeconds, track.DurationSeconds, width))
            context.DrawLine(beatPen, new Point(x, y), new Point(x, y + height));

        var (x1, x2) = TransitionWaveformLayout.MixWindowBox(
            track.MixAnchorSeconds, track.Bpm, barsBeforeMix, track.DurationSeconds, width, anchorIsEnd);
        var boxPen = new Pen(Brushes.Orange, 2.0);
        context.DrawRectangle(null, boxPen, new Rect(x1, y, Math.Max(0.0, x2 - x1), height));
    }
}
