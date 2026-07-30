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

                nodes.Add(new PositionedGenre(genre, x, y, nodeSize, radiusX, radiusY));
            }

            _lastNodes = nodes;
            context.Custom(new GenreGalaxyDrawOperation(rect, nodes));
        }

        internal sealed class PositionedGenre
        {
            public GenrePlanetViewModel Genre { get; }
            public float X { get; }
            public float Y { get; }
            public float NodeSize { get; }
            public float OrbitRadiusX { get; }
            public float OrbitRadiusY { get; }

            public PositionedGenre(GenrePlanetViewModel genre, float x, float y, float nodeSize, float orbitRadiusX, float orbitRadiusY)
            {
                Genre = genre;
                X = x;
                Y = y;
                NodeSize = nodeSize;
                OrbitRadiusX = orbitRadiusX;
                OrbitRadiusY = orbitRadiusY;
            }
        }

        private sealed class GenreGalaxyDrawOperation : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly List<PositionedGenre> _nodes;

            private static readonly SKPaint OrbPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

            private static readonly SKPaint OrbStrokePaint = new()
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                Color = SKColors.White.WithAlpha(70)
            };

            private static readonly SKPaint GlowPaint = new()
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };

            private static readonly SKPaint TextPaint = new()
            {
                IsAntialias = true,
                Color = SKColors.White,
                TextSize = 12,
                Typeface = SKTypeface.Default,
                TextAlign = SKTextAlign.Center
            };

            private static readonly SKPaint CountTextPaint = new()
            {
                IsAntialias = true,
                Color = SKColors.White.WithAlpha(220),
                TextSize = 10,
                Typeface = SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
                TextAlign = SKTextAlign.Center
            };

            private static readonly SKPaint LinkPaint = new()
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
            };

            public GenreGalaxyDrawOperation(Rect bounds, List<PositionedGenre> nodes)
            {
                _bounds = bounds;
                _nodes = nodes;
            }

            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
            public Rect Bounds => _bounds;
            // Unlike the purely decorative visualizers this was templated from, the galaxy is
            // clickable — the whole bounds must be hit-testable so pointer input reaches
            // OnPointerPressed, which then does the precise per-node distance check.
            public bool HitTest(Point p) => _bounds.Contains(p);

            public void Render(ImmediateDrawingContext context)
            {
                var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (lease == null) return;

                using var skiaContext = lease.Lease();
                var canvas = skiaContext.SkCanvas;

                float centerX = (float)_bounds.Width / 2f;
                float centerY = (float)_bounds.Height / 2f;

                canvas.Save();

                // Faint orbit rings, one per genre, fading out for the outer (larger) rings.
                for (int i = 0; i < _nodes.Count; i++)
                {
                    using var ringPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1,
                        Color = SKColors.White.WithAlpha((byte)Math.Max(2, 12 - (i * 2)))
                    };
                    var ringRect = new SKRect(
                        centerX - _nodes[i].OrbitRadiusX, centerY - _nodes[i].OrbitRadiusY,
                        centerX + _nodes[i].OrbitRadiusX, centerY + _nodes[i].OrbitRadiusY);
                    canvas.DrawOval(ringRect, ringPaint);
                }

                foreach (var node in _nodes)
                {
                    var color = ParseColor(node.Genre.Color);

                    // Soft glow behind the planet — a larger, low-alpha copy of the same colour.
                    GlowPaint.Color = color.WithAlpha(50);
                    canvas.DrawCircle(node.X, node.Y, node.NodeSize * 1.8f, GlowPaint);

                    OrbPaint.Shader = SKShader.CreateRadialGradient(
                        new SKPoint(node.X - node.NodeSize * 0.3f, node.Y - node.NodeSize * 0.3f),
                        node.NodeSize * 1.3f,
                        new[] { color.WithAlpha(235), color.WithAlpha(160) },
                        null,
                        SKShaderTileMode.Clamp);
                    canvas.DrawCircle(node.X, node.Y, node.NodeSize, OrbPaint);
                    OrbPaint.Shader = null;

                    OrbStrokePaint.Color = color.WithAlpha(120);
                    canvas.DrawCircle(node.X, node.Y, node.NodeSize, OrbStrokePaint);

                    canvas.DrawText(node.Genre.Name, node.X, node.Y - node.NodeSize - 8, TextPaint);
                    canvas.DrawText(node.Genre.Count.ToString(), node.X, node.Y + 4, CountTextPaint);
                }

                // Constellation links between nearby planets.
                for (int i = 0; i < _nodes.Count; i++)
                {
                    for (int j = i + 1; j < _nodes.Count; j++)
                    {
                        float dx = _nodes[i].X - _nodes[j].X;
                        float dy = _nodes[i].Y - _nodes[j].Y;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (dist < 100)
                        {
                            byte alpha = (byte)Math.Clamp(80 - (dist * 0.8f), 0, 80);
                            LinkPaint.Color = SKColors.White.WithAlpha(alpha);
                            canvas.DrawLine(_nodes[i].X, _nodes[i].Y, _nodes[j].X, _nodes[j].Y, LinkPaint);
                        }
                    }
                }

                canvas.Restore();
            }

            private static SKColor ParseColor(string hex)
            {
                return SKColor.TryParse(hex, out var c) ? c : new SKColor(0, 163, 255);
            }
        }
    }
}
