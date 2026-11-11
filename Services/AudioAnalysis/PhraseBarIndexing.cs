using System;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Bar ↔ seconds conversion for the bar-indexed phrase map (issue #225). Bars are counted from
/// Bar 1 (<c>downbeat</c>); a bar is four beats, so <c>barSeconds = 240 / bpm</c>. Phrases are stored
/// in bars and their seconds values are derived from the grid so a grid edit moves them with the bars.
/// </summary>
public static class PhraseBarIndexing
{
    public static double BarSeconds(double bpm) => bpm > 0 ? 240.0 / bpm : 0.0;

    /// <summary>The 0-based bar index of a timestamp, relative to Bar 1.</summary>
    public static int SecondsToBar(double seconds, double bpm, double downbeat)
    {
        double bar = BarSeconds(bpm);
        return bar > 0 ? (int)Math.Round((seconds - downbeat) / bar) : 0;
    }

    /// <summary>The bar count of a seconds-duration span.</summary>
    public static int DurationToBars(double durationSeconds, double bpm)
    {
        double bar = BarSeconds(bpm);
        return bar > 0 ? (int)Math.Round(durationSeconds / bar) : 0;
    }

    /// <summary>The timestamp (seconds) of a 0-based bar index, relative to Bar 1.</summary>
    public static double BarToSeconds(int bar, double bpm, double downbeat) =>
        downbeat + bar * BarSeconds(bpm);
}
