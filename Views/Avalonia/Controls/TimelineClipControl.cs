using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SLSKDONET.Models;
using SLSKDONET.Services.Timeline;
using SkiaSharp;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Renders a single <see cref="Models.Timeline.TimelineClip"/> lane region:
/// a coloured background, optional waveform, clip label, and fade-in/out triangles.
/// Designed to be hosted inside a timeline canvas at a position determined by the
/// parent view-model using <see cref="Canvas.LeftProperty"/> and <see cref="Control.WidthProperty"/>.
/// </summary>
public class TimelineClipControl : Control
{
    // ── Styled properties ─────────────────────────────────────────────────

    public static readonly StyledProperty<WaveformAnalysisData?> WaveformDataProperty =
        AvaloniaProperty.Register<TimelineClipControl, WaveformAnalysisData?>(nameof(WaveformData));

    public WaveformAnalysisData? WaveformData
    {
        get => GetValue(WaveformDataProperty);
        set => SetValue(WaveformDataProperty, value);
    }

    public static readonly StyledProperty<string> ClipLabelProperty =
        AvaloniaProperty.Register<TimelineClipControl, string>(nameof(ClipLabel), string.Empty);

    public string ClipLabel
    {
        get => GetValue(ClipLabelProperty);
        set => SetValue(ClipLabelProperty, value);
    }

    public static readonly StyledProperty<Color> ClipColorProperty =
        AvaloniaProperty.Register<TimelineClipControl, Color>(nameof(ClipColor), Color.Parse("#4A90D9"));

    public Color ClipColor
    {
        get => GetValue(ClipColorProperty);
        set => SetValue(ClipColorProperty, value);
    }

    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<TimelineClipControl, double>(nameof(ZoomLevel), 1.0);

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, Math.Max(1.0, value));
    }

    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<TimelineClipControl, double>(nameof(ScrollOffset), 0.0);

    /// <summary>Fractional horizontal scroll in [0, 1].</summary>
    public double ScrollOffset
    {
        get => GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, Math.Clamp(value, 0.0, 1.0));
    }

    /// <summary>
    /// Fade-in width as a fraction of the total clip width [0, 0.5].
    /// Set to 0 to disable the fade-in triangle.
    /// </summary>
    public static readonly StyledProperty<double> FadeInFractionProperty =
        AvaloniaProperty.Register<TimelineClipControl, double>(nameof(FadeInFraction), 0.05);

    public double FadeInFraction
    {
        get => GetValue(FadeInFractionProperty);
        set => SetValue(FadeInFractionProperty, Math.Clamp(value, 0.0, 0.5));
    }

    public static readonly StyledProperty<double> FadeOutFractionProperty =
        AvaloniaProperty.Register<TimelineClipControl, double>(nameof(FadeOutFraction), 0.05);

    public double FadeOutFraction
    {
        get => GetValue(FadeOutFractionProperty);
        set => SetValue(FadeOutFractionProperty, Math.Clamp(value, 0.0, 0.5));
    }

    // ── Static ctor / invalidation ────────────────────────────────────────

    static TimelineClipControl()
    {
        AffectsRender<TimelineClipControl>(
            WaveformDataProperty,
            ClipLabelProperty,
            ClipColorProperty,
            ZoomLevelProperty,
            ScrollOffsetProperty,
            FadeInFractionProperty,
            FadeOutFractionProperty);
    }

    // ── Render ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.Custom(new ClipDrawOperation(bounds, this));
    }

    // ── Inner draw operation ───────────────────────────────────────────────

    private sealed class ClipDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly TimelineClipControl _ctrl;

        public ClipDrawOperation(Rect bounds, TimelineClipControl ctrl)
        {
            _bounds = bounds;
            _ctrl = ctrl;
        }

        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature(typeof(ISkiaSharpApiLease)) as ISkiaSharpApiLease;
            if (lease is null) return;

            var canvas = lease.SkCanvas;

            int w = Math.Max(1, (int)_bounds.Width);
            int h = Math.Max(1, (int)_bounds.Height);

            var clipColor = new SKColor(
                _ctrl.ClipColor.R,
                _ctrl.ClipColor.G,
                _ctrl.ClipColor.B,
                200);

            var bgColor = new SKColor(
                (byte)(_ctrl.ClipColor.R / 4),
                (byte)(_ctrl.ClipColor.G / 4),
                (byte)(_ctrl.ClipColor.B / 4),
                230);

            // ── Background ────────────────────────────────────────────────
            using var bgPaint = new SKPaint { Color = bgColor, IsAntialias = false };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            // ── Waveform ──────────────────────────────────────────────────
            var data = _ctrl.WaveformData;
            if (data is not null && !data.IsEmpty)
            {
                using var wfBmp = WaveformRenderer.RenderFromWaveformData(
                    data, w, h,
                    waveColor: clipColor,
                    bgColor: SKColors.Transparent,
                    zoom: _ctrl.ZoomLevel,
                    scrollOffset: _ctrl.ScrollOffset);
                canvas.DrawBitmap(wfBmp, 0, 0);
            }

            // ── Fade-in triangle ──────────────────────────────────────────
            float fadeInW = (float)(_ctrl.FadeInFraction * w);
            if (fadeInW > 1)
            {
                using var fadePaint = new SKPaint { Color = SKColors.Black.WithAlpha(140), IsAntialias = true };
                var path = new SKPath();
                path.MoveTo(0, 0);
                path.LineTo(fadeInW, 0);
                path.LineTo(0, h);
                path.Close();
                canvas.DrawPath(path, fadePaint);
            }

            // ── Fade-out triangle ─────────────────────────────────────────
            float fadeOutW = (float)(_ctrl.FadeOutFraction * w);
            if (fadeOutW > 1)
            {
                using var fadePaint = new SKPaint { Color = SKColors.Black.WithAlpha(140), IsAntialias = true };
                var path = new SKPath();
                path.MoveTo(w, 0);
                path.LineTo(w - fadeOutW, 0);
                path.LineTo(w, h);
                path.Close();
                canvas.DrawPath(path, fadePaint);
            }

            // ── Border ────────────────────────────────────────────────────
            using var borderPaint = new SKPaint
            {
                Color = clipColor,
                IsAntialias = false,
                IsStroke = true,
                StrokeWidth = 1.5f
            };
            canvas.DrawRect(0, 0, w - 1, h - 1, borderPaint);

            // ── Label ─────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(_ctrl.ClipLabel))
            {
                using var labelPaint = new SKPaint
                {
                    Color = SKColors.White,
                    IsAntialias = true,
                    TextSize = Math.Clamp(h * 0.22f, 10f, 14f)
                };
                canvas.DrawText(_ctrl.ClipLabel, 4, labelPaint.TextSize + 2, labelPaint);
            }
        }

        public void Dispose() { }
    }
}
