using System;

namespace SLSKDONET.Services.Timeline;

/// <summary>
/// Grid quantisation resolution expressed as note subdivisions.
/// </summary>
public enum GridResolution
{
    /// <summary>Quarter note — one beat.</summary>
    Quarter = 1,

    /// <summary>Eighth note — half a beat.</summary>
    Eighth = 2,

    /// <summary>Sixteenth note — a quarter beat.</summary>
    Sixteenth = 4
}

/// <summary>
/// Stateless service for beat-grid calculations and clip-edge snapping.
/// All methods are pure functions with no side effects.
/// </summary>
public static class BeatGridService
{
    // ── Core unit conversions ─────────────────────────────────────────────

    /// <summary>Convert a beat number to a timeline position in seconds.</summary>
    public static double BeatToSeconds(double beat, double bpm) => beat * (60.0 / bpm);

    /// <summary>Convert a timeline position in seconds to a beat number.</summary>
    public static double SecondsToBeat(double seconds, double bpm) => seconds * (bpm / 60.0);

    /// <summary>
    /// Returns the beat subdivision width for <paramref name="resolution"/>
    /// (e.g. Eighth = 0.5 beats, Sixteenth = 0.25 beats).
    /// </summary>
    public static double SubdivisionBeats(GridResolution resolution) =>
        1.0 / (int)resolution;

    // ── Snap ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Snaps <paramref name="beatPosition"/> to the nearest grid line
    /// defined by <paramref name="resolution"/>.
    /// </summary>
    public static double SnapToGrid(double beatPosition, GridResolution resolution)
    {
        double sub = SubdivisionBeats(resolution);
        return Math.Round(beatPosition / sub) * sub;
    }

    /// <summary>
    /// Snaps <paramref name="beatPosition"/> to the grid line at or before
    /// (floor) rather than the nearest line.
    /// </summary>
    public static double SnapToGridFloor(double beatPosition, GridResolution resolution)
    {
        double sub = SubdivisionBeats(resolution);
        return Math.Floor(beatPosition / sub) * sub;
    }

    /// <summary>
    /// Snaps <paramref name="beatPosition"/> to the grid line at or after
    /// (ceiling).
    /// </summary>
    public static double SnapToGridCeiling(double beatPosition, GridResolution resolution)
    {
        double sub = SubdivisionBeats(resolution);
        return Math.Ceiling(beatPosition / sub) * sub;
    }

    // ── Grid line generation ──────────────────────────────────────────────

    /// <summary>
    /// Computes the absolute time (seconds) of every beat downbeat within
    /// <paramref name="durationSeconds"/>, starting from
    /// <paramref name="downbeatOffsetSeconds"/> (the detected first downbeat).
    /// </summary>
    public static double[] ComputeBeatGrid(
        double bpm,
        double durationSeconds,
        double downbeatOffsetSeconds = 0.0)
    {
        if (bpm <= 0 || durationSeconds <= 0) return Array.Empty<double>();

        double beatDuration = 60.0 / bpm;
        int count = (int)Math.Floor((durationSeconds - downbeatOffsetSeconds) / beatDuration) + 1;
        if (count <= 0) return Array.Empty<double>();

        var beats = new double[count];
        for (int i = 0; i < count; i++)
            beats[i] = downbeatOffsetSeconds + i * beatDuration;

        return beats;
    }

    /// <summary>
    /// Computes the absolute time (seconds) of every bar start (beat 1)
    /// within <paramref name="durationSeconds"/>.
    /// </summary>
    public static double[] ComputeBarGrid(
        double bpm,
        int beatsPerBar,
        double durationSeconds,
        double downbeatOffsetSeconds = 0.0)
    {
        if (bpm <= 0 || beatsPerBar <= 0 || durationSeconds <= 0) return Array.Empty<double>();

        double barDuration = beatsPerBar * (60.0 / bpm);
        int count = (int)Math.Floor((durationSeconds - downbeatOffsetSeconds) / barDuration) + 1;
        if (count <= 0) return Array.Empty<double>();

        var bars = new double[count];
        for (int i = 0; i < count; i++)
            bars[i] = downbeatOffsetSeconds + i * barDuration;

        return bars;
    }

    /// <summary>
    /// Computes subdivision grid lines (subdivisions within each beat) as
    /// absolute times in seconds, for use in fine-grained quantisation.
    /// </summary>
    public static double[] ComputeSubdivisionGrid(
        double bpm,
        GridResolution resolution,
        double durationSeconds,
        double downbeatOffsetSeconds = 0.0)
    {
        if (bpm <= 0 || durationSeconds <= 0) return Array.Empty<double>();

        double subDuration = SubdivisionBeats(resolution) * (60.0 / bpm);
        int count = (int)Math.Floor((durationSeconds - downbeatOffsetSeconds) / subDuration) + 1;
        if (count <= 0) return Array.Empty<double>();

        var lines = new double[count];
        for (int i = 0; i < count; i++)
            lines[i] = downbeatOffsetSeconds + i * subDuration;

        return lines;
    }

    // ── Beat position helpers ─────────────────────────────────────────────

    /// <summary>
    /// Returns the bar number (0-based) that <paramref name="beat"/> falls in.
    /// </summary>
    public static int BeatToBarIndex(double beat, int beatsPerBar) =>
        (int)Math.Floor(beat / beatsPerBar);

    /// <summary>
    /// Returns the beat-within-bar (0-based) for <paramref name="beat"/>.
    /// </summary>
    public static double BeatWithinBar(double beat, int beatsPerBar) =>
        beat % beatsPerBar;
}
