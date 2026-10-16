using System;
using System.Collections.Generic;
using System.Globalization;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Beat-grid and viewport maths shared by <see cref="CueForgeViewModel"/> and the Cue Forge
/// waveform controls, so snapping, bar numbers and zoom behave identically everywhere.
///
/// The grid is anchored at the track's downbeat offset and uses the precise (fractional) BPM.
/// Before this the waveform drew its grid from t = 0 with the rounded integer BPM, so on most
/// tracks the drawn bar lines drifted away from the music and snapping landed off-beat.
///
/// Viewport convention: <c>scroll</c> is the fraction of the track at the left edge (0..1) and
/// <c>zoom</c> is how many times the full track the view is magnified, so the visible window is
/// [scroll, scroll + 1/zoom].
/// </summary>
public static class CueForgeGrid
{
    public const double MinZoom = 1.0;

    /// <summary>Snap resolutions offered in the toolbar, with their length in beats.</summary>
    public static IReadOnlyList<string> SnapResolutions { get; } = new[] { "Beat", "Bar", "4 bars", "8 bars" };

    public static int ResolutionBeats(string? resolution) => resolution switch
    {
        "Beat" => 1,
        "Bar" => 4,
        "4 bars" => 16,
        "8 bars" => 32,
        _ => 1,
    };

    /// <summary>Nearest grid line to <paramref name="seconds"/>, <paramref name="beats"/> beats apart.
    /// Returns the input unchanged when there is no usable tempo.</summary>
    public static double Snap(double seconds, double bpm, double offset, int beats)
    {
        if (bpm <= 0 || beats <= 0 || double.IsNaN(seconds)) return seconds;
        double step = 60.0 / bpm * beats;
        double snapped = offset + Math.Round((seconds - offset) / step) * step;
        return snapped < 0 ? snapped + step : snapped;
    }

    /// <summary>1-based bar and beat at <paramref name="seconds"/> (bar 1 starts at the downbeat).</summary>
    public static (int Bar, int Beat) BarBeat(double seconds, double bpm, double offset)
    {
        if (bpm <= 0) return (1, 1);
        double beats = Math.Floor((seconds - offset) / (60.0 / bpm) + 1e-6);
        int bar = (int)Math.Floor(beats / 4.0);
        int beat = (int)(beats - bar * 4.0);
        return (bar + 1, beat + 1);
    }

    /// <summary>"mm:ss.ff" (invariant), e.g. 01:23.45.</summary>
    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
    }

    /// <summary>"Bar 12.3" readout for a position.</summary>
    public static string FormatBarBeat(double seconds, double bpm, double offset)
    {
        var (bar, beat) = BarBeat(seconds, bpm, offset);
        return bpm > 0 ? $"Bar {bar}.{beat}" : "";
    }

    public static double MaxScroll(double zoom) => Math.Max(0.0, 1.0 - 1.0 / Math.Max(MinZoom, zoom));

    public static double ClampScroll(double scroll, double zoom) => Math.Clamp(scroll, 0.0, MaxScroll(zoom));

    /// <summary>Scroll that keeps the track position under <paramref name="anchor"/> (0..1 across the
    /// view) fixed while zoom changes from <paramref name="oldZoom"/> to <paramref name="newZoom"/>.</summary>
    public static double ZoomAround(double scroll, double oldZoom, double newZoom, double anchor)
    {
        oldZoom = Math.Max(MinZoom, oldZoom);
        newZoom = Math.Max(MinZoom, newZoom);
        anchor = Math.Clamp(anchor, 0.0, 1.0);
        double trackFractionAtAnchor = scroll + anchor / oldZoom;
        return ClampScroll(trackFractionAtAnchor - anchor / newZoom, newZoom);
    }

    /// <summary>Scroll that keeps the playhead visible while following playback. Returns the
    /// current scroll when the playhead is comfortably inside the view; otherwise pages so the
    /// playhead sits at 15% of the window (DJ-software style "page follow").</summary>
    public static double FollowScroll(double playheadFraction, double scroll, double zoom)
    {
        zoom = Math.Max(MinZoom, zoom);
        if (zoom <= MinZoom) return 0.0;
        double window = 1.0 / zoom;
        double rel = (playheadFraction - scroll) / window;
        if (rel >= 0.0 && rel <= 0.9) return scroll;
        return ClampScroll(playheadFraction - window * 0.15, zoom);
    }

    /// <summary>Scroll that centres the view on a track fraction.</summary>
    public static double CenterOn(double fraction, double zoom) =>
        ClampScroll(fraction - 0.5 / Math.Max(MinZoom, zoom), zoom);
}
