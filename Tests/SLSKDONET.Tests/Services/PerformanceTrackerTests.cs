using System.Linq;
using System.Threading;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class PerformanceTrackerTests
{
    [Fact]
    public void Measure_RecordsDurationOnDispose()
    {
        var tracker = new PerformanceTracker();

        using (tracker.Measure("Test.Scope"))
        {
            Thread.Sleep(5);
        }

        // Record() posts to the Avalonia UI-thread dispatcher, which doesn't run in a plain xunit
        // host — GetLatest reads the underlying concurrent dictionary directly, set synchronously
        // before that dispatcher hop, so it's already populated by the time Dispose() returns.
        var latest = tracker.GetLatest("Test.Scope");
        Assert.NotNull(latest);
        Assert.True(latest.Value.TotalMilliseconds >= 0);
    }

    [Fact]
    public void Measure_DisposingTwice_RecordsOnlyOnce()
    {
        var tracker = new PerformanceTracker();
        var scope = tracker.Measure("Test.Idempotent");

        scope.Dispose();
        var firstLatest = tracker.GetLatest("Test.Idempotent");
        scope.Dispose();
        var secondLatest = tracker.GetLatest("Test.Idempotent");

        Assert.Equal(firstLatest, secondLatest);
    }

    [Fact]
    public void GetLatest_UnknownLabel_ReturnsNull()
    {
        var tracker = new PerformanceTracker();
        Assert.Null(tracker.GetLatest("Never.Recorded"));
    }

    [Fact]
    public void Record_MultipleLabels_TracksEachIndependently()
    {
        var tracker = new PerformanceTracker();

        tracker.Record("A", System.TimeSpan.FromMilliseconds(10));
        tracker.Record("B", System.TimeSpan.FromMilliseconds(20));

        Assert.Equal(10, tracker.GetLatest("A")!.Value.TotalMilliseconds);
        Assert.Equal(20, tracker.GetLatest("B")!.Value.TotalMilliseconds);
    }
}
