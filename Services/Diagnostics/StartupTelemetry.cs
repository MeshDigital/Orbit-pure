using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SLSKDONET.Services.Diagnostics;

/// <summary>
/// Lightweight process-lifetime startup stopwatch for PF0 (#606).
/// Emits structured Serilog lines (<c>[StartupMark]</c>) with elapsed milliseconds from process start.
/// Log-only; zero UI overhead.
/// </summary>
public static class StartupTelemetry
{
    private static long _startTimestamp = Stopwatch.GetTimestamp();
    private static readonly ConcurrentDictionary<string, double> RecordedMarks = new(StringComparer.Ordinal);

    /// <summary>Anchors elapsed-time measurements to process entry (called at top of <c>Program.Main</c>).</summary>
    public static void Start()
    {
        _startTimestamp = Stopwatch.GetTimestamp();
        RecordedMarks.Clear();
    }

    /// <summary>Elapsed milliseconds since <see cref="Start"/> (or static class initialization).</summary>
    public static double ElapsedMs =>
        (Stopwatch.GetTimestamp() - _startTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Logs a startup milestone once per process lifetime. Subsequent calls for the same
    /// <paramref name="stage"/> are no-ops and return false.
    /// </summary>
    public static bool MarkOnce(string stage, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(stage)) return false;

        var elapsed = ElapsedMs;
        if (!RecordedMarks.TryAdd(stage, elapsed))
            return false;

        if (string.IsNullOrWhiteSpace(detail))
        {
            Serilog.Log.Information("[StartupMark] {Stage} +{ElapsedMs:F1} ms", stage, elapsed);
        }
        else
        {
            Serilog.Log.Information("[StartupMark] {Stage} +{ElapsedMs:F1} ms ({Detail})", stage, elapsed, detail);
        }

        return true;
    }

    /// <summary>Returns the recorded timestamp in ms for a stage, or null if not yet marked.</summary>
    public static double? GetMarkMs(string stage) =>
        RecordedMarks.TryGetValue(stage, out var ms) ? ms : null;
}
