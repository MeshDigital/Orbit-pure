using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using SLSKDONET.ViewModels.EditLab;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// The Edit Lab's lane/timeline arranger: a bar ruler, one row per lane, clips as blocks. Click to
/// select, drag to move (snaps to the host's grid; hold Shift for free placement), drag a clip edge
/// to trim, click the ruler to move the playhead, drag across the ruler to set the loop region.
/// Wheel scrolls in time, Ctrl+wheel zooms. Esc during a drag puts the clip back.
/// Drawn in <see cref="Render"/> on the UI thread (not a custom draw operation); brushes, pens and
/// text layouts are cached and rebuilt only when the theme or the labels change.
/// </summary>
public sealed class EditArrangerControl : Control
{
    public const double RulerHeight = 24;
    public const double LaneHeight = 48;
    private const double EdgeGrip = 6;
    private const double DragThreshold = 3;

    public static readonly StyledProperty<double> ViewStartBarProperty =
        AvaloniaProperty.Register<EditArrangerControl, double>(nameof(ViewStartBar));

    public static readonly DirectProperty<EditArrangerControl, double> ScrollMaximumProperty =
        AvaloniaProperty.RegisterDirect<EditArrangerControl, double>(nameof(ScrollMaximum), c => c.ScrollMaximum);

    public static readonly DirectProperty<EditArrangerControl, double> VisibleBarsProperty =
        AvaloniaProperty.RegisterDirect<EditArrangerControl, double>(nameof(VisibleBars), c => c.VisibleBars);

    static EditArrangerControl()
    {
        AffectsRender<EditArrangerControl>(ViewStartBarProperty);
        FocusableProperty.OverrideDefaultValue<EditArrangerControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<EditArrangerControl>(true);
    }

    /// <summary>First visible bar (0-based) — the horizontal scroll position.</summary>
    public double ViewStartBar
    {
        get => GetValue(ViewStartBarProperty);
        set => SetValue(ViewStartBarProperty, Math.Max(0, value));
    }

    private double _scrollMaximum;
    public double ScrollMaximum { get => _scrollMaximum; private set => SetAndRaise(ScrollMaximumProperty, ref _scrollMaximum, value); }

    private double _visibleBars = 16;
    public double VisibleBars { get => _visibleBars; private set => SetAndRaise(VisibleBarsProperty, ref _visibleBars, value); }

    private IEditArrangerHost? _host;

    // ── cached drawing resources ─────────────────────────────────────────────
    private bool _resourcesReady;
    private IBrush _bg = Brushes.Transparent, _laneAlt = Brushes.Transparent, _ruler = Brushes.Transparent;
    private IBrush _text0 = Brushes.Transparent, _text2 = Brushes.Transparent, _accent = Brushes.Transparent;
    private IBrush _loopFill = Brushes.Transparent, _loopRuler = Brushes.Transparent, _warning = Brushes.Transparent;
    private Pen _barPen = new(), _beatPen = new(), _playheadPen = new(), _selectedPen = new(), _missingPen = new(), _fadePen = new(), _loopEdgePen = new();
    private readonly Dictionary<EditStem, (IBrush Fill, Pen Edge)> _stemPaint = new();
    private readonly Dictionary<string, FormattedText> _textCache = new();
    private Typeface _typeface;
    private Typeface _monoTypeface;

    // ── drag state ───────────────────────────────────────────────────────────
    private enum DragKind { None, Move, TrimStart, TrimEnd, Ruler, Loop }
    private DragKind _drag;
    private Guid _dragClip;
    private Point _pressPoint;
    private double _pressBar;
    private double _clipStartAtPress, _clipEndAtPress;
    private bool _dragMoved;

    public EditArrangerControl()
    {
        Cursor = Cursor.Default;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_host != null)
        {
            _host.ArrangementChanged -= OnArrangementChanged;
            _host.PlayheadChanged -= OnPlayheadChanged;
        }
        _host = DataContext as IEditArrangerHost;
        if (_host != null)
        {
            _host.ArrangementChanged += OnArrangementChanged;
            _host.PlayheadChanged += OnPlayheadChanged;
        }
        OnArrangementChanged();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _resourcesReady = false;
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _resourcesReady = false;
        _textCache.Clear();
        InvalidateVisual();
    }

    private void OnArrangementChanged()
    {
        UpdateScrollRange();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnPlayheadChanged()
    {
        // Page-follow while playing: keep the playhead in view.
        if (_host != null && _drag == DragKind.None)
        {
            double ph = _host.PlayheadBar;
            if (ph < ViewStartBar || ph > ViewStartBar + VisibleBars * 0.95)
                ViewStartBar = Math.Max(0, ph - VisibleBars * 0.1);
        }
        InvalidateVisual();
    }

    private void UpdateScrollRange()
    {
        double ppb = PixelsPerBar;
        VisibleBars = Bounds.Width > 0 ? Bounds.Width / ppb : 16;
        double length = (_host?.Project?.LengthBars ?? 0) + 16;
        ScrollMaximum = Math.Max(0, length - VisibleBars);
    }

    private double PixelsPerBar => Math.Max(1, _host?.PixelsPerBar ?? 48);
    private int LaneCount => _host?.Project?.Lanes.Count ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        return new Size(width, RulerHeight + Math.Max(1, LaneCount) * LaneHeight);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateScrollRange();
    }

    // ── coordinates ──────────────────────────────────────────────────────────

    private double BarToX(double bar) => (bar - ViewStartBar) * PixelsPerBar;
    private double XToBar(double x) => ViewStartBar + x / PixelsPerBar;

    private Rect ClipRect(int laneIndex, EditClip clip)
    {
        double x0 = BarToX(clip.StartBar), x1 = BarToX(clip.EndBar);
        return new Rect(x0, RulerHeight + laneIndex * LaneHeight + 2, Math.Max(2, x1 - x0), LaneHeight - 4);
    }

    private (EditClip Clip, int Lane, Rect Rect)? HitClip(Point p)
    {
        var project = _host?.Project;
        if (project == null || p.Y < RulerHeight) return null;
        int lane = (int)((p.Y - RulerHeight) / LaneHeight);
        if (lane < 0 || lane >= project.Lanes.Count) return null;
        // Topmost = last drawn.
        foreach (var clip in project.Lanes[lane].Clips.AsEnumerable().Reverse())
        {
            var r = ClipRect(lane, clip);
            if (r.Contains(p)) return (clip, lane, r);
        }
        return null;
    }

    private double SnapBar(double bar, KeyModifiers mods)
    {
        if (_host == null || mods.HasFlag(KeyModifiers.Shift)) return bar;
        return EditProjectEditor.Snap(bar, _host.SnapBeats, _host.Project?.BeatsPerBar ?? 4);
    }

    // ── input ────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_host?.Project == null) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        var p = point.Position;
        _pressPoint = p;
        _pressBar = XToBar(p.X);
        _dragMoved = false;

        if (p.Y < RulerHeight)
        {
            _drag = DragKind.Ruler;
            _host.SeekToBar(Math.Max(0, SnapBar(_pressBar, e.KeyModifiers)));
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var hit = HitClip(p);
        if (hit is { } h)
        {
            _host.SelectClip(h.Clip.Id);
            _dragClip = h.Clip.Id;
            _clipStartAtPress = h.Clip.StartBar;
            _clipEndAtPress = h.Clip.EndBar;
            _drag = p.X - h.Rect.Left <= EdgeGrip ? DragKind.TrimStart
                : h.Rect.Right - p.X <= EdgeGrip ? DragKind.TrimEnd
                : DragKind.Move;
            e.Pointer.Capture(this);
        }
        else
        {
            _host.SelectClip(null);
            _host.SeekToBar(Math.Max(0, SnapBar(_pressBar, e.KeyModifiers)));
            _drag = DragKind.None;
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_host?.Project == null) return;

        if (_drag == DragKind.None)
        {
            UpdateHoverCursor(p);
            return;
        }

        if (!_dragMoved && Math.Abs(p.X - _pressPoint.X) < DragThreshold && Math.Abs(p.Y - _pressPoint.Y) < DragThreshold) return;
        if (!_dragMoved)
        {
            _dragMoved = true;
            if (_drag is DragKind.Move or DragKind.TrimStart or DragKind.TrimEnd) _host.BeginClipGesture();
            if (_drag == DragKind.Ruler) _drag = DragKind.Loop;
        }

        double bar = XToBar(p.X);
        switch (_drag)
        {
            case DragKind.Move:
            {
                double start = SnapBar(_clipStartAtPress + (bar - _pressBar), e.KeyModifiers);
                int lane = Math.Clamp((int)((p.Y - RulerHeight) / LaneHeight), 0, Math.Max(0, LaneCount - 1));
                _host.MoveClipLive(_dragClip, Math.Max(0, start), lane);
                break;
            }
            case DragKind.TrimStart:
                _host.TrimClipStartLive(_dragClip, Math.Min(SnapBar(bar, e.KeyModifiers), _clipEndAtPress - EditProjectEditor.MinClipBars));
                break;
            case DragKind.TrimEnd:
                _host.TrimClipEndLive(_dragClip, Math.Max(SnapBar(bar, e.KeyModifiers), _clipStartAtPress + EditProjectEditor.MinClipBars));
                break;
            case DragKind.Loop:
            {
                double a = SnapBar(Math.Max(0, _pressBar), e.KeyModifiers), b = SnapBar(Math.Max(0, bar), e.KeyModifiers);
                _loopPreview = (Math.Min(a, b), Math.Max(a, b));
                InvalidateVisual();
                break;
            }
        }
        e.Handled = true;
    }

    private (double Start, double End)? _loopPreview;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        FinishDrag(commit: true);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        FinishDrag(commit: true);
    }

    private void FinishDrag(bool commit)
    {
        if (_host == null) { _drag = DragKind.None; return; }
        if (_drag == DragKind.Loop && _loopPreview is { } lp)
        {
            if (commit && lp.End - lp.Start > 0.01) _host.SetLoopRegion(lp.Start, lp.End);
            _loopPreview = null;
        }
        else if (_dragMoved && _drag is DragKind.Move or DragKind.TrimStart or DragKind.TrimEnd)
        {
            if (commit) _host.EndClipGesture();
            else _host.CancelClipGesture();
        }
        _drag = DragKind.None;
        _dragMoved = false;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drag != DragKind.None)
        {
            FinishDrag(commit: false);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_host == null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double anchorBar = XToBar(e.GetPosition(this).X);
            double factor = e.Delta.Y > 0 ? 1.25 : 1 / 1.25;
            _host.PixelsPerBar *= factor;
            ViewStartBar = Math.Max(0, anchorBar - e.GetPosition(this).X / PixelsPerBar);
            e.Handled = true;
        }
        else if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            double delta = (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y) * Math.Max(1, VisibleBars / 8);
            ViewStartBar = Math.Clamp(ViewStartBar + delta, 0, Math.Max(0, ScrollMaximum));
            e.Handled = true;
        }
        UpdateScrollRange();
    }

    private void UpdateHoverCursor(Point p)
    {
        var hit = HitClip(p);
        var wanted = hit is { } h && (p.X - h.Rect.Left <= EdgeGrip || h.Rect.Right - p.X <= EdgeGrip)
            ? StandardCursorType.SizeWestEast
            : hit != null ? StandardCursorType.Hand
            : StandardCursorType.Arrow;
        if (_cursorType != wanted)
        {
            _cursorType = wanted;
            Cursor = new Cursor(wanted);
        }
    }

    private StandardCursorType _cursorType = StandardCursorType.Arrow;

    // ── drawing ──────────────────────────────────────────────────────────────

    private IBrush Res(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        return Brushes.Transparent;
    }

    private static IBrush WithOpacity(IBrush brush, double opacity) =>
        brush is ISolidColorBrush s ? new SolidColorBrush(s.Color, opacity) : brush;

    private void EnsureResources()
    {
        if (_resourcesReady) return;
        _resourcesReady = true;
        _bg = Res("BrushBg0");
        _laneAlt = Res("BrushBg2");
        _ruler = Res("BrushBg2");
        _text0 = Res("BrushText0");
        _text2 = Res("BrushText2");
        _accent = Res("BrushAccent");
        _warning = Res("BrushWarning");
        _loopFill = WithOpacity(_accent, 0.08);
        _loopRuler = WithOpacity(_accent, 0.35);
        _barPen = new Pen(Res("BrushBorder1"), 1);
        _beatPen = new Pen(Res("BrushBorder0"), 1);
        _playheadPen = new Pen(_accent, 1.5);
        _selectedPen = new Pen(_text0, 2);
        _missingPen = new Pen(_warning, 1.5, new DashStyle(new double[] { 3, 3 }, 0));
        _fadePen = new Pen(WithOpacity(_text0, 0.55), 1);
        _loopEdgePen = new Pen(_accent, 1);

        _stemPaint.Clear();
        void Stem(EditStem stem, string key)
        {
            var b = Res(key);
            _stemPaint[stem] = (WithOpacity(b, 0.28), new Pen(b, 1));
        }
        Stem(EditStem.FullMix, "BrushAccent");
        Stem(EditStem.Vocals, "BrushStemVocals");
        Stem(EditStem.Drums, "BrushStemDrums");
        Stem(EditStem.Bass, "BrushStemBass");
        Stem(EditStem.Other, "BrushStemOther");
        Stem(EditStem.Instrumental, "BrushStemOther");

        var ui = this.TryFindResource("FontUI", out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        var mono = this.TryFindResource("FontMono", out var m) && m is FontFamily mf ? mf : FontFamily.Default;
        _typeface = new Typeface(ui);
        _monoTypeface = new Typeface(mono);
        _textCache.Clear();
    }

    private FormattedText Text(string text, bool mono, IBrush brush, double size)
    {
        string key = (mono ? "m|" : "u|") + size.ToString(CultureInfo.InvariantCulture) + "|" + brush.GetHashCode() + "|" + text;
        if (_textCache.TryGetValue(key, out var ft)) return ft;
        if (_textCache.Count > 2000) _textCache.Clear();
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, mono ? _monoTypeface : _typeface, size, brush);
        _textCache[key] = ft;
        return ft;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureResources();
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(_bg, bounds);
        var project = _host?.Project;
        if (project == null) return;

        double width = bounds.Width;
        double height = bounds.Height;
        double ppb = PixelsPerBar;
        int bpb = Math.Max(1, project.BeatsPerBar);

        // Lane rows
        for (int i = 0; i < project.Lanes.Count; i++)
        {
            if (i % 2 == 1) context.FillRectangle(_laneAlt, new Rect(0, RulerHeight + i * LaneHeight, width, LaneHeight));
        }

        // Loop region (behind the grid)
        var loop = _loopPreview ?? (project.HasLoopRegion ? (project.LoopStartBar!.Value, project.LoopEndBar!.Value) : ((double, double)?)null);
        if (loop is { } lr)
        {
            double lx0 = BarToX(lr.Item1), lx1 = BarToX(lr.Item2);
            context.FillRectangle(_loopFill, new Rect(lx0, RulerHeight, lx1 - lx0, height - RulerHeight));
            context.DrawLine(_loopEdgePen, new Point(lx0, RulerHeight), new Point(lx0, height));
            context.DrawLine(_loopEdgePen, new Point(lx1, RulerHeight), new Point(lx1, height));
        }

        // Grid + ruler
        context.FillRectangle(_ruler, new Rect(0, 0, width, RulerHeight));
        if (loop is { } lr2)
        {
            double lx0 = BarToX(lr2.Item1), lx1 = BarToX(lr2.Item2);
            context.FillRectangle(_loopRuler, new Rect(lx0, 0, lx1 - lx0, RulerHeight));
        }

        int firstBar = Math.Max(0, (int)Math.Floor(ViewStartBar));
        int lastBar = (int)Math.Ceiling(ViewStartBar + width / ppb);
        int labelEvery = ppb >= 40 ? 1 : ppb >= 20 ? 2 : ppb >= 10 ? 4 : ppb >= 5 ? 8 : 16;
        bool drawBeats = ppb / bpb >= 8;
        for (int bar = firstBar; bar <= lastBar; bar++)
        {
            double x = Math.Round(BarToX(bar)) + 0.5;
            context.DrawLine(_barPen, new Point(x, bar % labelEvery == 0 ? 0 : RulerHeight * 0.6), new Point(x, height));
            if (bar % labelEvery == 0)
                context.DrawText(Text((bar + 1).ToString(CultureInfo.InvariantCulture), true, _text2, 11), new Point(x + 4, 5));
            if (drawBeats)
            {
                for (int beat = 1; beat < bpb; beat++)
                {
                    double bx = Math.Round(BarToX(bar + beat / (double)bpb)) + 0.5;
                    context.DrawLine(_beatPen, new Point(bx, RulerHeight), new Point(bx, height));
                }
            }
        }

        // Clips
        var selected = _host!.SelectedClipId;
        for (int lane = 0; lane < project.Lanes.Count; lane++)
        {
            var laneModel = project.Lanes[lane];
            foreach (var clip in laneModel.Clips)
            {
                var r = ClipRect(lane, clip);
                if (r.Right < 0 || r.Left > width) continue;
                DrawClip(context, clip, r, clip.Id == selected, laneModel.Muted || clip.Muted, width);
            }
        }

        // Playhead
        double px = BarToX(_host.PlayheadBar);
        if (px >= 0 && px <= width) context.DrawLine(_playheadPen, new Point(px, 0), new Point(px, height));
    }

    private void DrawClip(DrawingContext context, EditClip clip, Rect r, bool selected, bool muted, double viewWidth)
    {
        var paint = _stemPaint.TryGetValue(clip.Stem, out var sp) ? sp : (_loopFill, _barPen);
        bool canPlay = _host!.ClipCanPlay(clip);
        using (context.PushOpacity(muted ? 0.35 : 1.0))
        {
            context.DrawRectangle(paint.Item1, canPlay ? paint.Item2 : _missingPen, r, 4, 4);

            // Fades: a line from the corner to the full-level point.
            double ppb = PixelsPerBar;
            if (clip.FadeInBars > 0)
                context.DrawLine(_fadePen, new Point(r.Left, r.Bottom), new Point(r.Left + clip.FadeInBars * ppb, r.Top));
            if (clip.FadeOutBars > 0)
                context.DrawLine(_fadePen, new Point(r.Right - clip.FadeOutBars * ppb, r.Top), new Point(r.Right, r.Bottom));

            if (r.Width > 24)
            {
                using (context.PushClip(new Rect(r.X + 1, r.Y, Math.Max(0, Math.Min(r.Width, viewWidth - r.X) - 2), r.Height)))
                {
                    double tx = Math.Max(r.Left, 0) + 6;
                    context.DrawText(Text(_host.ClipLabel(clip), false, _text0, 12), new Point(tx, r.Top + 4));
                    var match = _host.MatchFor(clip);
                    string facts = !canPlay ? "Needs stems"
                        : (match.FollowsTempo ? EditMatching.FormatStretch(match.StretchPercent) : "native")
                          + (match.Semitones != 0 ? "  " + EditMatching.FormatSemitones(match.Semitones) : "")
                          + (clip.GainDb != 0 ? string.Create(CultureInfo.InvariantCulture, $"  {clip.GainDb:+0.0;-0.0} dB") : "");
                    var factBrush = !canPlay || match.StretchWarning ? _warning : _text2;
                    context.DrawText(Text(facts, true, factBrush, 11), new Point(tx, r.Top + 22));
                }
            }
        }
        if (selected) context.DrawRectangle(null, _selectedPen, r.Deflate(1), 4, 4);
    }
}
