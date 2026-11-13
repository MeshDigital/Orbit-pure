using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Shared tap-tempo: computes a BPM from the running average interval between taps. Taps older than
/// three seconds are discarded (a pause resets the run), matching the Workstation's original logic.
/// </summary>
public sealed class TapTempoTracker
{
    private readonly List<DateTime> _taps = new();
    private readonly TimeSpan _resetWindow = TimeSpan.FromSeconds(3);

    /// <summary>Registers a tap and returns the current tapped BPM, or null with fewer than 2 taps.</summary>
    public double? Tap(DateTime now)
    {
        _taps.RemoveAll(t => (now - t) > _resetWindow);
        _taps.Add(now);
        if (_taps.Count < 2) return null;

        double totalSeconds = (_taps[^1] - _taps[0]).TotalSeconds;
        double avgInterval = totalSeconds / (_taps.Count - 1);
        return avgInterval > 0 ? 60.0 / avgInterval : null;
    }

    public void Reset() => _taps.Clear();
}

/// <summary>
/// BPM from two marked downbeats (issue #222): mark Bar 1 at <paramref name="barOneSeconds"/>, jump
/// <paramref name="barsBetween"/> bars, mark that bar's 1 at <paramref name="secondBarOneSeconds"/>.
/// BPM = 60 × 4·bars / Δt. Returns 0 for an invalid span.
/// </summary>
public static class TwoPointBpm
{
    public static double Calculate(double barOneSeconds, double secondBarOneSeconds, int barsBetween)
    {
        if (barsBetween <= 0) return 0;
        double dt = secondBarOneSeconds - barOneSeconds;
        if (dt <= 0) return 0;
        return 60.0 * 4.0 * barsBetween / dt;
    }
}
