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

        /// <summary>
        /// Wraps an <see cref="SKImage"/> so it can be safely shared across many consecutive
        /// draw operations (one per ~33ms frame) without any single one disposing it out from
        /// under another. The image is genuinely disposed only once every holder has released it.
        /// </summary>
        private sealed class RefCountedImage
        {
            public SKImage Image { get; }
            private int _refCount = 1; // starts owned by whoever constructs it

            public RefCountedImage(SKImage image) => Image = image;

            /// <summary>Call when handing this same instance to another consumer (e.g. a new draw operation).</summary>
            public RefCountedImage AddRef()
            {
                Interlocked.Increment(ref _refCount);
                return this;
            }

            /// <summary>Call when a consumer is done with it; disposes the underlying SKImage once every holder has released it.</summary>
            public void Release()
            {
                if (Interlocked.Decrement(ref _refCount) == 0)
                    Image.Dispose();
            }
        }

        private RefCountedImage? _blurredImage;
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
            // Releases this control's own slot's reference — any draw operation still holding
            // its own AddRef() (e.g. mid-render on the compositor thread) keeps the image alive
            // until it releases too, so this is safe even if a frame is still in flight.
            var img = Interlocked.Exchange(ref _blurredImage, null);
            img?.Release();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (!IsVisible || Bounds.Width < 2 || Bounds.Height < 2)
            {
                return;
            }

            if (Source == null && _blurredImage == null)
            {
                return;
            }

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
                // Release this control's own slot's reference — any draw operation still
                // rendering a frame holds its own AddRef() and keeps the image alive until
                // it releases too, so this can't race with an in-flight render.
                var old = Interlocked.Exchange(ref _blurredImage, null);
                old?.Release();
                return;
            }

            // Capture bitmap bytes on UI thread, then run blur processing in background.
            // Keeping the MemoryStream inside the posted callback avoids using a disposed stream.
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var stream = new System.IO.MemoryStream();
                    source.Save(stream);
                    ProcessBlur(stream.ToArray());
                }
                catch (ObjectDisposedException)
                {
                    // Source can be disposed during rapid visual-tree changes; skip this frame safely.
                }
                catch
                {
                }
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

                    // Swap in the new image. The old one's control-level reference is released
                    // here, but any draw operation still rendering a frame against it holds its
                    // own AddRef() and keeps it alive until that render completes — no race with
                    // an in-flight frame, unlike a raw unconditional Dispose() would be.
                    var newRef = new RefCountedImage(SKImage.FromBitmap(blurred));
                    var oldRef = Interlocked.Exchange(ref _blurredImage, newRef);
                    oldRef?.Release();
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

            // Capture a local snapshot so that ProcessBlur cannot swap _blurredImage
            // between the null-check and the construction of the draw operation.
            var imageSnapshot = _blurredImage;
            if (imageSnapshot == null)
            {
                context.FillRectangle(Brushes.Black, rect);
                return;
            }

            // Custom Skia Rendering for Parallax/Drift/Breathing. AddRef() here — this frame's
            // draw operation gets its own reference, independent of every other frame's draw
            // operation that may still be sharing the same underlying SKImage (this control's
            // ~33ms timer can produce several in-flight frames before any one of them actually
            // renders on the compositor thread). The draw op releases its own reference in
            // Dispose(); the image is only actually freed once every holder has released it.
            context.Custom(new LiveBackgroundCustomDrawOperation(rect, imageSnapshot.AddRef(), _animationValue, (float)Energy));
        }

        private class LiveBackgroundCustomDrawOperation : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly RefCountedImage _imageRef;
            private readonly SKImage _image;
            private readonly float _animation;
            private readonly float _energy;

            /// <summary>
            /// <paramref name="imageRef"/> must already be a reference this operation owns (i.e. the
            /// caller has called <see cref="RefCountedImage.AddRef"/> for it) — this operation will
            /// release exactly that one reference in <see cref="Dispose"/>, never disposing the
            /// underlying SKImage directly (other frames' draw operations may still be sharing it).
            /// </summary>
            public LiveBackgroundCustomDrawOperation(Rect bounds, RefCountedImage imageRef, float animation, float energy)
            {
                _bounds = bounds;
                _imageRef = imageRef;
                _image = imageRef.Image;
                _animation = animation;
                _energy = energy;
            }

            public void Dispose()
            {
                // Releases this operation's own reference — the underlying SKImage is only
                // actually disposed once every other frame's draw operation (and the control's
                // own current-image slot) has released its reference too.
                _imageRef.Release();
            }

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
