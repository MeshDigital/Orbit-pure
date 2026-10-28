using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using SLSKDONET.ViewModels;
using SLSKDONET.Utils;
using Avalonia.Skia;

namespace SLSKDONET.Views.Avalonia.Controls
{
    /// <summary>
    /// Animated "genre galaxy" — each genre orbits as a coloured planet, sized by track count,
    /// with faint constellation links to nearby genres. Clickable: tapping a planet raises
    /// <see cref="SelectGenreCommand"/> with that genre so the host page can navigate to it.
    /// </summary>
    public class GenreGalaxyCanvas : Control
    {
        public static readonly StyledProperty<IEnumerable<GenrePlanetViewModel>?> GenresProperty =
            AvaloniaProperty.Register<GenreGalaxyCanvas, IEnumerable<GenrePlanetViewModel>?>(nameof(Genres));

        public IEnumerable<GenrePlanetViewModel>? Genres
        {
            get => GetValue(GenresProperty);
            set => SetValue(GenresProperty, value);
        }

        public static readonly StyledProperty<ICommand?> SelectGenreCommandProperty =
            AvaloniaProperty.Register<GenreGalaxyCanvas, ICommand?>(nameof(SelectGenreCommand));

        public ICommand? SelectGenreCommand
        {
            get => GetValue(SelectGenreCommandProperty);
            set => SetValue(SelectGenreCommandProperty, value);
        }

        private float _animationValue;
        private readonly DispatcherTimer _timer;

        /// <summary>Screen-space positions computed on the most recent frame, used for
        /// hit-testing pointer clicks/hover against the (constantly moving) planets.</summary>
        private List<PositionedGenre>? _lastNodes;

        public GenreGalaxyCanvas()
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnTimerTick);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ResolveThemeColors();
            _timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _timer.Stop();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _animationValue += 0.005f;
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Cursor = HitTestNode(e.GetPosition(this)) != null
                ? new Cursor(StandardCursorType.Hand)
                : Cursor.Default;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var hit = HitTestNode(e.GetPosition(this));
            if (hit == null) return;

            if (SelectGenreCommand?.CanExecute(hit.Genre) == true)
            {
                SelectGenreCommand.Execute(hit.Genre);
            }
            e.Handled = true;
        }

        private PositionedGenre? HitTestNode(Point p)
        {
            if (_lastNodes == null) return null;

            foreach (var node in _lastNodes)
            {
                double dx = p.X - node.X;
                double dy = p.Y - node.Y;
                // A little extra padding beyond the drawn radius makes small planets easier to tap.
                if ((dx * dx) + (dy * dy) <= (node.NodeSize + 4) * (node.NodeSize + 4))
                {
                    return node;
                }
            }

            return null;
        }

        public override void Render(DrawingContext context)
        {
            var rect = Bounds;
            var genres = Genres?.ToList() ?? new List<GenrePlanetViewModel>();

            if (genres.Count == 0)
            {
                _lastNodes = null;
                base.Render(context);
                return;
            }

            float centerX = (float)rect.Width / 2f;
            float centerY = (float)rect.Height / 2f;
            var nodes = new List<PositionedGenre>(genres.Count);

            // Dashboard tiles are wide but short, so orbits are elliptical (scaled independently
            // per axis) rather than circular — a circle sized to fit the short axis would waste
            // most of the available width and force genres into a tight, overlapping cluster.
            // Leave headroom for the largest possible node radius (46) plus its label above it.
            float maxRadiusX = Math.Max(60f, centerX - 70f);
            float maxRadiusY = Math.Max(40f, centerY - 60f);
            float stepX = genres.Count > 1 ? maxRadiusX / genres.Count : 0f;
            float stepY = genres.Count > 1 ? maxRadiusY / genres.Count : 0f;
            float innerRadiusX = genres.Count > 1 ? maxRadiusX * 0.22f : 0f;
            float innerRadiusY = genres.Count > 1 ? maxRadiusY * 0.22f : 0f;

            for (int i = 0; i < genres.Count; i++)
            {
                var genre = genres[i];

                // Simple deterministic orbital math: radius expands per index, faster inner
                // rings, slower outer rings, alternating directions so it doesn't look like a
                // single spinning wheel.
                float radiusX = innerRadiusX + (i * stepX);
                float radiusY = innerRadiusY + (i * stepY);
                float speed = 1.5f / (i + 1f);
                float direction = (i % 2 == 0) ? 1f : -1f;
                float angleOffset = i * (float)(Math.PI * 2 / genres.Count);
                float pulse = (float)Math.Sin(_animationValue * 2f + i) * 2f;
                float nodeSize = Math.Clamp((float)(genre.Size / 2.0) + pulse, 12f, 40f);
                float currentAngle = angleOffset + (_animationValue * speed * direction);

                float x = centerX + (float)Math.Cos(currentAngle) * radiusX;
                float y = centerY + (float)Math.Sin(currentAngle) * radiusY;

                nodes.Add(new PositionedGenre(genre, x, y, nodeSize, radiusX, radiusY, ParseColor(genre.Color)));
            }

            _lastNodes = nodes;

            // Label layout on the UI thread (measuring with UI-thread-only paints); the draw
            // operation only paints what it is handed.
            var inputs = nodes.Select((n, i) => new GalaxyLabelInput(n.Genre.Name, n.X, n.Y, n.NodeSize,
                n.Genre.Name, CountFormat.N(n.Genre.Count))).ToList();
            var placements = GenreGalaxyLabelLayout.Layout(inputs, (float)rect.Width, (float)rect.Height,
                s => MeasureLabelPaint.MeasureText(s), LabelHeight,
                s => MeasureCountPaint.MeasureText(s), CountHeight,
                _previousLabelSpot);

            context.Custom(new GenreGalaxyDrawOperation(rect, nodes, placements, _textColor, _haloColor));
        }

        private const float LabelHeight = 15f;
        private const float CountHeight = 13f;

        // Measurement only, UI thread only (the draw operation has its own paints).
        private static readonly SKPaint MeasureLabelPaint = new() { IsAntialias = true, TextSize = 12, Typeface = SKTypeface.Default };
        private static readonly SKPaint MeasureCountPaint = new() { IsAntialias = true, TextSize = 10, Typeface = GalaxyPaints.BoldTypeface };

        /// <summary>Last frame's label spot per genre, so labels don't jump around as planets move.</summary>
        private readonly Dictionary<string, int> _previousLabelSpot = new();

        private SKColor _textColor = new(0xE8, 0xEA, 0xED);
        private SKColor _haloColor = new(0x0F, 0x10, 0x12);

        private void ResolveThemeColors()
        {
            // UI thread: resolve design tokens once so the render thread never touches resources.
            if (this.TryFindResource("ColorText0", ActualThemeVariant, out var t) && t is Color tc)
                _textColor = new SKColor(tc.R, tc.G, tc.B, tc.A);
            if (this.TryFindResource("ColorBase0", ActualThemeVariant, out var h) && h is Color hc)
                _haloColor = new SKColor(hc.R, hc.G, hc.B, hc.A);
        }

        private static SKColor ParseColor(string hex) =>
            SKColor.TryParse(hex, out var c) ? c : new SKColor(0, 163, 255);

        internal sealed class PositionedGenre
        {
            public GenrePlanetViewModel Genre { get; }
            public float X { get; }
            public float Y { get; }
            public float NodeSize { get; }
            public float OrbitRadiusX { get; }
            public float OrbitRadiusY { get; }
            public SKColor Color { get; }

            public PositionedGenre(GenrePlanetViewModel genre, float x, float y, float nodeSize, float orbitRadiusX, float orbitRadiusY, SKColor color)
            {
                Genre = genre;
                X = x;
                Y = y;
                NodeSize = nodeSize;
                OrbitRadiusX = orbitRadiusX;
                OrbitRadiusY = orbitRadiusY;
                Color = color;
            }
        }

        /// <summary>
        /// Paints created once and reused every frame (30 fps): no SKPaint/SKShader/typeface
        /// allocations on the render thread. They are only touched under <see cref="Sync"/>, so two
        /// draw operations can never mutate them at the same time.
        /// </summary>
        private static class GalaxyPaints
        {
            public static readonly object Sync = new();
            public static readonly SKTypeface BoldTypeface =
                SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

            public static readonly SKPaint Ring = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            public static readonly SKPaint Fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
            public static readonly SKPaint Stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            public static readonly SKPaint Link = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            public static readonly SKPaint Leader = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };

            public static readonly SKPaint Label = new() { IsAntialias = true, TextSize = 12, Typeface = SKTypeface.Default, TextAlign = SKTextAlign.Center };
            public static readonly SKPaint LabelHalo = new()
            {
                IsAntialias = true, TextSize = 12, Typeface = SKTypeface.Default, TextAlign = SKTextAlign.Center,
                Style = SKPaintStyle.Stroke, StrokeWidth = 3f, StrokeJoin = SKStrokeJoin.Round,
            };
            public static readonly SKPaint Count = new() { IsAntialias = true, TextSize = 10, Typeface = BoldTypeface, TextAlign = SKTextAlign.Center };
            public static readonly SKPaint CountHalo = new()
            {
                IsAntialias = true, TextSize = 10, Typeface = BoldTypeface, TextAlign = SKTextAlign.Center,
                Style = SKPaintStyle.Stroke, StrokeWidth = 3f, StrokeJoin = SKStrokeJoin.Round,
            };
        }

        private sealed class GenreGalaxyDrawOperation : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly List<PositionedGenre> _nodes;
            private readonly List<GalaxyLabelPlacement> _labels;
            private readonly SKColor _text;
            private readonly SKColor _halo;

            public GenreGalaxyDrawOperation(Rect bounds, List<PositionedGenre> nodes, List<GalaxyLabelPlacement> labels, SKColor text, SKColor halo)
            {
                _bounds = bounds;
                _nodes = nodes;
                _labels = labels;
                _text = text;
                _halo = halo;
            }

            public void Dispose() { } // paints are shared and long-lived (GalaxyPaints)
            public bool Equals(ICustomDrawOperation? other) => false;
            public Rect Bounds => _bounds;
            // Unlike the purely decorative visualizers this was templated from, the galaxy is
            // clickable — the whole bounds must be hit-testable so pointer input reaches
            // OnPointerPressed, which then does the precise per-node distance check.
            public bool HitTest(Point p) => _bounds.Contains(p);

            public void Render(ImmediateDrawingContext context)
            {
                try
                {
                    var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                    if (lease == null) return;

                    using var skiaContext = lease.Lease();
                    var canvas = skiaContext.SkCanvas;

                    float centerX = (float)_bounds.Width / 2f;
                    float centerY = (float)_bounds.Height / 2f;

                    lock (GalaxyPaints.Sync)
                    {
                        canvas.Save();

                        // Faint orbit rings, one per genre, fading out for the outer (larger) rings.
                        for (int i = 0; i < _nodes.Count; i++)
                        {
                            GalaxyPaints.Ring.Color = SKColors.White.WithAlpha((byte)Math.Max(2, 12 - (i * 2)));
                            canvas.DrawOval(new SKRect(
                                centerX - _nodes[i].OrbitRadiusX, centerY - _nodes[i].OrbitRadiusY,
                                centerX + _nodes[i].OrbitRadiusX, centerY + _nodes[i].OrbitRadiusY), GalaxyPaints.Ring);
                        }

                        // Constellation links behind the planets.
                        for (int i = 0; i < _nodes.Count; i++)
                        {
                            for (int j = i + 1; j < _nodes.Count; j++)
                            {
                                float dx = _nodes[i].X - _nodes[j].X;
                                float dy = _nodes[i].Y - _nodes[j].Y;
                                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                                if (dist < 100)
                                {
                                    GalaxyPaints.Link.Color = SKColors.White.WithAlpha((byte)Math.Clamp(80 - (dist * 0.8f), 0, 80));
                                    canvas.DrawLine(_nodes[i].X, _nodes[i].Y, _nodes[j].X, _nodes[j].Y, GalaxyPaints.Link);
                                }
                            }
                        }

                        // Planets: glow, body, a soft highlight (no per-frame shader), rim.
                        foreach (var node in _nodes)
                        {
                            GalaxyPaints.Fill.Color = node.Color.WithAlpha(45);
                            canvas.DrawCircle(node.X, node.Y, node.NodeSize * 1.8f, GalaxyPaints.Fill);
                            GalaxyPaints.Fill.Color = node.Color.WithAlpha(200);
                            canvas.DrawCircle(node.X, node.Y, node.NodeSize, GalaxyPaints.Fill);
                            GalaxyPaints.Fill.Color = SKColors.White.WithAlpha(28);
                            canvas.DrawCircle(node.X - node.NodeSize * 0.3f, node.Y - node.NodeSize * 0.3f, node.NodeSize * 0.55f, GalaxyPaints.Fill);
                            GalaxyPaints.Stroke.Color = node.Color.WithAlpha(140);
                            canvas.DrawCircle(node.X, node.Y, node.NodeSize, GalaxyPaints.Stroke);
                        }

                        // Text last, on top of every planet, each with a dark halo so it reads on any colour.
                        GalaxyPaints.Leader.Color = _text.WithAlpha(90);
                        GalaxyPaints.Label.Color = _text;
                        GalaxyPaints.LabelHalo.Color = _halo.WithAlpha(210);
                        GalaxyPaints.Count.Color = _text;
                        GalaxyPaints.CountHalo.Color = _halo.WithAlpha(190);
                        foreach (var l in _labels)
                        {
                            if (l.Leader)
                                canvas.DrawLine(l.LeaderFromX, l.LeaderFromY, l.LeaderToX, l.LeaderToY, GalaxyPaints.Leader);

                            if (l.CountInside)
                            {
                                float cy = l.CountBox.Bottom - 3f;
                                canvas.DrawText(l.Count, l.CountBox.CenterX, cy, GalaxyPaints.CountHalo);
                                canvas.DrawText(l.Count, l.CountBox.CenterX, cy, GalaxyPaints.Count);
                            }

                            float baseline = l.Box.Bottom - 4f;
                            canvas.DrawText(l.Text, l.Box.CenterX, baseline, GalaxyPaints.LabelHalo);
                            canvas.DrawText(l.Text, l.Box.CenterX, baseline, GalaxyPaints.Label);
                        }

                        canvas.Restore();
                    }
                }
                catch (Exception ex)
                {
                    // Render-thread exceptions bypass all managed exception handling and hard-crash
                    // the process with zero trace. Skip the frame instead of taking the app down.
                    Serilog.Log.Warning(ex, "GenreGalaxyCanvas: render tick failed — skipping frame");
                }
            }
        }
    }
}
