using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SLSKDONET.Models;
using SLSKDONET.Services.Timeline;
using SkiaSharp;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Full-length waveform underlay for library track rows.
///
/// Renders the complete track waveform as an RGB tri-band SkiaSharp bitmap
/// on a background thread, then displays it behind the row metadata via a
/// cached WriteableBitmap (pixel-copy, no PNG encode).
///
/// Performance contract:
///  - Render is off the UI thread; only InvalidateVisual() posts back.
///  - Re-render fires only when WaveformData changes OR width changes > 8px.
///  - A CancellationTokenSource per render request prevents stale bitmaps.
/// </summary>
public sealed class LibraryWaveformView : global::Avalonia.Controls.Control
{
    public static readonly StyledProperty<WaveformAnalysisData?> WaveformDataProperty =
        AvaloniaProperty.Register<LibraryWaveformView, WaveformAnalysisData?>(nameof(WaveformData));

    public WaveformAnalysisData? WaveformData
    {
        get => GetValue(WaveformDataProperty);
        set => SetValue(WaveformDataProperty, value);
    }

    private WriteableBitmap? _bitmap;
    private CancellationTokenSource? _cts;
    private int _lastW;
    private int _lastH;

    static LibraryWaveformView()
    {
        WaveformDataProperty.Changed.AddClassHandler<LibraryWaveformView>((v, _) => v.ScheduleRender(0, 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        int w = (int)result.Width;
        int h = (int)result.Height;

        // Only re-render when size changes meaningfully (avoids thrash during layout passes)
        if (w > 0 && h > 0 && (Math.Abs(w - _lastW) > 8 || Math.Abs(h - _lastH) > 4))
            ScheduleRender(w, h);

        return result;
    }

    private void ScheduleRender(int w, int h)
    {
        var data = WaveformData;
        if (data == null || data.IsEmpty) return;

        if (w <= 0) w = (int)Bounds.Width;
        if (h <= 0) h = (int)Bounds.Height;
        if (w <= 0 || h <= 0) return;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _lastW = w;
        _lastH = h;

        // Snapshot values for background thread (no closure over mutable fields)
        var renderData = data;
        var renderW = w;
        var renderH = h;

        Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;

            // Prefer RGB tri-band when all three bands are available;
            // fall back to single-color RMS when only PeakData or RmsData exists.
            SKBitmap skBmp;
            bool hasTriBand = (renderData.LowData?.Length > 0)
                           || (renderData.MidData?.Length > 0)
                           || (renderData.HighData?.Length > 0);

            if (hasTriBand)
            {
                skBmp = WaveformRenderer.RenderRgb(
                    renderData, renderW, renderH,
                    bgColor: new SKColor(0, 0, 0, 0));  // transparent background
            }
            else
            {
                skBmp = WaveformRenderer.RenderFromWaveformData(
                    renderData, renderW, renderH,
                    waveColor: new SKColor(120, 200, 255, 210),
                    bgColor:   new SKColor(0, 0, 0, 0));
            }

            if (token.IsCancellationRequested) { skBmp.Dispose(); return; }

            // Fast pixel copy: SKBitmap BGRA8888 → WriteableBitmap BGRA8888
            // Uses Marshal.Copy (no unsafe block required)
            WriteableBitmap? wb = null;
            try
            {
                wb = new WriteableBitmap(
                    new PixelSize(skBmp.Width, skBmp.Height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                using var fb = wb.Lock();
                var bytes = skBmp.Bytes;
                Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
            }
            finally
            {
                skBmp.Dispose();
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested)
                {
                    wb?.Dispose();
                    return;
                }

                _bitmap?.Dispose();
                _bitmap = wb;
                InvalidateVisual();
            }, DispatcherPriority.Background);

        }, token);
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is null) return;

        var src = new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        var dst = new Rect(Bounds.Size);
        context.DrawImage(_bitmap, src, dst);
    }
}
