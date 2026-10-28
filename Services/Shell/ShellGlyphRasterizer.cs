using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.Shell;

public enum ShellGlyph
{
    Previous,
    Play,
    Pause,
    Next,
}

/// <summary>
/// Rasterises the taskbar thumbnail-toolbar glyphs (previous / play / pause / next) from simple
/// polygons into straight-alpha 32-bit ARGB pixels, so the Win32 layer can turn them into
/// HICONs at whatever small-icon size the current DPI needs. Pure and deterministic — no GDI, no
/// emoji fonts, no bundled bitmaps that blur at 125/150 %.
/// </summary>
public static class ShellGlyphRasterizer
{
    private const int Supersample = 4;

    /// <summary>Returns <paramref name="size"/>×<paramref name="size"/> pixels, row-major, top-down, as 0xAARRGGBB.</summary>
    public static uint[] Render(ShellGlyph glyph, int size, byte r = 255, byte g = 255, byte b = 255)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var polygons = GetPolygons(glyph);
        var pixels = new uint[size * size];
        var samples = Supersample * Supersample;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < Supersample; sy++)
                {
                    var py = (y + (sy + 0.5) / Supersample) / size;
                    for (var sx = 0; sx < Supersample; sx++)
                    {
                        var px = (x + (sx + 0.5) / Supersample) / size;
                        foreach (var poly in polygons)
                        {
                            if (Contains(poly, px, py)) { hits++; break; }
                        }
                    }
                }

                if (hits == 0) continue;
                var alpha = (uint)Math.Round(255.0 * hits / samples);
                pixels[y * size + x] = (alpha << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
            }
        }

        return pixels;
    }

    /// <summary>Glyph outlines in unit coordinates (0..1, y down), each a closed polygon.</summary>
    internal static IReadOnlyList<(double X, double Y)[]> GetPolygons(ShellGlyph glyph) => glyph switch
    {
        ShellGlyph.Play => new[]
        {
            new[] { (0.30, 0.18), (0.82, 0.50), (0.30, 0.82) },
        },
        ShellGlyph.Pause => new[]
        {
            Rect(0.24, 0.20, 0.42, 0.80),
            Rect(0.58, 0.20, 0.76, 0.80),
        },
        ShellGlyph.Next => new[]
        {
            new[] { (0.18, 0.20), (0.62, 0.50), (0.18, 0.80) },
            Rect(0.66, 0.20, 0.80, 0.80),
        },
        ShellGlyph.Previous => new[]
        {
            new[] { (0.82, 0.20), (0.38, 0.50), (0.82, 0.80) },
            Rect(0.20, 0.20, 0.34, 0.80),
        },
        _ => Array.Empty<(double, double)[]>(),
    };

    private static (double X, double Y)[] Rect(double x0, double y0, double x1, double y1) =>
        new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };

    // Even-odd ray cast.
    private static bool Contains((double X, double Y)[] poly, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }
}
