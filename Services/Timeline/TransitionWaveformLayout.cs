using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services.Timeline;

/// <summary>One lane's data for the transition overlap canvas.</summary>
public sealed record TrackWaveformData(
    float[] RmsProfile,
    double DurationSeconds,
    double Bpm,
    IReadOnlyList<double> BeatPositionsSeconds,
    double MixAnchorSeconds);

/// <summary>The outgoing + incoming tracks plus the mix window length (in bars).</summary>
public sealed record TransitionWaveformData(
    TrackWaveformData Outgoing,
    TrackWaveformData Incoming,
    int BarsBeforeMix);

/// <summary>
/// Slice K6: pure transition-overlap geometry. Computes the mix window (from
/// <see cref="TransitionWaveformData.BarsBeforeMix"/> and BPM) and the time→pixel / beat-grid
/// mapping. The Avalonia <c>TransitionWaveformCanvas</c> only paints this geometry.
/// </summary>
public static class TransitionWaveformLayout
{
    /// <summary>Seconds per bar (4 beats) at the given BPM; 0 for unknown BPM.</summary>
    public static double BarSeconds(double bpm) => bpm > 0 ? (4.0 * 60.0) / bpm : 0.0;

    /// <summary>The mix window length in seconds.</summary>
    public static double MixWindowSeconds(double bpm, int barsBeforeMix)
        => Math.Max(0, barsBeforeMix) * BarSeconds(bpm);

    /// <summary>Maps a track time (seconds) to an x pixel within a lane of the given width.</summary>
    public static double TimeToX(double timeSeconds, double durationSeconds, double width)
        => durationSeconds > 0 ? Math.Clamp(timeSeconds / durationSeconds, 0.0, 1.0) * width : 0.0;

    /// <summary>The mix window's x-range for one lane. <paramref name="anchorIsEnd"/> = the window
    /// ends at the anchor (outgoing track's mix-out); false = it starts at the anchor (incoming mix-in).</summary>
    public static (double X1, double X2) MixWindowBox(
        double mixAnchorSeconds,
        double bpm,
        int barsBeforeMix,
        double durationSeconds,
        double width,
        bool anchorIsEnd)
    {
        var window = MixWindowSeconds(bpm, barsBeforeMix);
        var start = anchorIsEnd ? mixAnchorSeconds - window : mixAnchorSeconds;
        var end = anchorIsEnd ? mixAnchorSeconds : mixAnchorSeconds + window;
        return (TimeToX(start, durationSeconds, width), TimeToX(end, durationSeconds, width));
    }

    /// <summary>Beat tick x-positions for a lane (every 4th beat is a bar/downbeat).</summary>
    public static IReadOnlyList<double> BeatTickXs(
        IReadOnlyList<double> beatPositionsSeconds,
        double durationSeconds,
        double width)
        => beatPositionsSeconds is null
            ? Array.Empty<double>()
            : beatPositionsSeconds.Select(t => TimeToX(t, durationSeconds, width)).ToList();
}
