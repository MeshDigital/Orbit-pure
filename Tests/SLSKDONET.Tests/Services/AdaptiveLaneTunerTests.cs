using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class AdaptiveLaneTunerTests
{
    [Fact]
    public void ComputeNextLaneLimit_HealthySignal_StepsUpTowardMax()
    {
        var config = new AppConfig
        {
            EnableAdaptiveLanes = true,
            MaxDiscoveryLanes = 4,
            MinAdaptiveSearchLanes = 2,
            MaxAdaptiveSearchLanes = 6
        };

        var signal = new NetworkHealthSignal(
            IsConnected: true,
            ConnectionState: "Connected, LoggedIn",
            LastFailureStatus: ConnectionFailureStatus.Healthy,
            LastFailureMessage: null,
            RecentTimeoutCount: 0,
            RecentConnectionRefusedCount: 0,
            ZeroResultSearchCount: 1,
            TotalSearchCount: 10,
            ZeroResultPercentage: 10,
            SuccessfulSearchCount: 9,
            LastSuccessfulSearch: DateTime.UtcNow,
            TimeSinceLastSuccess: TimeSpan.FromSeconds(5),
            ThrottleStatus: ThrottleStatus.None,
            BanStatus: BanStatus.None,
            IsHealthy: true,
            DiagnosticMessage: "healthy");

        var counters = new NetworkReliabilityCounters(0, 0, 0, 0, 0, 0, 0, 0);

        var next = AdaptiveLaneTuner.ComputeNextLaneLimit(config, signal, counters, currentLaneLimit: 4);
        Assert.Equal(5, next);
    }

    [Fact]
    public void ComputeNextLaneLimit_ThrottledSignal_StepsDown()
    {
        var config = new AppConfig
        {
            EnableAdaptiveLanes = true,
            MaxDiscoveryLanes = 5,
            MinAdaptiveSearchLanes = 2,
            MaxAdaptiveSearchLanes = 6
        };

        var signal = new NetworkHealthSignal(
            IsConnected: true,
            ConnectionState: "Connected, LoggedIn",
            LastFailureStatus: ConnectionFailureStatus.Healthy,
            LastFailureMessage: null,
            RecentTimeoutCount: 2,
            RecentConnectionRefusedCount: 0,
            ZeroResultSearchCount: 9,
            TotalSearchCount: 10,
            ZeroResultPercentage: 90,
            SuccessfulSearchCount: 1,
            LastSuccessfulSearch: DateTime.UtcNow.AddMinutes(-5),
            TimeSinceLastSuccess: TimeSpan.FromMinutes(5),
            ThrottleStatus: ThrottleStatus.Confirmed,
            BanStatus: BanStatus.None,
            IsHealthy: false,
            DiagnosticMessage: "throttled");

        var counters = new NetworkReliabilityCounters(0, 0, 0, 0, 0, 0, 0, 0);

        var next = AdaptiveLaneTuner.ComputeNextLaneLimit(config, signal, counters, currentLaneLimit: 5);
        Assert.Equal(4, next);
    }

    [Fact]
    public void ComputeNextLaneLimit_KickOrBan_DropsTowardMinimum()
    {
        var config = new AppConfig
        {
            EnableAdaptiveLanes = true,
            MaxDiscoveryLanes = 5,
            MinAdaptiveSearchLanes = 2,
            MaxAdaptiveSearchLanes = 6
        };

        var signal = new NetworkHealthSignal(
            IsConnected: false,
            ConnectionState: "Disconnected",
            LastFailureStatus: ConnectionFailureStatus.ConnectionRefused,
            LastFailureMessage: "Kicked",
            RecentTimeoutCount: 0,
            RecentConnectionRefusedCount: 2,
            ZeroResultSearchCount: 0,
            TotalSearchCount: 0,
            ZeroResultPercentage: 0,
            SuccessfulSearchCount: 0,
            LastSuccessfulSearch: null,
            TimeSinceLastSuccess: null,
            ThrottleStatus: ThrottleStatus.None,
            BanStatus: BanStatus.Suspected,
            IsHealthy: false,
            DiagnosticMessage: "suspected ban");

        var counters = new NetworkReliabilityCounters(1, 0, 0, 0, 0, 0, 0, 0);

        var next = AdaptiveLaneTuner.ComputeNextLaneLimit(config, signal, counters, currentLaneLimit: 4);
        Assert.Equal(3, next);
    }
}
