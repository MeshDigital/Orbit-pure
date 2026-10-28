using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Whole-track overview above Cue Forge's detail waveform: mono envelope, cue ticks, the visible
/// window of the detail view and the playhead. Click or drag to move the playhead there and
/// centre the detail view on it.
///
/// The envelope and cue ticks are cached in a bitmap that is only rebuilt when data, cues or size
/// change; playback redraws only blit it and draw the window rectangle and playhead with cached
/// brushes. Control.Render runs on the UI thread, so reading styled properties is safe.
/// </summary>
public class CueForgeOverviewControl : Control
{
    public static readonly StyledProperty<byte[]?> WaveformLowProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, byte[]?>(nameof(WaveformLow));
    public byte[]? WaveformLow { get => GetValue(WaveformLowProperty); set => SetValue(WaveformLowProperty, value); }

    public static readonly StyledProperty<byte[]?> WaveformMidProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, byte[]?>(nameof(WaveformMid));
    public byte[]? WaveformMid { get => GetValue(WaveformMidProperty); set => SetValue(WaveformMidProperty, value); }

    public static readonly StyledProperty<byte[]?> WaveformHighProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, byte[]?>(nameof(WaveformHigh));
    public byte[]? WaveformHigh { get => GetValue(WaveformHighProperty); set => SetValue(WaveformHighProperty, value); }

    public static readonly StyledProperty<ObservableCollection<OrbitCue>?> CuesProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, ObservableCollection<OrbitCue>?>(nameof(Cues));
    public ObservableCollection<OrbitCue>? Cues { get => GetValue(CuesProperty); set => SetValue(CuesProperty, value); }

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, int>(nameof(Revision));
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

    public static readonly StyledProperty<double> CurrentPlayPositionProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, double>(nameof(CurrentPlayPosition));
    public double CurrentPlayPosition { get => GetValue(CurrentPlayPositionProperty); set => SetValue(CurrentPlayPositionProperty, value); }

    public static readonly StyledProperty<double> TrackDurationProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, double>(nameof(TrackDuration), 300.0);
    public double TrackDuration { get => GetValue(TrackDurationProperty); set => SetValue(TrackDurationProperty, value); }

    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, double>(nameof(ZoomLevel), 1.0);
    public double ZoomLevel { get => GetValue(ZoomLevelProperty); set => SetValue(ZoomLevelProperty, value); }

    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, double>(nameof(ScrollOffset), 0.0,
            defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);
    public double ScrollOffset { get => GetValue(ScrollOffsetProperty); set => SetValue(ScrollOffsetProperty, value); }

    public static readonly StyledProperty<System.Windows.Input.ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, System.Windows.Input.ICommand?>(nameof(SeekCommand));
    public System.Windows.Input.ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    /// <summary>Click (no drag) on a cue tick: play from the cue. A drag from a tick scrubs as usual.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CueClickedCommandProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, System.Windows.Input.ICommand?>(nameof(CueClickedCommand));
    public System.Windows.Input.ICommand? CueClickedCommand { get => GetValue(CueClickedCommandProperty); set => SetValue(CueClickedCommandProperty, value); }

    public static readonly StyledProperty<IBrush?> IntroRegionFillProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, IBrush?>(nameof(IntroRegionFill));
    public IBrush? IntroRegionFill { get => GetValue(IntroRegionFillProperty); set => SetValue(IntroRegionFillProperty, value); }

    public static readonly StyledProperty<IBrush?> OutroRegionFillProperty =
        AvaloniaProperty.Register<CueForgeOverviewControl, IBrush?>(nameof(OutroRegionFill));
    public IBrush? OutroRegionFill { get => GetValue(OutroRegionFillProperty); set => SetValue(OutroRegionFillProperty, value); }

    private const double CueHitRadius = 5.0;
    private readonly MarkerGesture<OrbitCue> _cueGesture = new();

    private static readonly IBrush EnvelopeBrush = new SolidColorBrush(Color.FromArgb(150, 168, 173, 181)).ToImmutable();
    private static readonly IBrush WindowFill = new SolidColorBrush(Color.FromArgb(28, 78, 201, 176)).ToImmutable();
    private static readonly IPen WindowPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 78, 201, 176)).ToImmutable(), 1).ToImmutable();
    private static readonly IBrush PlayedBrush = new SolidColorBrush(Color.FromArgb(70, 15, 16, 18)).ToImmutable();
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Colors.White).ToImmutable(), 1.5).ToImmutable();

    private static readonly IBrush BadgeTextBrush = new SolidColorBrush(Color.FromRgb(15, 16, 18)).ToImmutable();
    private static readonly Typeface BadgeTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    private const double BadgeSize = 11;

    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly FormattedText?[] _padLetters = new FormattedText?[8];
    private RenderTargetBitmap? _bitmap;
    private Size _bitmapSize;
    private double _bitmapScale;
    private bool _dirty = true;
    private bool _dragging;
    private DateTime _lastSeek = DateTime.MinValue;

    public CueForgeOverviewControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;
        EnsureBitmap(size);
        // Source rect in PIXELS: Avalonia 11.2's DrawImage(img, dest) passes img.Size (DIPs) as the
        // source rect, which Skia reads as pixels, so at 150% scaling only the top-left 2/3 of a
        // DPI-scaled bitmap was drawn, stretched (cues/ruler 1.5x off, end of track missing).
        if (_bitmap != null) ctx.DrawImage(_bitmap, PixelRect(_bitmap), new Rect(size));
        if (TrackDuration <= 0) return;

        double zoom = Math.Max(1.0, ZoomLevel);
        if (zoom > 1.001)
        {
            double x = ScrollOffset * size.Width, w = Math.Max(2, size.Width / zoom);
            var r = new Rect(x, 0.5, w, size.Height - 1);
            ctx.FillRectangle(WindowFill, r);
            ctx.DrawRectangle(null, WindowPen, r);
        }
        double px = Math.Clamp(CurrentPlayPosition / TrackDuration, 0, 1) * size.Width;
        ctx.FillRectangle(PlayedBrush, new Rect(0, 0, px, size.Height));
        ctx.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, size.Height));
    }

    private void EnsureBitmap(Size size)
    {
        double scale = VisualRoot?.RenderScaling ?? 1.0;
        bool resized = _bitmap == null || Math.Abs(_bitmapSize.Width - size.Width) > 0.5 ||
                       Math.Abs(_bitmapSize.Height - size.Height) > 0.5 || Math.Abs(_bitmapScale - scale) > 0.01;
        if (!_dirty && !resized) return;
        if (resized)
        {
            _bitmap?.Dispose();
            _bitmap = new RenderTargetBitmap(
                new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale))),
                new Vector(96 * scale, 96 * scale));
        }

        using (var ctx = _bitmap!.CreateDrawingContext())
        {
            var low = WaveformLow; var mid = WaveformMid; var high = WaveformHigh;
            if (low is { Length: > 1 } && mid?.Length == low.Length && high?.Length == low.Length)
            {
                // One filled envelope geometry: a single draw call, no per-column allocations.
                int cols = Math.Max(2, (int)size.Width);
                double cy = size.Height / 2, maxHalf = size.Height * 0.46;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    var amps = new double[cols];
                    for (int c = 0; c < cols; c++)
                    {
                        int i0 = (int)((long)c * low.Length / cols), i1 = Math.Max(i0 + 1, (int)((long)(c + 1) * low.Length / cols));
                        int peak = 0;
                        for (int i = i0; i < i1 && i < low.Length; i++)
                            peak = Math.Max(peak, (low[i] + mid![i] + high![i]) / 3);
                        amps[c] = peak / 255.0 * maxHalf;
                    }
                    g.BeginFigure(new Point(0, cy - amps[0]), true);
                    for (int c = 1; c < cols; c++) g.LineTo(new Point(c, cy - amps[c]));
                    for (int c = cols - 1; c >= 0; c--) g.LineTo(new Point(c, cy + amps[c]));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(EnvelopeBrush, null, geo);
            }

            if (Cues != null && TrackDuration > 0)
            {
                // Section regions under the cue ticks.
                if (IntroRegionFill != null && Engine.Cueing.SectionMarkers.IntroRegion(Cues) is { } intro)
                {
                    double a = intro.Start / TrackDuration * size.Width, b = intro.End / TrackDuration * size.Width;
                    ctx.FillRectangle(IntroRegionFill, new Rect(a, 0, Math.Max(0, b - a), size.Height));
                }
                if (OutroRegionFill != null && Engine.Cueing.SectionMarkers.OutroRegion(Cues, TrackDuration) is { } outro)
                {
                    double a = outro.Start / TrackDuration * size.Width, b = outro.End / TrackDuration * size.Width;
                    ctx.FillRectangle(OutroRegionFill, new Rect(a, 0, Math.Max(0, b - a), size.Height));
                }

                foreach (var cue in Cues)
                {
                    double x = cue.Timestamp / TrackDuration * size.Width;
                    var brush = GetBrush(cue.Color);
                    if (cue.IsLoop)
                    {
                        double x2 = cue.LoopEndSeconds / TrackDuration * size.Width;
                        ctx.FillRectangle(brush, new Rect(x, size.Height - 3, Math.Max(2, x2 - x), 3));
                    }
                    else
                    {
                        ctx.FillRectangle(brush, new Rect(x - 0.5, 0, cue.SlotIndex >= 0 ? 2 : 1, size.Height));
                    }
                }

                // Pad letter badges on top, so cues outside the zoomed detail view (e.g. the
                // second drop group) are findable at a glance.
                foreach (var cue in Cues)
                {
                    if (cue.IsLoop || cue.SlotIndex is < 0 or > 7) continue;
                    double x = Math.Clamp(cue.Timestamp / TrackDuration * size.Width - 0.5, 0, Math.Max(0, size.Width - BadgeSize));
                    ctx.FillRectangle(GetBrush(cue.Color), new Rect(x, 0, BadgeSize, BadgeSize), 2);
                    var letter = PadLetter(cue.SlotIndex);
                    ctx.DrawText(letter, new Point(x + (BadgeSize - letter.Width) / 2, (BadgeSize - letter.Height) / 2));
                }
            }
        }
        _bitmapSize = size;
        _bitmapScale = scale;
        _dirty = false;
    }

    internal static Rect PixelRect(Bitmap bitmap) => new(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

    private FormattedText PadLetter(int slot) =>
        _padLetters[slot] ??= new FormattedText(((char)('A' + slot)).ToString(), System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, BadgeTypeface, 8.5, BadgeTextBrush);

    private IBrush GetBrush(string? hex)
    {
        hex ??= "";
        if (!_brushCache.TryGetValue(hex, out var b))
        {
            var c = !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var parsed) ? parsed : Colors.White;
            _brushCache[hex] = b = new SolidColorBrush(c).ToImmutable();
        }
        return b;
    }

    /// <summary>The cue tick (point cue or loop start) nearest to <paramref name="x"/> within the hit radius.</summary>
    private OrbitCue? HitCue(double x)
    {
        if (Cues == null || TrackDuration <= 0 || Bounds.Width <= 0) return null;
        OrbitCue? best = null;
        double bestD = CueHitRadius;
        foreach (var cue in Cues)
        {
            double d = Math.Abs(x - cue.Timestamp / TrackDuration * Bounds.Width);
            if (d <= bestD) { bestD = d; best = cue; }
        }
        return best;
    }

    private bool _hoverTipShown;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        double x = e.GetPosition(this).X;
        _dragging = true;
        e.Pointer.Capture(this);
        // On a cue tick: wait — a click plays from the cue, a drag scrubs. Elsewhere: jump at once.
        if (CueClickedCommand != null && HitCue(x) is { } cue) { _cueGesture.Press(cue, x); return; }
        MoveTo(x, force: true);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetPosition(this).X;
        if (!_dragging)
        {
            bool over = CueClickedCommand != null && HitCue(x) != null;
            if (over != _hoverTipShown)
            {
                _hoverTipShown = over;
                ToolTip.SetTip(this, over ? "Click to play from this cue · drag to jump" : null);
            }
            return;
        }
        if (_cueGesture.Target != null && !_cueGesture.IsDragging && !_cueGesture.Move(x)) return;
        MoveTo(x, force: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        double x = e.GetPosition(this).X;
        var (cue, result) = _cueGesture.Release();
        e.Pointer.Capture(null);
        if (result == MarkerGestureResult.Click && cue != null)
        {
            ScrollOffset = CueForgeGrid.CenterOn(cue.Timestamp / Math.Max(1e-9, TrackDuration), ZoomLevel);
            if (CueClickedCommand?.CanExecute(cue) == true) CueClickedCommand.Execute(cue);
            return;
        }
        MoveTo(x, force: true);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _cueGesture.Cancel();
        _dragging = false;
    }

    private void MoveTo(double x, bool force)
    {
        if (TrackDuration <= 0 || Bounds.Width <= 0) return;
        double frac = Math.Clamp(x / Bounds.Width, 0, 1);
        ScrollOffset = CueForgeGrid.CenterOn(frac, ZoomLevel);
        var now = DateTime.UtcNow;
        if (!force && (now - _lastSeek).TotalMilliseconds < 33) return;
        _lastSeek = now;
        double seconds = frac * TrackDuration;
        if (SeekCommand?.CanExecute(seconds) == true) SeekCommand.Execute(seconds);
    }

    private void OnCuesChanged(object? sender, NotifyCollectionChangedEventArgs e) { _dirty = true; InvalidateVisual(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CuesProperty)
        {
            if (change.OldValue is ObservableCollection<OrbitCue> old) old.CollectionChanged -= OnCuesChanged;
            if (change.NewValue is ObservableCollection<OrbitCue> fresh) fresh.CollectionChanged += OnCuesChanged;
        }
        if (change.Property == CuesProperty || change.Property == RevisionProperty || change.Property == TrackDurationProperty ||
            change.Property == WaveformLowProperty || change.Property == WaveformMidProperty || change.Property == WaveformHighProperty ||
            change.Property == IntroRegionFillProperty || change.Property == OutroRegionFillProperty)
        {
            _dirty = true;
            InvalidateVisual();
        }
        else if (change.Property == CurrentPlayPositionProperty || change.Property == ZoomLevelProperty || change.Property == ScrollOffsetProperty)
        {
            InvalidateVisual();
        }
    }
}
