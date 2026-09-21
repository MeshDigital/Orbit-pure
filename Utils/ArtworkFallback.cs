using System;
using Avalonia.Media;

namespace SLSKDONET.Utils;

/// <summary>
/// Generates a deterministic, visually consistent placeholder color + monogram letter for a
/// track/album/playlist that has no real artwork, so a missing-art tile reads as a distinct,
/// recognizable swatch instead of the same flat gray square repeated for every unanalyzed row.
/// Same seed always produces the same color — a given track's tile stays stable across
/// sessions and re-renders rather than flickering between colors.
///
/// Supersedes the ad hoc raw-RGB-from-hash approach in AlbumNode.GenerateColorFromHash, which
/// could land on muddy or washed-out combinations depending on which hash bits came out. This
/// hashes into a HUE only and fixes saturation/lightness, so every generated color reads as
/// equally vibrant against this app's dark theme regardless of which hue comes out.
/// </summary>
public static class ArtworkFallback
{
    private const double Saturation = 0.55;
    private const double Lightness = 0.42;

    /// <summary>Deterministic placeholder color for <paramref name="seed"/> (e.g. "Artist - Title").</summary>
    public static Color GetColor(string? seed)
    {
        var text = string.IsNullOrWhiteSpace(seed) ? "?" : seed;

        int hash = 17;
        unchecked
        {
            foreach (char c in text) hash = hash * 31 + c;
        }

        double hue = (hash & 0x7FFFFFFF) % 360;
        return HslToRgb(hue, Saturation, Lightness);
    }

    /// <summary>Same as <see cref="GetColor"/>, wrapped as a brush for direct XAML binding.</summary>
    public static IBrush GetBrush(string? seed) => new SolidColorBrush(GetColor(seed));

    /// <summary>First letter/digit of the seed, uppercased — "?" when there's nothing usable.</summary>
    public static string GetLetter(string? seed)
    {
        if (string.IsNullOrWhiteSpace(seed)) return "?";
        foreach (var c in seed)
        {
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        }
        return "?";
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = l - c / 2;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
