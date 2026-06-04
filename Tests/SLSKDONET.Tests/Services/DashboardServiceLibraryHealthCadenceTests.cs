using System;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class DashboardServiceLibraryHealthCadenceTests
{
    [Fact]
    public void ShouldRecalculateLibraryHealth_ReturnsTrue_WhenLastScanIsMissing()
    {
        var shouldRecalculate = DashboardService.ShouldRecalculateLibraryHealth(
            now: DateTime.Now,
            lastScanDate: null,
            pendingUpdates: 0,
            maxAge: TimeSpan.FromMinutes(5));

        Assert.True(shouldRecalculate);
    }

    [Fact]
    public void ShouldRecalculateLibraryHealth_ReturnsTrue_WhenPendingUpdatesReachThreshold()
    {
        var now = DateTime.Now;
        var recentScan = now.AddMinutes(-1);

        var shouldRecalculate = DashboardService.ShouldRecalculateLibraryHealth(
            now: now,
            lastScanDate: recentScan,
            pendingUpdates: DashboardService.DefaultPendingUpdatesRefreshThreshold,
            maxAge: TimeSpan.FromMinutes(5));

        Assert.True(shouldRecalculate);
    }

    [Fact]
    public void ShouldRecalculateLibraryHealth_ReturnsTrue_WhenScanAgeExceedsMaxAge()
    {
        var now = DateTime.Now;
        var staleScan = now.AddMinutes(-6);

        var shouldRecalculate = DashboardService.ShouldRecalculateLibraryHealth(
            now: now,
            lastScanDate: staleScan,
            pendingUpdates: 0,
            maxAge: TimeSpan.FromMinutes(5));

        Assert.True(shouldRecalculate);
    }

    [Fact]
    public void ShouldRecalculateLibraryHealth_ReturnsFalse_WhenScanIsFreshAndPendingUpdatesLow()
    {
        var now = DateTime.Now;
        var recentScan = now.AddMinutes(-1);

        var shouldRecalculate = DashboardService.ShouldRecalculateLibraryHealth(
            now: now,
            lastScanDate: recentScan,
            pendingUpdates: DashboardService.DefaultPendingUpdatesRefreshThreshold - 1,
            maxAge: TimeSpan.FromMinutes(5));

        Assert.False(shouldRecalculate);
    }

    [Fact]
    public void ShouldRecalculateLibraryHealth_ReturnsTrue_WhenMaxAgeIsNonPositive()
    {
        var now = DateTime.Now;
        var recentScan = now.AddSeconds(-5);

        var shouldRecalculate = DashboardService.ShouldRecalculateLibraryHealth(
            now: now,
            lastScanDate: recentScan,
            pendingUpdates: 0,
            maxAge: TimeSpan.Zero);

        Assert.True(shouldRecalculate);
    }
}
