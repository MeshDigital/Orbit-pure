using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Cue Forge's zoomable detail waveform with editable cue and loop markers.
///
/// Interaction: click empty waveform = move the playhead (hold and drag to scrub); drag a cue
/// marker or loop edge to move it (snapped to the grid, hold Shift for free placement) with a
/// live time / bar readout; mouse wheel zooms around the cursor, Shift/Ctrl + wheel (or a
/// horizontal swipe) scrolls.
///
/// Rendering: everything except the playhead and the drag readout is drawn once into a cached
/// bitmap (rebuilt only when data, cues, zoom/scroll or size change). The waveform itself is
/// written straight into a pixel buffer, and every pen, brush and label is cached, so playback
/// redraws (~30 fps) only blit the cache and draw one line. Control.Render runs on the UI thread,
/// so reading styled properties here is safe (unlike ICustomDrawOperation.Render).
/// </summary>
public class CueForgeWaveformControl : Control
{
    // ── Styled Properties ──────────────────────────────────────────────────

    public static readonly StyledProperty<ObservableCollection<OrbitCue>?> CuesProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, ObservableCollection<OrbitCue>?>(nameof(Cues));
    public ObservableCollection<OrbitCue>? Cues { get => GetValue(CuesProperty); set => SetValue(CuesProperty, value); }

    public static readonly StyledProperty<ObservableCollection<PhraseSegment>?> PhraseSegmentsProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, ObservableCollection<PhraseSegment>?>(nameof(PhraseSegments));
    public ObservableCollection<PhraseSegment>? PhraseSegments { get => GetValue(PhraseSegmentsProperty); set => SetValue(PhraseSegmentsProperty, value); }

    public static readonly StyledProperty<double> CurrentPlayPositionProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(CurrentPlayPosition));
    public double CurrentPlayPosition { get => GetValue(CurrentPlayPositionProperty); set => SetValue(CurrentPlayPositionProperty, value); }

    public static readonly StyledProperty<double> TrackDurationProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(TrackDuration), 300.0);
    public double TrackDuration { get => GetValue(TrackDurationProperty); set => SetValue(TrackDurationProperty, value); }

    /// <summary>Rounded BPM; used only when <see cref="GridBpm"/> isn't set.</summary>
    public static readonly StyledProperty<int> BpmProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, int>(nameof(Bpm), 120);
    public int Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }

    /// <summary>Precise BPM for the beat grid and snapping.</summary>
    public static readonly StyledProperty<double> GridBpmProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(GridBpm));
    public double GridBpm { get => GetValue(GridBpmProperty); set => SetValue(GridBpmProperty, value); }

    /// <summary>Downbeat offset (seconds) the grid is anchored at.</summary>
    public static readonly StyledProperty<double> GridOffsetProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(GridOffset));
    public double GridOffset { get => GetValue(GridOffsetProperty); set => SetValue(GridOffsetProperty, value); }

    public static readonly StyledProperty<bool> SnapToGridProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, bool>(nameof(SnapToGrid), true);
    public bool SnapToGrid { get => GetValue(SnapToGridProperty); set => SetValue(SnapToGridProperty, value); }

    /// <summary>Snap resolution in beats (1 = beat, 4 = bar…).</summary>
    public static readonly StyledProperty<int> SnapBeatsProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, int>(nameof(SnapBeats), 1);
    public int SnapBeats { get => GetValue(SnapBeatsProperty); set => SetValue(SnapBeatsProperty, value); }

    public static readonly StyledProperty<byte[]?> WaveformLowProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, byte[]?>(nameof(WaveformLow));
    public byte[]? WaveformLow { get => GetValue(WaveformLowProperty); set => SetValue(WaveformLowProperty, value); }

    public static readonly StyledProperty<byte[]?> WaveformMidProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, byte[]?>(nameof(WaveformMid));
    public byte[]? WaveformMid { get => GetValue(WaveformMidProperty); set => SetValue(WaveformMidProperty, value); }

    public static readonly StyledProperty<byte[]?> WaveformHighProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, byte[]?>(nameof(WaveformHigh));
    public byte[]? WaveformHigh { get => GetValue(WaveformHighProperty); set => SetValue(WaveformHighProperty, value); }

    public static readonly StyledProperty<float[]?> EnergyCurveProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, float[]?>(nameof(EnergyCurve));
    public float[]? EnergyCurve { get => GetValue(EnergyCurveProperty); set => SetValue(EnergyCurveProperty, value); }

    public static readonly StyledProperty<float[]?> VocalDensityCurveProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, float[]?>(nameof(VocalDensityCurve));
    public float[]? VocalDensityCurve { get => GetValue(VocalDensityCurveProperty); set => SetValue(VocalDensityCurveProperty, value); }

    public static readonly StyledProperty<float[]?> OnsetDensityCurveProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, float[]?>(nameof(OnsetDensityCurve));
    public float[]? OnsetDensityCurve { get => GetValue(OnsetDensityCurveProperty); set => SetValue(OnsetDensityCurveProperty, value); }

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, IBrush?>(nameof(Background));
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>Bump to redraw after an in-place cue edit (rename, recolour, move).</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, int>(nameof(Revision));
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

    public static readonly StyledProperty<double> MaxZoomProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(MaxZoom), 32.0);
    public double MaxZoom { get => GetValue(MaxZoomProperty); set => SetValue(MaxZoomProperty, value); }

    /// <summary>Executed with the clicked / scrubbed time in seconds (double).</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, System.Windows.Input.ICommand?>(nameof(SeekCommand));
    public System.Windows.Input.ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    /// <summary>Fired when a cue or loop is pressed, so it opens in the inspector.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> SelectCueCommandProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, System.Windows.Input.ICommand?>(nameof(SelectCueCommand));
    public System.Windows.Input.ICommand? SelectCueCommand { get => GetValue(SelectCueCommandProperty); set => SetValue(SelectCueCommandProperty, value); }

    /// <summary>Fired on the first real drag movement, before the cue moves (undo snapshot).</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CueDragStartedCommandProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, System.Windows.Input.ICommand?>(nameof(CueDragStartedCommand));
    public System.Windows.Input.ICommand? CueDragStartedCommand { get => GetValue(CueDragStartedCommandProperty); set => SetValue(CueDragStartedCommandProperty, value); }

    /// <summary>Fired on release once a cue or loop edge actually moved.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CueUpdatedCommandProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, System.Windows.Input.ICommand?>(nameof(CueUpdatedCommand));
    public System.Windows.Input.ICommand? CueUpdatedCommand { get => GetValue(CueUpdatedCommandProperty); set => SetValue(CueUpdatedCommandProperty, value); }

    public static readonly StyledProperty<OrbitCue?> SelectedCueProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, OrbitCue?>(nameof(SelectedCue));
    public OrbitCue? SelectedCue { get => GetValue(SelectedCueProperty); set => SetValue(SelectedCueProperty, value); }

    /// <summary>Magnification (1 = whole track).</summary>
    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(ZoomLevel), 1.0,
            defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);
    public double ZoomLevel { get => GetValue(ZoomLevelProperty); set => SetValue(ZoomLevelProperty, value); }

    /// <summary>Fraction of the track at the left edge (0..1 - 1/zoom).</summary>
    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, double>(nameof(ScrollOffset), 0.0,
            defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);
    public double ScrollOffset { get => GetValue(ScrollOffsetProperty); set => SetValue(ScrollOffsetProperty, value); }

    /// <summary>Fired on a click (press + release without dragging) on a cue flag or loop: Cue Forge
    /// plays from the cue (prelisten). A drag moves the marker instead and never fires this.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CueClickedCommandProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, System.Windows.Input.ICommand?>(nameof(CueClickedCommand));
    public System.Windows.Input.ICommand? CueClickedCommand { get => GetValue(CueClickedCommandProperty); set => SetValue(CueClickedCommandProperty, value); }

    // Section regions (Intro end / Outro markers) — token brushes from the view.
    public static readonly StyledProperty<IBrush?> IntroRegionFillProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, IBrush?>(nameof(IntroRegionFill));
    public IBrush? IntroRegionFill { get => GetValue(IntroRegionFillProperty); set => SetValue(IntroRegionFillProperty, value); }
    public static readonly StyledProperty<IBrush?> IntroRegionEdgeProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, IBrush?>(nameof(IntroRegionEdge));
    public IBrush? IntroRegionEdge { get => GetValue(IntroRegionEdgeProperty); set => SetValue(IntroRegionEdgeProperty, value); }
    public static readonly StyledProperty<IBrush?> OutroRegionFillProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, IBrush?>(nameof(OutroRegionFill));
    public IBrush? OutroRegionFill { get => GetValue(OutroRegionFillProperty); set => SetValue(OutroRegionFillProperty, value); }
    public static readonly StyledProperty<IBrush?> OutroRegionEdgeProperty =
        AvaloniaProperty.Register<CueForgeWaveformControl, IBrush?>(nameof(OutroRegionEdge));
    public IBrush? OutroRegionEdge { get => GetValue(OutroRegionEdgeProperty); set => SetValue(OutroRegionEdgeProperty, value); }

    /// <summary>Shown while hovering a cue flag.</summary>
    public const string CueFlagTip = "Click to play from here · drag to move";

    /// <summary>True while the user holds the mouse down scrubbing the playhead.</summary>
    public event EventHandler<bool>? ScrubbingChanged;

    // ── Layout ─────────────────────────────────────────────────────────────

    private const double RulerHeight = 16.0;
    private const double PhraseMapHeight = 10.0;
    private const double HitRadius = 6.0;
    private const double FlagWidth = 16.0;
    private const double FlagHeight = 14.0;

    // ── Cached drawing resources (allocated once, never in Render) ─────────

    private static readonly IBrush RulerBrush = new SolidColorBrush(Color.FromArgb(255, 18, 19, 22)).ToImmutable();
    private static readonly IBrush RulerTextBrush = new SolidColorBrush(Color.FromArgb(200, 168, 173, 181)).ToImmutable();
    private static readonly IBrush LabelBgBrush = new SolidColorBrush(Color.FromArgb(200, 15, 16, 18)).ToImmutable();
    private static readonly IBrush LabelTextBrush = new SolidColorBrush(Color.FromArgb(255, 232, 234, 237)).ToImmutable();
    private static readonly IBrush FlagTextBrush = new SolidColorBrush(Color.FromArgb(255, 15, 16, 18)).ToImmutable();
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)).ToImmutable();
    private static readonly IBrush DoubleDropBrush = new SolidColorBrush(Color.FromArgb(22, 170, 114, 255)).ToImmutable();
    private static readonly IPen BeatPen = new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)).ToImmutable(), 1).ToImmutable();
    private static readonly IPen BarPen = new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)).ToImmutable(), 1).ToImmutable();
    private static readonly IPen PhrasePen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)).ToImmutable(), 1).ToImmutable();
    private static readonly IPen DividerPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)).ToImmutable(), 1).ToImmutable();
    private static readonly IPen EnergyPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 78, 201, 176)).ToImmutable(), 1.5).ToImmutable();
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)).ToImmutable();
    private static readonly IPen PlayheadPen = new Pen(PlayheadBrush, 1.5).ToImmutable();
    private static readonly IBrush ReadoutBgBrush = new SolidColorBrush(Color.FromArgb(235, 34, 35, 39)).ToImmutable();
    private static readonly Typeface UiTypeface = new("Inter");
    private static readonly Typeface MonoTypeface = new(new FontFamily("Cascadia Mono, Consolas, monospace"));
    private static readonly Cursor HandleCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor ScrubCursor = new(StandardCursorType.Ibeam);
    private static readonly Geometry PlayheadHead = CreateTriangle(new Point(-5, 0), new Point(5, 0), new Point(0, 7));
    private static readonly Geometry EdgeArrowLeft = CreateTriangle(new Point(0, 0), new Point(7, -5), new Point(7, 5));
    private static readonly Geometry EdgeArrowRight = CreateTriangle(new Point(0, 0), new Point(-7, -5), new Point(-7, 5));

    private static readonly Dictionary<string, (IBrush Fill, IBrush Band, IPen Edge)> PhraseStyles = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IBrush> _brushCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IBrush> _loopFillCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPen> _penCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FormattedText> _textCache = new();

    // ── Static-layer cache ─────────────────────────────────────────────────

    private RenderTargetBitmap? _staticBitmap;
    private WriteableBitmap? _waveBitmap;
    private int[]? _pixels;
    private Size _staticBitmapSize;
    private double _staticScale;
    private bool _staticDirty = true;

    // ── Drag state ─────────────────────────────────────────────────────────

    private enum DragKind { None, Scrub, Cue, LoopStart, LoopEnd }
    private DragKind _drag;
    private OrbitCue? _dragCue;
    private bool _dragMoved;
    private Point _pressPoint;
    private FormattedText? _readout;
    private double _readoutX;
    private DateTime _lastInvalidate = DateTime.MinValue;
    private DateTime _lastScrubSeek = DateTime.MinValue;
    private const double ThrottleMs = 33.0;

    public CueForgeWaveformControl()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    // ── Coordinate mapping ─────────────────────────────────────────────────

    private double EffectiveBpm => GridBpm > 0 ? GridBpm : Bpm;
    private double Zoom => Math.Max(1.0, ZoomLevel);

    private double TimeToX(double seconds, double width) =>
        TrackDuration <= 0 ? 0 : (seconds / TrackDuration - ScrollOffset) * Zoom * width;

    private double XToTime(double x, double width) =>
        width <= 0 ? 0 : (x / width / Zoom + ScrollOffset) * TrackDuration;

    /// <summary>Track time (seconds) under a point in this control — used by the page's context menu.</summary>
    public double TimeAt(Point p) => Math.Clamp(XToTime(p.X, Bounds.Width), 0, Math.Max(0, TrackDuration));

    private double ApplySnap(double t, KeyModifiers mods)
    {
        t = Math.Clamp(t, 0, TrackDuration);
        if (!SnapToGrid || (mods & KeyModifiers.Shift) != 0) return t;
        return Math.Clamp(CueForgeGrid.Snap(t, EffectiveBpm, GridOffset, Math.Max(1, SnapBeats)), 0, TrackDuration);
    }

    // ── Rendering ──────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;

        EnsureStaticBitmap(size);
        if (_staticBitmap is not null)
            ctx.DrawImage(_staticBitmap, new Rect(size));

        DrawPlayhead(ctx, size);

        if (_readout != null && _drag is DragKind.Cue or DragKind.LoopStart or DragKind.LoopEnd)
        {
            double w = _readout.Width + 12, h = _readout.Height + 6;
            double x = Math.Clamp(_readoutX + 8, 0, Math.Max(0, size.Width - w));
            double y = RulerHeight + 4;
            ctx.FillRectangle(ReadoutBgBrush, new Rect(x, y, w, h), 4);
            ctx.DrawText(_readout, new Point(x + 6, y + 3));
        }
    }

    private void DrawPlayhead(DrawingContext ctx, Size size)
    {
        if (TrackDuration <= 0) return;
        double bottom = size.Height - PhraseMapHeight;
        double x = TimeToX(CurrentPlayPosition, size.Width);
        if (x < 0)
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(2, RulerHeight / 2)))
                ctx.DrawGeometry(PlayheadBrush, null, EdgeArrowLeft);
            return;
        }
        if (x > size.Width)
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(size.Width - 2, RulerHeight / 2)))
                ctx.DrawGeometry(PlayheadBrush, null, EdgeArrowRight);
            return;
        }
        ctx.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, bottom));
        using (ctx.PushTransform(Matrix.CreateTranslation(x, 0)))
            ctx.DrawGeometry(PlayheadBrush, null, PlayheadHead);
    }

    private void EnsureStaticBitmap(Size size)
    {
        double scale = VisualRoot?.RenderScaling ?? 1.0;
        bool sizeChanged = _staticBitmap is null ||
            Math.Abs(_staticBitmapSize.Width - size.Width) > 0.5 ||
            Math.Abs(_staticBitmapSize.Height - size.Height) > 0.5 ||
            Math.Abs(_staticScale - scale) > 0.01;
        if (!_staticDirty && !sizeChanged) return;

        var pixelSize = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                                      Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        if (sizeChanged)
        {
            _staticBitmap?.Dispose();
            _staticBitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        }

        int waveTopPx = (int)Math.Round(RulerHeight * scale);
        int waveBottomPx = (int)Math.Round((size.Height - PhraseMapHeight) * scale);
        RenderWavePixels(pixelSize.Width, Math.Max(1, waveBottomPx - waveTopPx), scale);

        using (var ctx = _staticBitmap!.CreateDrawingContext())
        {
            var wave = new Rect(0, RulerHeight, size.Width, Math.Max(1, size.Height - RulerHeight - PhraseMapHeight));
            ctx.FillRectangle(RulerBrush, new Rect(0, 0, size.Width, RulerHeight));
            if (_waveBitmap != null) ctx.DrawImage(_waveBitmap, wave);
            DrawSectionBands(ctx, wave);
            DrawSectionRegions(ctx, wave);
            DrawPhraseMap(ctx, size, wave);
            DrawEnergy(ctx, wave);
            DrawGridAndRuler(ctx, size, wave);
            DrawDoubleDropZone(ctx, wave);
            DrawLoops(ctx, wave);
            DrawCueMarkers(ctx, wave);
            ctx.DrawLine(DividerPen, new Point(0, RulerHeight - 0.5), new Point(size.Width, RulerHeight - 0.5));
        }

        _staticBitmapSize = size;
        _staticScale = scale;
        _staticDirty = false;
    }

    /// <summary>Writes background + vocal/onset tint + RGB waveform columns into a pixel buffer.</summary>
    private void RenderWavePixels(int w, int h, double scale)
    {
        if (_waveBitmap == null || _waveBitmap.PixelSize.Width != w || _waveBitmap.PixelSize.Height != h)
        {
            _waveBitmap?.Dispose();
            _waveBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96 * scale, 96 * scale),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            _pixels = new int[w * h];
        }
        var px = _pixels!;

        var bg = (Background as ISolidColorBrush)?.Color ?? Color.FromRgb(15, 16, 18);
        var low = WaveformLow; var mid = WaveformMid; var high = WaveformHigh;
        var vocal = VocalDensityCurve; var onset = OnsetDensityCurve;
        double dur = TrackDuration;
        bool hasWave = low is { Length: > 1 } && mid is { Length: > 0 } && high is { Length: > 0 } &&
                       mid.Length == low.Length && high.Length == low.Length && dur > 0;
        int n = hasWave ? low!.Length : 0;
        double centerY = h * 0.5;
        double maxHalf = h * 0.47;

        for (int x = 0; x < w; x++)
        {
            double t0 = XToTime(x / scale, w / scale);
            double t1 = XToTime((x + 1) / scale, w / scale);
            double frac0 = dur > 0 ? t0 / dur : 0;

            // Column background, tinted by vocal / onset density at this time.
            float br = bg.R, bgc = bg.G, bb = bg.B;
            if (frac0 >= 0 && frac0 <= 1)
            {
                if (vocal is { Length: > 1 })
                {
                    float v = vocal[(int)Math.Clamp(frac0 * vocal.Length, 0, vocal.Length - 1)];
                    if (v > 0.3f) { float a = v * 0.18f; br += (229 - br) * a; bgc += (96 - bgc) * a; bb += (90 - bb) * a; }
                }
                if (onset is { Length: > 1 })
                {
                    float d = onset[(int)Math.Clamp(frac0 * onset.Length, 0, onset.Length - 1)];
                    if (d > 0.05f) { float a = d * 0.08f; br += (90 - br) * a; bgc += (167 - bgc) * a; bb += (232 - bb) * a; }
                }
            }
            int bgPixel = Pack(255, (int)br, (int)bgc, (int)bb);

            int top = 0, bottom = -1, wavePixel = 0;
            if (hasWave && frac0 >= 0 && frac0 <= 1)
            {
                double i0f = t0 / dur * n, i1f = t1 / dur * n;
                int i0 = (int)Math.Clamp(Math.Floor(i0f), 0, n - 1);
                int i1 = (int)Math.Clamp(Math.Ceiling(i1f), i0 + 1, n);
                int r, g, b;
                if (i1 - i0 <= 1)
                {
                    // Zoomed past the sample resolution: interpolate instead of stepping.
                    double exact = Math.Clamp(i0f, 0, n - 1);
                    int a0 = (int)exact, a1 = Math.Min(a0 + 1, n - 1);
                    double f = exact - a0;
                    r = (int)(low![a0] + (low[a1] - low[a0]) * f);
                    g = (int)(mid![a0] + (mid[a1] - mid[a0]) * f);
                    b = (int)(high![a0] + (high[a1] - high[a0]) * f);
                }
                else
                {
                    int sr = 0, sg = 0, sb = 0;
                    for (int i = i0; i < i1; i++) { sr += low![i]; sg += mid![i]; sb += high![i]; }
                    int c = i1 - i0;
                    r = sr / c; g = sg / c; b = sb / c;
                }
                double amp = (r + g + b) / 3.0 / 255.0;
                double half = Math.Max(0.5, amp * maxHalf);
                top = (int)Math.Floor(centerY - half);
                bottom = (int)Math.Ceiling(centerY + half);
                // Brighten so quiet passages stay readable on the dark background.
                double boost = 255.0 / Math.Max(64, Math.Max(r, Math.Max(g, b)));
                boost = Math.Min(boost, 1.6);
                wavePixel = Pack(255, (int)Math.Min(255, r * boost), (int)Math.Min(255, g * boost), (int)Math.Min(255, b * boost));
            }

            for (int y = 0; y < h; y++)
                px[y * w + x] = y >= top && y <= bottom ? wavePixel : bgPixel;
        }

        using var fb = _waveBitmap.Lock();
        int rowInts = fb.RowBytes / 4;
        if (rowInts == w)
            Marshal.Copy(px, 0, fb.Address, w * h);
        else
            for (int y = 0; y < h; y++)
                Marshal.Copy(px, y * w, fb.Address + y * fb.RowBytes, w);
    }

    private static int Pack(int a, int r, int g, int b) =>
        (Math.Clamp(a, 0, 255) << 24) | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    private void DrawSectionBands(DrawingContext ctx, Rect wave)
    {
        if (PhraseSegments is not { Count: > 0 } segments) return;
        foreach (var seg in segments)
        {
            double x1 = TimeToX(seg.Start, wave.Width), x2 = TimeToX(seg.Start + seg.Duration, wave.Width);
            if (x2 <= 0 || x1 >= wave.Width) continue;
            var style = GetPhraseStyle(seg.Label);
            ctx.FillRectangle(style.Band, new Rect(Math.Max(0, x1), wave.Top, Math.Min(wave.Width, x2) - Math.Max(0, x1), wave.Height));
            if (x1 >= 0) ctx.DrawLine(style.Edge, new Point(x1, wave.Top), new Point(x1, wave.Bottom));
        }
    }

    /// <summary>Intro region (track start / Intro cue → Intro end) and outro region (Outro → end),
    /// tinted with the view's token brushes. Drawn into the cached static layer.</summary>
    private void DrawSectionRegions(DrawingContext ctx, Rect wave)
    {
        if (Cues is null || TrackDuration <= 0) return;
        if (Engine.Cueing.SectionMarkers.IntroRegion(Cues) is { } intro)
            DrawRegion(ctx, wave, intro.Start, intro.End, IntroRegionFill, IntroRegionEdge, edgeAtStart: false);
        if (Engine.Cueing.SectionMarkers.OutroRegion(Cues, TrackDuration) is { } outro)
            DrawRegion(ctx, wave, outro.Start, outro.End, OutroRegionFill, OutroRegionEdge, edgeAtStart: true);
    }

    private void DrawRegion(DrawingContext ctx, Rect wave, double start, double end, IBrush? fill, IBrush? edge, bool edgeAtStart)
    {
        double x1 = TimeToX(start, wave.Width), x2 = TimeToX(end, wave.Width);
        if (x2 <= 0 || x1 >= wave.Width || x2 - x1 < 0.5) return;
        double l = Math.Max(0, x1), r = Math.Min(wave.Width, x2);
        if (fill != null) ctx.FillRectangle(fill, new Rect(l, wave.Top, r - l, wave.Height));
        if (edge == null) return;
        double ex = edgeAtStart ? x1 : x2;
        if (ex >= 0 && ex <= wave.Width) ctx.DrawLine(GetRegionPen(edge), new Point(ex, wave.Top), new Point(ex, wave.Bottom));
    }

    private readonly Dictionary<IBrush, IPen> _regionPens = new();
    private IPen GetRegionPen(IBrush brush)
    {
        if (!_regionPens.TryGetValue(brush, out var pen))
            _regionPens[brush] = pen = new Pen(brush, 1, dashStyle: new DashStyle(new double[] { 3, 3 }, 0)).ToImmutable();
        return pen;
    }

    private void DrawPhraseMap(DrawingContext ctx, Size size, Rect wave)
    {
        var strip = new Rect(0, size.Height - PhraseMapHeight, size.Width, PhraseMapHeight);
        ctx.FillRectangle(RulerBrush, strip);
        if (PhraseSegments is { Count: > 0 } segments)
        {
            foreach (var seg in segments)
            {
                double x1 = TimeToX(seg.Start, size.Width), x2 = TimeToX(seg.Start + seg.Duration, size.Width);
                if (x2 <= 0 || x1 >= size.Width) continue;
                x1 = Math.Max(0, x1); x2 = Math.Min(size.Width, x2);
                ctx.FillRectangle(GetPhraseStyle(seg.Label).Fill, new Rect(x1, strip.Top + 2, Math.Max(1, x2 - x1 - 1), PhraseMapHeight - 3));
            }
        }
        ctx.DrawLine(DividerPen, new Point(0, strip.Top + 0.5), new Point(size.Width, strip.Top + 0.5));
    }

    private void DrawEnergy(DrawingContext ctx, Rect wave)
    {
        var curve = EnergyCurve;
        if (curve is not { Length: > 1 } || TrackDuration <= 0) return;
        int steps = Math.Max(2, (int)(wave.Width / 3));
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i <= steps; i++)
            {
                double x = wave.Width * i / steps;
                double frac = XToTime(x, wave.Width) / TrackDuration;
                float v = curve[(int)Math.Clamp(frac * curve.Length, 0, curve.Length - 1)];
                var p = new Point(x, wave.Bottom - 4 - v * wave.Height * 0.3);
                if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, EnergyPen, geo);
    }

    private void DrawGridAndRuler(DrawingContext ctx, Size size, Rect wave)
    {
        double bpm = EffectiveBpm;
        if (bpm <= 0 || TrackDuration <= 0) return;
        double beatSec = 60.0 / bpm, barSec = beatSec * 4;
        double pxPerSec = size.Width * Zoom / TrackDuration;
        double barPx = barSec * pxPerSec, beatPx = beatSec * pxPerSec;
        double tStart = XToTime(0, size.Width), tEnd = XToTime(size.Width, size.Width);

        // Bar numbering step so labels never overlap (~40 px apart).
        int labelStep = 1;
        while (labelStep * barPx < 40 && labelStep < 1024) labelStep *= 2;

        long firstBar = (long)Math.Floor((tStart - GridOffset) / barSec);
        long lastBar = (long)Math.Ceiling((tEnd - GridOffset) / barSec);
        if (lastBar - firstBar > 4000) return;

        for (long k = firstBar; k <= lastBar; k++)
        {
            double t = GridOffset + k * barSec;
            double x = TimeToX(t, size.Width);
            if (x >= -1 && x <= size.Width + 1 && t >= 0)
            {
                bool phrase = k % 8 == 0;
                if (phrase || barPx >= 4)
                    ctx.DrawLine(phrase ? PhrasePen : BarPen, new Point(Math.Round(x) + 0.5, wave.Top), new Point(Math.Round(x) + 0.5, wave.Bottom));
                if (k >= 0 && k % labelStep == 0)
                {
                    var label = GetText((k + 1).ToString(CultureInfo.InvariantCulture), MonoTypeface, 10, RulerTextBrush);
                    ctx.DrawText(label, new Point(x + 3, (RulerHeight - label.Height) / 2));
                    ctx.DrawLine(BarPen, new Point(Math.Round(x) + 0.5, RulerHeight - 5), new Point(Math.Round(x) + 0.5, RulerHeight));
                }
            }
            if (beatPx >= 7)
            {
                for (int i = 1; i < 4; i++)
                {
                    double bx = TimeToX(t + beatSec * i, size.Width);
                    if (bx >= 0 && bx <= size.Width)
                        ctx.DrawLine(BeatPen, new Point(Math.Round(bx) + 0.5, wave.Top), new Point(Math.Round(bx) + 0.5, wave.Bottom));
                }
            }
        }
    }

    private void DrawDoubleDropZone(DrawingContext ctx, Rect wave)
    {
        if (Cues is null) return;
        double? d1 = null, d2 = null;
        foreach (var c in Cues)
        {
            if (c.IsLoop || c.Role != CueRole.Drop) continue;
            if (d1 == null || c.Timestamp < d1) { d2 = d1; d1 = c.Timestamp; }
            else if (d2 == null || c.Timestamp < d2) d2 = c.Timestamp;
        }
        if (d1 is not { } a || d2 is not { } b) return;
        double x1 = TimeToX(a, wave.Width), x2 = TimeToX(b, wave.Width);
        if (x2 - x1 < 2 || x2 < 0 || x1 > wave.Width) return;
        ctx.FillRectangle(DoubleDropBrush, new Rect(Math.Max(0, x1), wave.Top, Math.Min(wave.Width, x2) - Math.Max(0, x1), wave.Height));
    }

    private void DrawLoops(DrawingContext ctx, Rect wave)
    {
        if (Cues is null) return;
        foreach (var loop in Cues)
        {
            if (!loop.IsLoop) continue;
            double x1 = TimeToX(loop.Timestamp, wave.Width), x2 = TimeToX(loop.LoopEndSeconds, wave.Width);
            if (x2 <= 0 || x1 >= wave.Width) continue;
            bool selected = ReferenceEquals(loop, SelectedCue);
            ctx.FillRectangle(GetLoopFill(loop.Color, selected), new Rect(Math.Max(0, x1), wave.Top, Math.Min(wave.Width, x2) - Math.Max(0, x1), wave.Height));
            var pen = GetPen(loop.Color, selected ? 2 : 1.5);
            var brush = GetBrush(loop.Color);
            ctx.DrawLine(pen, new Point(x1, wave.Top), new Point(x1, wave.Bottom));
            ctx.DrawLine(pen, new Point(x2, wave.Top), new Point(x2, wave.Bottom));
            // Grab handles
            ctx.FillRectangle(brush, new Rect(x1, wave.Top, 6, 10));
            ctx.FillRectangle(brush, new Rect(x2 - 6, wave.Top, 6, 10));
            var name = GetText(loop.Name, UiTypeface, 10, LabelTextBrush);
            if (x2 - x1 > name.Width + 16)
            {
                ctx.FillRectangle(LabelBgBrush, new Rect(x1 + 8, wave.Bottom - name.Height - 6, name.Width + 6, name.Height + 2), 2);
                ctx.DrawText(name, new Point(x1 + 11, wave.Bottom - name.Height - 5));
            }
        }
    }

    private void DrawCueMarkers(DrawingContext ctx, Rect wave)
    {
        if (Cues is null) return;
        OrbitCue? selected = null;
        foreach (var cue in Cues)
        {
            if (cue.IsLoop) continue;
            if (ReferenceEquals(cue, SelectedCue)) { selected = cue; continue; }
            DrawCue(ctx, wave, cue, false);
        }
        if (selected != null) DrawCue(ctx, wave, selected, true);
    }

    private void DrawCue(DrawingContext ctx, Rect wave, OrbitCue cue, bool isSelected)
    {
        double x = TimeToX(cue.Timestamp, wave.Width);
        if (x < -FlagWidth || x > wave.Width + 1) return;
        var brush = GetBrush(cue.Color);
        if (isSelected) ctx.FillRectangle(SelectionBrush, new Rect(x - 3, wave.Top, 6, wave.Height));
        ctx.DrawLine(GetPen(cue.Color, isSelected ? 2 : 1), new Point(x, wave.Top), new Point(x, wave.Bottom));

        double labelX;
        if (cue.SlotIndex is >= 0 and <= 7)
        {
            // Hot cue: coloured flag with its pad letter.
            ctx.FillRectangle(brush, new Rect(x, wave.Top, FlagWidth, FlagHeight), 2);
            var letter = GetText(cue.SlotLabel, UiTypeface, 10, FlagTextBrush, FontWeight.Bold);
            ctx.DrawText(letter, new Point(x + (FlagWidth - letter.Width) / 2, wave.Top + (FlagHeight - letter.Height) / 2));
            labelX = x + FlagWidth + 3;
        }
        else
        {
            // Memory cue: small downward triangle, like the CDJ's memory markers.
            using (ctx.PushTransform(Matrix.CreateTranslation(x, wave.Top)))
                ctx.DrawGeometry(brush, null, MemoryMarker);
            labelX = x + 7;
        }

        var name = string.IsNullOrWhiteSpace(cue.Name) ? cue.Role.ToString() : cue.Name;
        var label = GetText(name, UiTypeface, isSelected ? 11 : 10, LabelTextBrush, isSelected ? FontWeight.SemiBold : FontWeight.Normal);
        ctx.FillRectangle(LabelBgBrush, new Rect(labelX - 2, wave.Top + 1, label.Width + 4, label.Height), 2);
        ctx.DrawText(label, new Point(labelX, wave.Top + 1));
    }

    private static readonly Geometry MemoryMarker = CreateTriangle(new Point(-5, 0), new Point(5, 0), new Point(0, 8));

    // ── Resource caches ────────────────────────────────────────────────────

    private static Geometry CreateTriangle(Point a, Point b, Point c)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open()) { g.BeginFigure(a, true); g.LineTo(b); g.LineTo(c); g.EndFigure(true); }
        return geo;
    }

    private static Color ParseColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c)) return c;
        return Color.FromRgb(232, 234, 237);
    }

    private IBrush GetBrush(string? hex)
    {
        hex ??= "";
        if (!_brushCache.TryGetValue(hex, out var b))
            _brushCache[hex] = b = new SolidColorBrush(ParseColor(hex)).ToImmutable();
        return b;
    }

    private IBrush GetLoopFill(string? hex, bool selected)
    {
        var key = (hex ?? "") + (selected ? "|s" : "");
        if (!_loopFillCache.TryGetValue(key, out var b))
        {
            var c = ParseColor(hex);
            _loopFillCache[key] = b = new SolidColorBrush(Color.FromArgb(selected ? (byte)70 : (byte)45, c.R, c.G, c.B)).ToImmutable();
        }
        return b;
    }

    private IPen GetPen(string? hex, double thickness)
    {
        var key = (hex ?? "") + "|" + thickness.ToString(CultureInfo.InvariantCulture);
        if (!_penCache.TryGetValue(key, out var p))
            _penCache[key] = p = new Pen(GetBrush(hex), thickness).ToImmutable();
        return p;
    }

    private FormattedText GetText(string text, Typeface typeface, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
    {
        var key = $"{text}|{size}|{(int)weight}|{typeface.FontFamily.Name}|{brush.GetHashCode()}";
        if (_textCache.TryGetValue(key, out var ft)) return ft;
        if (_textCache.Count > 1024) _textCache.Clear();
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(typeface.FontFamily, FontStyle.Normal, weight), size, brush);
        _textCache[key] = ft;
        return ft;
    }

    private static (IBrush Fill, IBrush Band, IPen Edge) GetPhraseStyle(string? label)
    {
        label ??= "";
        if (PhraseStyles.TryGetValue(label, out var s)) return s;
        var c = label.ToLowerInvariant() switch
        {
            "intro" => Color.FromRgb(90, 167, 232),
            "build" or "buildup" => Color.FromRgb(229, 166, 69),
            "drop" => Color.FromRgb(229, 96, 90),
            "breakdown" => Color.FromRgb(170, 114, 255),
            "outro" => Color.FromRgb(78, 201, 176),
            "bridge" => Color.FromRgb(122, 128, 138),
            _ => Color.FromRgb(92, 98, 107),
        };
        s = (new SolidColorBrush(Color.FromArgb(170, c.R, c.G, c.B)).ToImmutable(),
             new SolidColorBrush(Color.FromArgb(12, c.R, c.G, c.B)).ToImmutable(),
             new Pen(new SolidColorBrush(Color.FromArgb(50, c.R, c.G, c.B)).ToImmutable(), 1).ToImmutable());
        PhraseStyles[label] = s;
        return s;
    }

    // ── Mouse interaction ──────────────────────────────────────────────────

    private (DragKind Kind, OrbitCue? Cue) HitTest(Point pt)
    {
        if (Cues is null) return (DragKind.None, null);
        double w = Bounds.Width;
        double best = HitRadius;
        (DragKind, OrbitCue?) hit = (DragKind.None, null);

        foreach (var c in Cues)
        {
            if (c.IsLoop)
            {
                double d1 = Math.Abs(pt.X - TimeToX(c.Timestamp, w));
                double d2 = Math.Abs(pt.X - TimeToX(c.LoopEndSeconds, w));
                if (d1 < best) { best = d1; hit = (DragKind.LoopStart, c); }
                if (d2 < best) { best = d2; hit = (DragKind.LoopEnd, c); }
            }
            else
            {
                double x = TimeToX(c.Timestamp, w);
                double d = Math.Abs(pt.X - x);
                // The hot cue flag (to the right of the line) is grabbable too.
                bool onFlag = c.SlotIndex >= 0 && pt.Y >= RulerHeight && pt.Y <= RulerHeight + FlagHeight && pt.X >= x && pt.X <= x + FlagWidth;
                if (onFlag) d = Math.Min(d, 0.5);
                if (d < best) { best = d; hit = (DragKind.Cue, c); }
            }
        }
        return hit;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed || TrackDuration <= 0) return;
        var pt = e.GetPosition(this);
        _pressPoint = pt;
        _dragMoved = false;

        var (kind, cue) = HitTest(pt);
        if (kind != DragKind.None && cue != null)
        {
            _drag = kind;
            _dragCue = cue;
            if (SelectCueCommand?.CanExecute(cue) == true) SelectCueCommand.Execute(cue);
            e.Pointer.Capture(this);
            return;
        }

        // Empty waveform: move the playhead, and keep scrubbing while held.
        _drag = DragKind.Scrub;
        e.Pointer.Capture(this);
        ScrubbingChanged?.Invoke(this, true);
        Seek(TimeAt(pt), force: true);
    }

    private void Seek(double seconds, bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastScrubSeek).TotalMilliseconds < ThrottleMs) return;
        _lastScrubSeek = now;
        if (SeekCommand?.CanExecute(seconds) == true) SeekCommand.Execute(seconds);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        double w = Bounds.Width;

        switch (_drag)
        {
            case DragKind.None:
                var (kind, _) = HitTest(pt);
                Cursor = kind == DragKind.None ? ScrubCursor : HandleCursor;
                SetHoverTip(kind != DragKind.None);
                return;

            case DragKind.Scrub:
                Seek(TimeAt(pt), force: false);
                return;
        }

        if (_dragCue == null) return;
        if (!_dragMoved)
        {
            if (Math.Abs(pt.X - _pressPoint.X) < MarkerGesture<OrbitCue>.DragThresholdPixels) return; // a click, not a drag
            _dragMoved = true;
            if (CueDragStartedCommand?.CanExecute(_dragCue) == true) CueDragStartedCommand.Execute(_dragCue);
        }

        double t = ApplySnap(XToTime(pt.X, w), e.KeyModifiers);
        switch (_drag)
        {
            case DragKind.Cue: _dragCue.Timestamp = t; break;
            case DragKind.LoopStart: _dragCue.Timestamp = Math.Min(t, _dragCue.LoopEndSeconds - 0.05); t = _dragCue.Timestamp; break;
            case DragKind.LoopEnd: _dragCue.LoopEndSeconds = Math.Max(t, _dragCue.Timestamp + 0.05); t = _dragCue.LoopEndSeconds; break;
        }

        _readoutX = TimeToX(t, w);
        var bar = CueForgeGrid.FormatBarBeat(t, EffectiveBpm, GridOffset);
        var text = bar.Length > 0 ? $"{CueForgeGrid.FormatTime(t)}  {bar}" : CueForgeGrid.FormatTime(t);
        _readout = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoTypeface, 11, LabelTextBrush);
        InvalidateStaticThrottled();
    }

    private bool _hoverTipShown;
    private void SetHoverTip(bool overMarker)
    {
        if (overMarker == _hoverTipShown) return;
        _hoverTipShown = overMarker;
        ToolTip.SetTip(this, overMarker ? CueFlagTip : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoverTip(false);
    }

    private void InvalidateStaticThrottled()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastInvalidate).TotalMilliseconds < ThrottleMs) { InvalidateVisual(); return; }
        _lastInvalidate = now;
        InvalidateStaticCache();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer, commit: true, e.GetPosition(this));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(null, commit: true, null);
    }

    private void EndDrag(IPointer? pointer, bool commit, Point? at)
    {
        var kind = _drag;
        var cue = _dragCue;
        bool moved = _dragMoved;
        _drag = DragKind.None;
        _dragCue = null;
        _dragMoved = false;
        _readout = null;
        pointer?.Capture(null);

        if (kind == DragKind.Scrub)
        {
            if (at is { } p) Seek(TimeAt(p), force: true);
            ScrubbingChanged?.Invoke(this, false);
        }
        else if (commit && moved && cue != null)
        {
            InvalidateStaticCache();
            if (CueUpdatedCommand?.CanExecute(cue) == true) CueUpdatedCommand.Execute(cue);
        }
        else if (commit && !moved && cue != null && at != null)
        {
            // Released without passing the drag threshold: a click — play from the cue. The cue
            // itself was never touched, so a click can't nudge it.
            if (CueClickedCommand?.CanExecute(cue) == true) CueClickedCommand.Execute(cue);
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (TrackDuration <= 0 || Bounds.Width <= 0) return;
        double zoom = Zoom;
        double window = 1.0 / zoom;
        bool scrollMode = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0 || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);

        if (scrollMode)
        {
            double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            ScrollOffset = CueForgeGrid.ClampScroll(ScrollOffset - delta * window * 0.15, zoom);
        }
        else
        {
            double anchor = e.GetPosition(this).X / Bounds.Width;
            double newZoom = Math.Clamp(zoom * Math.Pow(1.2, e.Delta.Y), 1.0, Math.Max(1.0, MaxZoom));
            double scroll = CueForgeGrid.ZoomAround(ScrollOffset, zoom, newZoom, anchor);
            ZoomLevel = newZoom;
            ScrollOffset = scroll;
        }
        e.Handled = true;
    }

    // ── Invalidation ───────────────────────────────────────────────────────

    private void InvalidateStaticCache()
    {
        _staticDirty = true;
        InvalidateVisual();
    }

    private void OnCuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateStaticCache();
    private void OnPhrasesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateStaticCache();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CuesProperty)
        {
            if (change.OldValue is ObservableCollection<OrbitCue> old) old.CollectionChanged -= OnCuesCollectionChanged;
            if (change.NewValue is ObservableCollection<OrbitCue> fresh) fresh.CollectionChanged += OnCuesCollectionChanged;
        }
        else if (change.Property == PhraseSegmentsProperty)
        {
            if (change.OldValue is ObservableCollection<PhraseSegment> old) old.CollectionChanged -= OnPhrasesCollectionChanged;
            if (change.NewValue is ObservableCollection<PhraseSegment> fresh) fresh.CollectionChanged += OnPhrasesCollectionChanged;
        }

        if (change.Property == CurrentPlayPositionProperty)
        {
            InvalidateVisual(); // playhead only — never rebuilds the cache
            return;
        }

        if (change.Property == RevisionProperty)
        {
            if (_drag is DragKind.Cue or DragKind.LoopStart or DragKind.LoopEnd) InvalidateStaticThrottled();
            else InvalidateStaticCache();
            return;
        }

        if (change.Property == CuesProperty || change.Property == PhraseSegmentsProperty ||
            change.Property == BpmProperty || change.Property == GridBpmProperty || change.Property == GridOffsetProperty ||
            change.Property == TrackDurationProperty || change.Property == BackgroundProperty ||
            change.Property == WaveformLowProperty || change.Property == WaveformMidProperty ||
            change.Property == WaveformHighProperty || change.Property == EnergyCurveProperty ||
            change.Property == VocalDensityCurveProperty || change.Property == OnsetDensityCurveProperty ||
            change.Property == ZoomLevelProperty || change.Property == ScrollOffsetProperty ||
            change.Property == SelectedCueProperty ||
            change.Property == IntroRegionFillProperty || change.Property == IntroRegionEdgeProperty ||
            change.Property == OutroRegionFillProperty || change.Property == OutroRegionEdgeProperty)
        {
            InvalidateStaticCache();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_drag != DragKind.None) EndDrag(null, commit: true, null);
    }
}
