using System;

namespace SLSKDONET.Models;

/// <summary>Derived musical timing for the first detected structural drop.</summary>
public static class IntroTiming
{
    /// <summary>
    /// Converts seconds from track start to the first drop into approximate 4/4 bars.
    /// Returns null when either input is missing or invalid. Zero seconds is valid and yields zero bars.
    /// </summary>
    public static double? CalculateBars(double? timeTillDropSeconds, double? bpm)
    {
        if (timeTillDropSeconds is not { } seconds ||
            !double.IsFinite(seconds) || seconds < 0 ||
            bpm is not { } tempo ||
            !double.IsFinite(tempo) || tempo <= 0)
        {
            return null;
        }

        return seconds / (240.0 / tempo);
    }
}
