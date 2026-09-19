using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;

namespace SLSKDONET.Services;

/// <summary>One recorded timing — a page navigation dispatch, a ViewModel's async load, etc.</summary>
public sealed record PerfTiming(string Label, TimeSpan Duration, DateTime AtUtc);

/// <summary>
/// Lightweight, opt-in performance instrumentation for the live perf overlay (Ctrl+Shift+P).
/// Wrap anything worth timing with <c>using var _ = _perfTracker.Measure("Some.Label");</c> — the
/// duration is recorded when the scope disposes. Kept intentionally simple (no percentiles/buckets):
/// this is a live debugging aid, not a telemetry pipeline, and nothing here is ever sent anywhere.
/// </summary>
public sealed class PerformanceTracker
{
    private const int MaxHistory = 50;

    private readonly ConcurrentDictionary<string, TimeSpan> _latestByLabel = new();

    /// <summary>Most recent timings, newest first — bound directly by the perf overlay.</summary>
    public ObservableCollection<PerfTiming> RecentTimings { get; } = new();

    /// <summary>Last recorded duration per label (e.g. "Nav:Users") — for "how long did the current screen take" lookups.</summary>
    public TimeSpan? GetLatest(string label) => _latestByLabel.TryGetValue(label, out var d) ? d : null;

    public IDisposable Measure(string label) => new Scope(this, label);

    public void Record(string label, TimeSpan duration)
    {
        _latestByLabel[label] = duration;

        // Record() can be called from any thread (a background load task's finally block, etc.),
        // but ObservableCollection isn't thread-safe and the overlay binds to it directly.
        Dispatcher.UIThread.Post(() =>
        {
            RecentTimings.Insert(0, new PerfTiming(label, duration, DateTime.UtcNow));
            while (RecentTimings.Count > MaxHistory)
                RecentTimings.RemoveAt(RecentTimings.Count - 1);
        });
    }

    private sealed class Scope : IDisposable
    {
        private readonly PerformanceTracker _tracker;
        private readonly string _label;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public Scope(PerformanceTracker tracker, string label)
        {
            _tracker = tracker;
            _label = label;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tracker.Record(_label, _stopwatch.Elapsed);
        }
    }
}
