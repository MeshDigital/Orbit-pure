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
    private const int ReportThresholdMs = 250;

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

    private void OnTick(object? sender, EventArgs e)
    {
        var blockedMs = _sinceLastTick.ElapsedMilliseconds - TickMs;
        _sinceLastTick.Restart();
        if (blockedMs >= ReportThresholdMs)
            _logger.LogWarning("[UI-STALL] UI thread was blocked for ~{Ms}ms", blockedMs);
    }
}
