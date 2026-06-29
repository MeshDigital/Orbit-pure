using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using SLSKDONET;                  // App
using SLSKDONET.Services.Audio;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Real-time FFT spectrum visualizer for the library hover-preview.
///
/// Architecture:
///  - Inherits Avalonia Control; renders via ICustomDrawOperation so SkiaSharp
///    draws directly into Avalonia's retained scene graph (same pattern as
///    WaveformControl, TimelineClipControl, etc.).
///  - Hooks ILibraryPreviewPlayer.SpectrumChanged from the audio thread.
///  - Array swap is lock-free (Volatile.Write / Volatile.Read).
///  - 30 ms post throttle prevents flooding the UI thread (caps at ~33 fps).
///  - Colour gradient interpolates PrimaryColor → SecondaryColor across bins.
///  - Log-scale dB normalisation makes low-energy bins visible.
/// </summary>
public sealed class SpectrumBarVisualizer : Control, IDisposable
{
    // ── Styled properties ────────────────────────────────────────────────────

    public static readonly StyledProperty<Color> PrimaryColorProperty =
        AvaloniaProperty.Register<SpectrumBarVisualizer, Color>(
            nameof(PrimaryColor), Color.Parse("#00C8FF")); // neon cyan

    public static readonly StyledProperty<Color> SecondaryColorProperty =
        AvaloniaProperty.Register<SpectrumBarVisualizer, Color>(
            nameof(SecondaryColor), Color.Parse("#FF2864")); // hot pink

    public Color PrimaryColor
    {
        get => GetValue(PrimaryColorProperty);
        set => SetValue(PrimaryColorProperty, value);
    }

    public Color SecondaryColor
    {
        get => GetValue(SecondaryColorProperty);
        set => SetValue(SecondaryColorProperty, value);
    }

    // ── State ────────────────────────────────────────────────────────────────

    private ILibraryPreviewPlayer? _previewPlayer;
    private float[]? _spectrum;         // replaced atomically; never mutated in place
    private long _lastPostTicks;        // used for 30ms throttle

    // Only use the lower 128 FFT bins — human-perceptual frequency range
    private const int BinCount = 128;

    // ── Lifetime ─────────────────────────────────────────────────────────────

    public SpectrumBarVisualizer()
    {
        AttachedToVisualTree   += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _previewPlayer = (Application.Current as App)?.Services?.GetService<ILibraryPreviewPlayer>();
        if (_previewPlayer != null)
            _previewPlayer.SpectrumChanged += OnSpectrumChanged;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_previewPlayer != null)
            _previewPlayer.SpectrumChanged -= OnSpectrumChanged;
        Volatile.Write(ref _spectrum, null);
    }

    public void Dispose()
    {
        if (_previewPlayer != null)
            _previewPlayer.SpectrumChanged -= OnSpectrumChanged;
        _previewPlayer = null;
    }

    // ── Audio-thread callback ─────────────────────────────────────────────────

    private void OnSpectrumChanged(object? sender, float[] spectrum)
    {
        // Atomic swap — the draw op reads this snapshot; never partially-written
        Volatile.Write(ref _spectrum, spectrum);

        // Throttle: skip if we posted within the last 30ms (~33 fps max)
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastPostTicks) < 30) return;
        Volatile.Write(ref _lastPostTicks, now);

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
    }

    // ── Avalonia render ───────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var spec = Volatile.Read(ref _spectrum);
        if (spec == null || spec.Length == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            // Nothing playing — clear
            context.Custom(new SpectrumDrawOp(null, Bounds, default, default));
            return;
        }

        context.Custom(new SpectrumDrawOp(
            spec, Bounds,
            new SKColor(PrimaryColor.R,   PrimaryColor.G,   PrimaryColor.B),
            new SKColor(SecondaryColor.R, SecondaryColor.G, SecondaryColor.B)));
    }

    // ── SkiaSharp draw operation ──────────────────────────────────────────────

    private sealed class SpectrumDrawOp : ICustomDrawOperation
    {
        private readonly float[]? _spectrum;
        private readonly Rect _bounds;
        private readonly SKColor _primary;
        private readonly SKColor _secondary;

        public Rect Bounds => _bounds;

        public SpectrumDrawOp(float[]? spectrum, Rect bounds, SKColor primary, SKColor secondary)
        {
            _spectrum  = spectrum;
            _bounds    = bounds;
            _primary   = primary;
            _secondary = secondary;
        }

        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature(typeof(ISkiaSharpApiLease)) as ISkiaSharpApiLease;
            if (lease is null) return;
            var canvas = lease.SkCanvas;

            float w     = (float)_bounds.Width;
            float h     = (float)_bounds.Height;
            float halfH = h * 0.5f;

            canvas.Clear(SKColors.Transparent);

            if (_spectrum == null) return;

            int binCount = Math.Min(_spectrum.Length, BinCount);
            float barW   = w / binCount;

            using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };

            for (int i = 0; i < binCount; i++)
            {
                // Log-dB normalisation: maps [0.0001–1] → [0–1] over 60 dB range
                float db   = 20f * MathF.Log10(_spectrum[i] + 0.0001f);
                float norm = Math.Clamp((db + 60f) / 60f, 0f, 1f);

                float barHalf = norm * halfH * 0.95f;
                if (barHalf < 0.5f) continue;

                // Linear colour gradient: primary (low freq) → secondary (high freq)
                float t = (float)i / binCount;
                paint.Color = new SKColor(
                    Lerp(_primary.Red,   _secondary.Red,   t),
                    Lerp(_primary.Green, _secondary.Green, t),
                    Lerp(_primary.Blue,  _secondary.Blue,  t));

                float x = i * barW;

                // Symmetrical mirror bars from centre axis
                canvas.DrawRect(x, halfH - barHalf, barW - 0.5f, barHalf * 2f, paint);
            }
        }

        private static byte Lerp(byte a, byte b, float t) =>
            (byte)(a + (b - a) * t);
    }
}
