using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.ViewModels;
using Avalonia.Skia;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public class GenreGalaxyCanvas : Control
    {
        public static readonly StyledProperty<IEnumerable<GenrePlanetViewModel>?> GenresProperty =
            AvaloniaProperty.Register<GenreGalaxyCanvas, IEnumerable<GenrePlanetViewModel>?>(nameof(Genres));

        public IEnumerable<GenrePlanetViewModel>? Genres
        {
            get => GetValue(GenresProperty);
            set => SetValue(GenresProperty, value);
        }

        private float _animationValue;
        private readonly DispatcherTimer _timer;

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

        public override void Render(DrawingContext context)
        {
            var rect = Bounds;
            var genres = Genres?.ToList() ?? new List<GenrePlanetViewModel>();

            if (!genres.Any())
            {
                base.Render(context);
                return;
            }

            context.Custom(new GenreGalaxyDrawOperation(rect, genres, _animationValue));
        }

        private class GenreGalaxyDrawOperation : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly List<GenrePlanetViewModel> _genres;
            private readonly float _animation;

            // Cached paints to avoid allocation per frame
            private static readonly SKPaint _orbPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            private static readonly SKPaint _orbStrokePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                Color = SKColors.White.WithAlpha(50)
            };

            private static readonly SKPaint _textPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White,
                TextSize = 12,
                Typeface = SKTypeface.Default,
                TextAlign = SKTextAlign.Center
            };
            
            private static readonly SKPaint _countTextPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White.WithAlpha(200),
                TextSize = 10,
                Typeface = SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
                TextAlign = SKTextAlign.Center
            };

            private static readonly SKPaint _linkPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                Color = SKColors.White.WithAlpha(30)
            };

            public GenreGalaxyDrawOperation(Rect bounds, List<GenrePlanetViewModel> genres, float animation)
            {
                _bounds = bounds;
                _genres = genres;
                _animation = animation;
            }

            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
            public Rect Bounds => _bounds;
            public bool HitTest(Point p) => false;

            public void Render(ImmediateDrawingContext context)
            {
                var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (lease == null) return;

                using var skiaContext = lease.Lease();
                var canvas = skiaContext.SkCanvas;

                float centerX = (float)_bounds.Width / 2f;
                float centerY = (float)_bounds.Height / 2f;
                
                // Track positions for drawing links
                var nodePositions = new List<SKPoint>();

                // Define colors
                SKColor leadColor = new SKColor(0, 163, 255); // #00A3FF

                canvas.Save();

                for (int i = 0; i < _genres.Count; i++)
                {
                    var genre = _genres[i];
                    
                    // Simple deterministic orbital math:
                    // Radius expands based on index (so smaller genres orbit further out, or vice versa)
                    // Angle incorporates index + time
                    float radius = 30f + (i * 25f);
                    
                    // Faster inner rings, slower outer rings. Alternating directions.
                    float speed = 1.5f / (i + 1f);
                    float direction = (i % 2 == 0) ? 1f : -1f;
                    
                    // Base rotation offset to distribute them
                    float angleOffset = i * (float)(Math.PI * 2 / _genres.Count);
                    
                    float pulse = (float)Math.Sin(_animation * 2f + i) * 3f;
                    float nodeSize = (float)(genre.Size / 2.0) + pulse;
                    
                    // Ensure size is within reasonable bounds
                    nodeSize = Math.Clamp(nodeSize, 10f, 60f);

                    float currentAngle = angleOffset + (_animation * speed * direction);
                    
                    float x = centerX + (float)Math.Cos(currentAngle) * radius;
                    float y = centerY + (float)Math.Sin(currentAngle) * radius;

                    nodePositions.Add(new SKPoint(x, y));

                    // Draw Orbit Ring (Faint)
                    using var ringPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1,
                        Color = SKColors.White.WithAlpha((byte)(10 - (i * 2))) // Fades out for outer rings
                    };
                    canvas.DrawCircle(centerX, centerY, radius, ringPaint);
                    
                    // Set Orb Color
                    _orbPaint.Color = (i == 0) ? leadColor.WithAlpha(180) : leadColor.WithAlpha(80);
                    
                    // Draw Orb
                    canvas.DrawCircle(x, y, nodeSize, _orbPaint);
                    canvas.DrawCircle(x, y, nodeSize, _orbStrokePaint);

                    // Draw Text
                    canvas.DrawText(genre.Name, x, y - nodeSize - 8, _textPaint);
                    canvas.DrawText(genre.Count.ToString(), x, y + 4, _countTextPaint);
                }

                // Draw constellation links between nearby nodes
                for (int i = 0; i < nodePositions.Count; i++)
                {
                    for (int j = i + 1; j < nodePositions.Count; j++)
                    {
                        float dist = SKPoint.Distance(nodePositions[i], nodePositions[j]);
                        if (dist < 100) // Connect if close
                        {
                            // Opacity based on distance (closer = more opaque)
                            byte alpha = (byte)Math.Clamp(80 - (dist * 0.8f), 0, 80);
                            _linkPaint.Color = SKColors.White.WithAlpha(alpha);
                            canvas.DrawLine(nodePositions[i], nodePositions[j], _linkPaint);
                        }
                    }
                }

                canvas.Restore();
            }
        }
    }
}
