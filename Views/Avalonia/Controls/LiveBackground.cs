using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Avalonia.Skia;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Platform;
using System;
using System.Threading;
using Avalonia.Threading;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public class LiveBackground : Control
    {
        public static readonly StyledProperty<Bitmap?> SourceProperty =
            AvaloniaProperty.Register<LiveBackground, Bitmap?>(nameof(Source));

        public Bitmap? Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public static readonly StyledProperty<double> EnergyProperty =
            AvaloniaProperty.Register<LiveBackground, double>(nameof(Energy), 0.5);

        public double Energy
        {
            get => GetValue(EnergyProperty);
            set => SetValue(EnergyProperty, value);
        }

        private SKImage? _blurredImage;
        private Bitmap? _lastSource;
        private float _animationValue;
        private readonly DispatcherTimer _timer;
        private readonly Random _random = new();

        public LiveBackground()
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
            // Phase 21: High-Fidelity Physics
            // Higher energy = faster drift and more intense "heartbeat"
            float speedMultiplier = (float)(1.0 + (Energy * 3.0));
            _animationValue += 0.002f * speedMultiplier;
            
            InvalidateVisual();
        }

        private void UpdateBlurredBitmap(Bitmap? source)
        {
            if (source == _lastSource) return;
            _lastSource = source;

            if (source == null)
            {
                _blurredImage?.Dispose();
                _blurredImage = null;
                return;
            }

            // Perform blur on background thread to avoid UI stutter
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var stream = new System.IO.MemoryStream())
                    {
                        Dispatcher.UIThread.Post(() => {
                            source.Save(stream);
                            ProcessBlur(stream.ToArray());
                        });
                    }
                }
                catch { }
            });
        }

        private void ProcessBlur(byte[] data)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using var original = SKBitmap.Decode(data);
                    if (original == null) return;

                    // Phase 21: High-Fidelity Downscale
                    // 300x300 provides smoother gradients than 200x200
                    int sw = 300;
                    int sh = 300;
                    var scaled = new SKBitmap(sw, sh);
                    original.ScalePixels(scaled, SKFilterQuality.Medium);

                    var blurred = new SKBitmap(sw, sh);
                    using (var canvas = new SKCanvas(blurred))
                    {
                        using var paint = new SKPaint();
                        // Variable blur based on default, will be combined with dynamic scaling in Render
                        using var blur = SKImageFilter.CreateBlur(40f, 40f);
                        paint.ImageFilter = blur;
                        canvas.DrawBitmap(scaled, 0, 0, paint);
                    }

                    var image = SKImage.FromBitmap(blurred);
                    var old = Interlocked.Exchange(ref _blurredImage, image);
                    old?.Dispose();
                    blurred.Dispose();
                    scaled.Dispose();

                    Dispatcher.UIThread.Post(InvalidateVisual);
                }
                catch { }
            });
        }

        public override void Render(DrawingContext context)
        {
            UpdateBlurredBitmap(Source);

            var rect = Bounds;
            if (_blurredImage == null)
            {
                context.FillRectangle(Brushes.Black, rect);
                return;
            }

            // Custom Skia Rendering for Parallax/Drift/Breathing
            context.Custom(new LiveBackgroundCustomDrawOperation(rect, _blurredImage, _animationValue, (float)Energy));
        }

        private class LiveBackgroundCustomDrawOperation : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly SKImage _image;
            private readonly float _animation;
            private readonly float _energy;

            public LiveBackgroundCustomDrawOperation(Rect bounds, SKImage image, float animation, float energy)
            {
                _bounds = bounds;
                _image = image;
                _animation = animation;
                _energy = energy;
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

                canvas.Save();
                
                // Phase 21: Dynamic Cinematic Motion
                // Core scale to fill
                float baseScaleX = (float)_bounds.Width / _image.Width * 1.3f;
                float baseScaleY = (float)_bounds.Height / _image.Height * 1.3f;
                float baseScale = Math.Max(baseScaleX, baseScaleY);

                // Heartbeat Breathing: Subtle scale oscillation based on Energy
                float breathing = (float)(Math.Sin(_animation * 2.0) * 0.05 * _energy);
                float finalScale = baseScale + breathing;

                // Cinematic Drift: Larger, floating movement
                float driftX = (float)(Math.Sin(_animation * 0.5) * 80 * (0.5 + _energy));
                float driftY = (float)(Math.Cos(_animation * 0.3) * 60 * (0.5 + _energy));

                canvas.Translate((float)_bounds.Width / 2 + driftX, (float)_bounds.Height / 2 + driftY);
                canvas.Scale(finalScale, finalScale);
                canvas.Translate(-_image.Width / 2f, -_image.Height / 2f);

                using var paint = new SKPaint { 
                    // Subtle opacity pulsing
                    Color = new SKColor(255, 255, 255, (byte)(230 + (Math.Sin(_animation) * 25 * _energy)))
                };
                
                canvas.DrawImage(_image, new SKRect(0, 0, _image.Width, _image.Height), paint);

                // Add dark vignette/overlay
                using var overlay = new SKPaint
                {
                    // Darker vignette for lower energy (more chill), brighter for higher energy
                    Color = new SKColor(0, 0, 0, (byte)(200 - (_energy * 60)))
                };
                canvas.DrawRect(0, 0, _image.Width, _image.Height, overlay);

                canvas.Restore();
            }
        }
    }
}
