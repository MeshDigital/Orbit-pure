using System;
using System.Diagnostics;

namespace SLSKDONET.Services;

/// <summary>
/// Forwards import progress at most once per interval (phase changes and the final report are
/// forced through), so a 50,000-file scan doesn't post 50,000 updates to the UI thread. The
/// snapshot factory is only invoked when a report is actually sent. Thread-safe.
/// </summary>
internal sealed class ThrottledProgressReporter
{
    private readonly IProgress<LibraryImportProgress>? _target;
    private readonly long _intervalTicks;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private long _lastReportTicks = long.MinValue;

    public ThrottledProgressReporter(IProgress<LibraryImportProgress>? target, TimeSpan interval)
    {
        _target = target;
        _intervalTicks = (long)(Math.Max(0, interval.TotalSeconds) * Stopwatch.Frequency);
    }

    /// <summary>How many reports were forwarded (tests use this to prove throttling).</summary>
    public int ReportsSent { get; private set; }

    public void Report(Func<LibraryImportProgress> snapshot, bool force = false)
    {
        if (_target == null) return;
        LibraryImportProgress value;
        lock (_lock)
        {
            var now = _clock.ElapsedTicks;
            if (!force && _lastReportTicks != long.MinValue && now - _lastReportTicks < _intervalTicks) return;
            _lastReportTicks = now;
            ReportsSent++;
            value = snapshot();
        }
        _target.Report(value);
    }
}
