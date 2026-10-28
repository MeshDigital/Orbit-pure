using System;
using System.Collections.Generic;
using Avalonia;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>Places cue names in separate lanes without changing marker or hit-test positions.</summary>
internal static class CueLabelLayout
{
    private const double LaneHeight = 18;
    private const int MaxLanes = 3;

    public static bool TryPlace(
        double preferredX,
        double width,
        double height,
        double viewportWidth,
        double top,
        double bottom,
        IReadOnlyList<Rect> occupied,
        out Rect placement)
    {
        placement = default;
        if (!double.IsFinite(preferredX) || !double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 0 || height <= 0 || viewportWidth < width + 4 || bottom - top < height)
            return false;

        double x = Math.Clamp(preferredX - 2, 0, Math.Max(0, viewportWidth - width - 4));
        int lanes = Math.Min(MaxLanes, Math.Max(1, (int)((bottom - top) / LaneHeight)));
        for (int lane = 0; lane < lanes; lane++)
        {
            var candidate = new Rect(x, top + lane * LaneHeight, width + 4, height);
            bool overlaps = false;
            foreach (var existing in occupied)
            {
                if (!candidate.Intersects(existing)) continue;
                overlaps = true;
                break;
            }
            if (overlaps) continue;
            placement = candidate;
            return true;
        }
        return false;
    }
}
