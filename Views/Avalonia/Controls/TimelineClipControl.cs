using System;
using System.Linq;
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
    public static readonly StyledProperty<System.Collections.Generic.IEnumerable<PhraseSegment>?> PhraseSegmentsProperty =
        AvaloniaProperty.Register<TimelineClipControl, System.Collections.Generic.IEnumerable<PhraseSegment>?>(nameof(PhraseSegments));

    public System.Collections.Generic.IEnumerable<PhraseSegment>? PhraseSegments
    {
        get => GetValue(PhraseSegmentsProperty);
        set => SetValue(PhraseSegmentsProperty, value);
    }

    public static readonly StyledProperty<float> BpmProperty =
        AvaloniaProperty.Register<TimelineClipControl, float>(nameof(Bpm), 0f);

    public float Bpm
    {
        get => GetValue(BpmProperty);
        set => SetValue(BpmProperty, value);
    }

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
            PhraseSegmentsProperty,
            BpmProperty,
            FadeInFractionProperty,
            FadeOutFractionProperty);
    }

    // ── Render ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        // Snapshot every StyledProperty value here, on the UI thread, before handing off
        // to the draw operation — ClipDrawOperation.Render runs on the render/compositor
        // thread, and reading AvaloniaProperty getters there throws with zero trace
        // (confirmed root cause of a prior crash class in LiveBackground/OrbitVisualizerCanvas).
        context.Custom(new ClipDrawOperation(
            bounds,
            ClipColor,
            WaveformData,
            PhraseSegments?.ToList(),
            ZoomLevel,
            ScrollOffset,
            Bpm,
            FadeInFraction,
            FadeOutFraction,
            ClipLabel));
    }

    // ── Inner draw operation ───────────────────────────────────────────────

    private sealed class ClipDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly Color _clipColor;
        private readonly WaveformAnalysisData? _waveformData;
        private readonly System.Collections.Generic.List<PhraseSegment>? _phraseSegments;
        private readonly double _zoomLevel;
        private readonly double _scrollOffset;
        private readonly float _bpm;
        private readonly double _fadeInFraction;
        private readonly double _fadeOutFraction;
        private readonly string _clipLabel;

        public ClipDrawOperation(
            Rect bounds,
            Color clipColor,
            WaveformAnalysisData? waveformData,
            System.Collections.Generic.List<PhraseSegment>? phraseSegments,
            double zoomLevel,
            double scrollOffset,
            float bpm,
            double fadeInFraction,
            double fadeOutFraction,
            string clipLabel)
        {
            _bounds = bounds;
            _clipColor = clipColor;
            _waveformData = waveformData;
            _phraseSegments = phraseSegments;
            _zoomLevel = zoomLevel;
            _scrollOffset = scrollOffset;
            _bpm = bpm;
            _fadeInFraction = fadeInFraction;
            _fadeOutFraction = fadeOutFraction;
            _clipLabel = clipLabel;
        }

        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            try
            {
                var lease = context.TryGetFeature(typeof(ISkiaSharpApiLease)) as ISkiaSharpApiLease;
                if (lease is null) return;

                var canvas = lease.SkCanvas;

                int w = Math.Max(1, (int)_bounds.Width);
                int h = Math.Max(1, (int)_bounds.Height);

                var clipColor = new SKColor(
                    _clipColor.R,
                    _clipColor.G,
                    _clipColor.B,
                    200);

                var bgColor = new SKColor(
                    (byte)(_clipColor.R / 4),
                    (byte)(_clipColor.G / 4),
                    (byte)(_clipColor.B / 4),
                    230);

                // ── Background ────────────────────────────────────────────────
                using var bgPaint = new SKPaint { Color = bgColor, IsAntialias = false };
                canvas.DrawRect(0, 0, w, h, bgPaint);

                // ── Waveform ──────────────────────────────────────────────────
                var data = _waveformData;
                if (data is not null && !data.IsEmpty)
                {
                    using var wfBmp = WaveformRenderer.RenderFromWaveformData(
                        data, w, h,
                        waveColor: clipColor,
                        bgColor: SKColors.Transparent,
                        zoom: _zoomLevel,
                        scrollOffset: _scrollOffset);

                    if (_phraseSegments is { Count: > 0 })
                    {
                        WaveformRenderer.OverlayPhraseSections(
                            wfBmp,
                            _phraseSegments,
                            data.DurationSeconds,
                            _bpm,
                            _zoomLevel,
                            _scrollOffset);
                    }

                    canvas.DrawBitmap(wfBmp, 0, 0);
                }

                // ── Fade-in triangle ──────────────────────────────────────────
                float fadeInW = (float)(_fadeInFraction * w);
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
                float fadeOutW = (float)(_fadeOutFraction * w);
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
                if (!string.IsNullOrWhiteSpace(_clipLabel))
                {
                    using var labelPaint = new SKPaint
                    {
                        Color = SKColors.White,
                        IsAntialias = true,
                        TextSize = Math.Clamp(h * 0.22f, 10f, 14f)
                    };
                    canvas.DrawText(_clipLabel, 4, labelPaint.TextSize + 2, labelPaint);
                }
            }
            catch (Exception ex)
            {
                // Render-thread exceptions bypass all managed exception handling
                // (AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException never
                // see them) and hard-crash the process with zero trace. Skip the frame instead.
                Serilog.Log.Warning(ex, "TimelineClipControl: render tick failed — skipping frame");
            }
        }

        public void Dispose() { }
    }
}
