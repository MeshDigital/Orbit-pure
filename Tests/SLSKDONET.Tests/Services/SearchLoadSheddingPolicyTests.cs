using SLSKDONET.Configuration;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SearchLoadSheddingPolicyTests
{
    [Fact]
    public void Compute_ReturnsNormalProfile_WhenBelowElevatedThreshold()
    {
        var config = new AppConfig
        {
            EnableSearchLoadShedding = true,
            ElevatedSearchPressureActiveSearches = 3,
            CriticalSearchPressureActiveSearches = 5,
            SearchResponseLimit = 100,
            SearchFileLimit = 120,
            MaxSearchVariations = 3
        };

        var profile = SearchLoadSheddingPolicy.Compute(config, activeSearchCount: 2);

        Assert.Equal(SearchPressureLevel.Normal, profile.PressureLevel);
        Assert.Equal(100, profile.EffectiveResponseLimit);
        Assert.Equal(120, profile.EffectiveFileLimit);
        Assert.Equal(3, profile.EffectiveVariationCap);
        Assert.Equal(0, profile.AdditionalThrottleDelayMs);
    }

    [Fact]
    public void Compute_ReturnsElevatedProfile_WhenAtElevatedThreshold()
    {
        var config = new AppConfig
        {
            EnableSearchLoadShedding = true,
            ElevatedSearchPressureActiveSearches = 3,
            CriticalSearchPressureActiveSearches = 5,
            SearchResponseLimit = 100,
            SearchFileLimit = 120,
            MaxSearchVariations = 4,
            ElevatedSearchResponseLimitPercent = 75,
            ElevatedSearchFileLimitPercent = 75,
            ElevatedSearchExtraDelayMs = 80
        };

        var profile = SearchLoadSheddingPolicy.Compute(config, activeSearchCount: 3);

        Assert.Equal(SearchPressureLevel.Elevated, profile.PressureLevel);
        Assert.Equal(75, profile.EffectiveResponseLimit);
        Assert.Equal(90, profile.EffectiveFileLimit);
        Assert.Equal(2, profile.EffectiveVariationCap);
        Assert.Equal(80, profile.AdditionalThrottleDelayMs);
    }

    [Fact]
    public void Compute_ReturnsCriticalProfile_WhenAtCriticalThreshold()
    {
        var config = new AppConfig
        {
            EnableSearchLoadShedding = true,
            ElevatedSearchPressureActiveSearches = 3,
            CriticalSearchPressureActiveSearches = 5,
            SearchResponseLimit = 100,
            SearchFileLimit = 120,
            MaxSearchVariations = 4,
            CriticalSearchResponseLimitPercent = 50,
            CriticalSearchFileLimitPercent = 50,
            CriticalSearchExtraDelayMs = 200
        };

        var profile = SearchLoadSheddingPolicy.Compute(config, activeSearchCount: 5);

        Assert.Equal(SearchPressureLevel.Critical, profile.PressureLevel);
        Assert.Equal(50, profile.EffectiveResponseLimit);
        Assert.Equal(60, profile.EffectiveFileLimit);
        Assert.Equal(1, profile.EffectiveVariationCap);
        Assert.Equal(200, profile.AdditionalThrottleDelayMs);
    }
}
