using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Computes adaptive discovery/search lane limits from live network telemetry.
/// </summary>
public static class AdaptiveLaneTuner
{
    public static int ComputeNextLaneLimit(
        AppConfig config,
        NetworkHealthSignal health,
        NetworkReliabilityCounters counters,
        int currentLaneLimit)
    {
        var minLane = Math.Clamp(config.MinAdaptiveSearchLanes, 1, 8);
        var maxLane = Math.Clamp(config.MaxAdaptiveSearchLanes, minLane, 8);
        var baseline = Math.Clamp(config.MaxDiscoveryLanes, minLane, maxLane);

        if (!config.EnableAdaptiveLanes)
        {
            return baseline;
        }

        var target = baseline;

        if (health.BanStatus != BanStatus.None || !health.IsConnected || counters.KickedEventCount > 0)
        {
            target = minLane;
        }
        else if (health.ThrottleStatus == ThrottleStatus.Confirmed || health.ZeroResultPercentage >= 90)
        {
            target = minLane;
        }
        else if (health.ThrottleStatus == ThrottleStatus.Suspected || health.RecentTimeoutCount >= 3)
        {
            target = Math.Max(minLane, baseline - 2);
        }
        else if (health.IsDegraded || health.RecentTimeoutCount >= 1)
        {
            target = Math.Max(minLane, baseline - 1);
        }
        else if (health.IsHealthy && health.SuccessfulSearchCount >= 5 && health.ZeroResultPercentage <= 40)
        {
            target = maxLane;
        }

        var clampedCurrent = Math.Clamp(currentLaneLimit, minLane, maxLane);
        if (clampedCurrent < target)
        {
            return Math.Min(maxLane, clampedCurrent + 1);
        }

        if (clampedCurrent > target)
        {
            return Math.Max(minLane, clampedCurrent - 1);
        }

        return clampedCurrent;
    }
}
