using System;
using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Logs a warning whenever the UI thread was blocked long enough for a user to notice. A 100ms
/// DispatcherTimer tick can only run when the UI thread is free, so a late tick means everything
/// queued ahead of it (a synchronous DB query, a big layout pass, ...) held the thread for that long.
/// Pair the timestamps with the surrounding log lines to see what caused the freeze.
/// </summary>
public sealed class UiStallWatchdog
{
    private const int TickMs = 100;
    internal const int ReportThresholdMs = 250;
    /// <summary>
    /// A gap this large cannot be a UI freeze (a synchronous call that blocked the UI thread for
    /// minutes would trip the OS "not responding" path long before this); it is a sleep/resume or
    /// a paused debugger. Reported separately rather than as a stall (#336).
    /// </summary>
    internal const long ResumeGapThresholdMs = 5 * 60 * 1000;

    private readonly ILogger<UiStallWatchdog> _logger;
    private readonly Stopwatch _sinceLastTick = new();
    private DispatcherTimer? _timer;

    public UiStallWatchdog(ILogger<UiStallWatchdog> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(TickMs), DispatcherPriority.Background, OnTick);
        _sinceLastTick.Restart();
        _timer.Start();
    }

    /// <summary>How a late tick should be reported.</summary>
    internal enum GapKind { BelowThreshold, Stall, ResumeGap }

    internal static GapKind Classify(long blockedMs) =>
        blockedMs < ReportThresholdMs ? GapKind.BelowThreshold
        : blockedMs >= ResumeGapThresholdMs ? GapKind.ResumeGap
        : GapKind.Stall;

    private void OnTick(object? sender, EventArgs e)
    {
        var blockedMs = _sinceLastTick.ElapsedMilliseconds - TickMs;
        _sinceLastTick.Restart();

        switch (Classify(blockedMs))
        {
            case GapKind.Stall:
                _logger.LogWarning("[UI-STALL] UI thread was blocked for ~{Ms}ms", blockedMs);
                break;
            case GapKind.ResumeGap:
                _logger.LogInformation("[UI-STALL] Skipped a wall-clock gap of ~{Ms}ms — likely a sleep/resume, not a UI freeze", blockedMs);
                break;
        }
    }
}
