using System;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

public enum SearchPressureLevel
{
    Normal,
    Elevated,
    Critical
}

public sealed record SearchExecutionProfile(
    SearchPressureLevel PressureLevel,
    int EffectiveResponseLimit,
    int EffectiveFileLimit,
    int EffectiveVariationCap,
    int AdditionalThrottleDelayMs,
    int TokenBucketCapacity,
    int TokenRefillIntervalMs);

public static class SearchLoadSheddingPolicy
{
    public static SearchExecutionProfile Compute(AppConfig config, int activeSearchCount)
    {
        var baseResponseLimit = Math.Max(20, config.SearchResponseLimit);
        var baseFileLimit = Math.Max(20, config.SearchFileLimit);
        var baseVariationCap = Math.Max(1, config.MaxSearchVariations);

        if (!config.EnableSearchLoadShedding)
        {
            return new SearchExecutionProfile(
                SearchPressureLevel.Normal,
                baseResponseLimit,
                baseFileLimit,
                baseVariationCap,
                0,
                Math.Max(1, config.SearchTokenBucketCapacity),
                Math.Max(500, config.SearchTokenBucketRefillMs));
        }

        var elevatedThreshold = Math.Max(1, config.ElevatedSearchPressureActiveSearches);
        var criticalThreshold = Math.Max(elevatedThreshold, config.CriticalSearchPressureActiveSearches);

        var pressure = activeSearchCount >= criticalThreshold
            ? SearchPressureLevel.Critical
            : activeSearchCount >= elevatedThreshold
                ? SearchPressureLevel.Elevated
                : SearchPressureLevel.Normal;

        return pressure switch
        {
            SearchPressureLevel.Critical => new SearchExecutionProfile(
                pressure,
                ApplyPercent(baseResponseLimit, config.CriticalSearchResponseLimitPercent),
                ApplyPercent(baseFileLimit, config.CriticalSearchFileLimitPercent),
                1,
                Math.Max(0, config.CriticalSearchExtraDelayMs),
                1,
                Math.Max(500, config.CriticalSearchTokenBucketRefillMs)),

            SearchPressureLevel.Elevated => new SearchExecutionProfile(
                pressure,
                ApplyPercent(baseResponseLimit, config.ElevatedSearchResponseLimitPercent),
                ApplyPercent(baseFileLimit, config.ElevatedSearchFileLimitPercent),
                Math.Min(baseVariationCap, 2),
                Math.Max(0, config.ElevatedSearchExtraDelayMs),
                1,
                Math.Max(500, config.ElevatedSearchTokenBucketRefillMs)),

            _ => new SearchExecutionProfile(
                pressure,
                baseResponseLimit,
                baseFileLimit,
                baseVariationCap,
                0,
                Math.Max(1, config.SearchTokenBucketCapacity),
                Math.Max(500, config.SearchTokenBucketRefillMs))
        };
    }

    private static int ApplyPercent(int value, int percent)
    {
        var clampedPercent = Math.Clamp(percent, 10, 100);
        return Math.Max(20, (int)Math.Round(value * (clampedPercent / 100d), MidpointRounding.AwayFromZero));
    }
}