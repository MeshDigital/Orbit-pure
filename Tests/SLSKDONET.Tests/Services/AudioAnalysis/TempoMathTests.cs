using System;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

public class TapTempoTrackerTests
{
    [Fact]
    public void TwoTaps_ComputesBpmFromAverageInterval()
    {
        var tracker = new TapTempoTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Null(tracker.Tap(t0));
        double? bpm = tracker.Tap(t0.AddSeconds(0.5)); // 120 BPM
        Assert.NotNull(bpm);
        Assert.Equal(120.0, bpm!.Value, 1);
    }

    [Fact]
    public void SingleTap_ReturnsNull()
    {
        var tracker = new TapTempoTracker();
        Assert.Null(tracker.Tap(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void TapsOlderThanThreeSeconds_AreDiscarded()
    {
        var tracker = new TapTempoTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        tracker.Tap(t0);
        tracker.Tap(t0.AddSeconds(0.5));
        Assert.Equal(120.0, tracker.Tap(t0.AddSeconds(1.0))!.Value, 1);

        // A tap more than 3 s later discards the old run and starts fresh.
        Assert.Null(tracker.Tap(t0.AddSeconds(5.0)));
    }

    [Fact]
    public void Reset_ClearsTheTapRun()
    {
        var tracker = new TapTempoTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        tracker.Tap(t0);
        tracker.Reset();
        Assert.Null(tracker.Tap(t0.AddSeconds(0.5)));
    }
}

public class TwoPointBpmTests
{
    [Fact]
    public void TwoDownbeats_ComputesTheExactBpm()
    {
        // A synthetic 174.00 click track: mark Bar 1 at 0.5 s, jump 16 bars, mark that bar's 1.
        double barOne = 0.5;
        double dt = 16 * 4 * (60.0 / 174.0);
        double secondBarOne = barOne + dt;

        double bpm = TwoPointBpm.Calculate(barOne, secondBarOne, barsBetween: 16);

        Assert.Equal(174.0, bpm, 2);
    }

    [Fact]
    public void PickupBeforeBarOne_StillMeasuresTheSpan()
    {
        // Bar 1 can be anywhere; the math only depends on the span between the two marked beats.
        double barOne = 2.3;
        double dt = 8 * 4 * (60.0 / 128.0);
        double secondBarOne = barOne + dt;

        Assert.Equal(128.0, TwoPointBpm.Calculate(barOne, secondBarOne, barsBetween: 8), 2);
    }

    [Theory]
    [InlineData(0, 10.0, 0)]      // zero bars
    [InlineData(10.0, 10.0, 4)]   // zero span
    [InlineData(20.0, 10.0, 4)]   // negative span
    public void InvalidSpan_ReturnsZero(double barOne, double secondBarOne, int bars)
    {
        Assert.Equal(0, TwoPointBpm.Calculate(barOne, secondBarOne, bars));
    }
}
