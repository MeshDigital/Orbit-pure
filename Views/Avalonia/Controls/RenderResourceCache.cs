using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Immutable brushes, pens, parsed colours and laid-out text for per-frame drawing (#318). Render code asks for
/// "this colour at this opacity" and gets the same immutable object every frame instead of allocating a new
/// <see cref="SolidColorBrush"/>/<see cref="Pen"/>/<see cref="FormattedText"/> 60 times a second (CLAUDE.md:
/// "Don't allocate brushes/pens in Render; cache them"). Opacity is quantised to 1/256, so a fading highlight
/// reuses at most 256 entries. Used from the UI thread only (control <c>Render</c>, not ICustomDrawOperation).
/// </summary>
internal static class RenderResourceCache
{
    private const int MaxEntries = 4096;

    private static readonly Dictionary<(uint Color, int Opacity), ImmutableSolidColorBrush> BrushCache = new();
    private static readonly Dictionary<(uint Color, int Opacity, int Thickness, PenLineCap Cap, PenLineJoin Join), ImmutablePen> PenCache = new();
    private static readonly Dictionary<string, Color> ColorCache = new(StringComparer.OrdinalIgnoreCase);

    private static int Quantise(double opacity) => (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

    public static ImmutableSolidColorBrush Brush(Color color, double opacity = 1)
    {
        var key = (color.ToUInt32(), Quantise(opacity));
        if (BrushCache.TryGetValue(key, out var brush)) return brush;
        if (BrushCache.Count >= MaxEntries) BrushCache.Clear();
        return BrushCache[key] = new ImmutableSolidColorBrush(color, key.Item2 / 255.0);
    }

    public static ImmutablePen Pen(Color color, double opacity, double thickness,
        PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter)
    {
        var key = (color.ToUInt32(), Quantise(opacity), (int)Math.Round(thickness * 100), cap, join);
        if (PenCache.TryGetValue(key, out var pen)) return pen;
        if (PenCache.Count >= MaxEntries) PenCache.Clear();
        return PenCache[key] = new ImmutablePen(Brush(color, opacity), thickness, lineCap: cap, lineJoin: join);
    }

    /// <summary>A pen over any brush (e.g. a themed <c>PlayheadBrush</c>), made immutable once.</summary>
    public static ImmutablePen Pen(IBrush brush, double thickness) =>
        brush is ISolidColorBrush solid
            ? Pen(solid.Color, solid.Opacity, thickness)
            : new ImmutablePen(brush.ToImmutable(), thickness);

    /// <summary><see cref="Color.Parse(string)"/>, cached; unparseable text falls back to <paramref name="fallback"/>.</summary>
    public static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (ColorCache.TryGetValue(text, out var color)) return color;
        if (ColorCache.Count >= MaxEntries) ColorCache.Clear();
        return ColorCache[text] = Color.TryParse(text, out var parsed) ? parsed : fallback;
    }
}

/// <summary>Per-control cache of laid-out labels (text + size + weight + brush colour → <see cref="FormattedText"/>).</summary>
internal sealed class FormattedTextCache
{
    private const int MaxEntries = 512;
    private readonly Dictionary<(string Text, int Size, FontWeight Weight, uint Color, int Opacity), FormattedText> _cache = new();

    public FormattedText Get(string text, double size, FontWeight weight, Color color, double opacity = 1)
    {
        var key = (text, (int)Math.Round(size * 10), weight, color.ToUInt32(), (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255));
        if (_cache.TryGetValue(key, out var ft)) return ft;
        if (_cache.Count >= MaxEntries) _cache.Clear();
        return _cache[key] = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, RenderResourceCache.Brush(color, opacity));
    }

    public int Count => _cache.Count;
}
