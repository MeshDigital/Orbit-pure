using SLSKDONET.Models;
using SLSKDONET.Models.Discovery;
using Xunit;

namespace SLSKDONET.Tests.Models.Discovery;

public class DiscoveryDtosTests
{
    [Fact]
    public void DiscoverySearchResultDto_PreferredReason_ShouldPreferMatchReason()
    {
        var dto = new DiscoverySearchResultDto
        {
            MatchReason = "strong fit • trusted peer • score 92",
            Track = new Track { ScoreBreakdown = "Blend: Match=80.0, Fit=90.0, Final=92.0" }
        };

        Assert.Equal("strong fit • trusted peer • score 92", dto.PreferredReason);
    }

    [Fact]
    public void DiscoverySearchResultDto_PreferredReason_ShouldFallbackToTrackScoreBreakdown()
    {
        var dto = new DiscoverySearchResultDto
        {
            MatchReason = null,
            Track = new Track { ScoreBreakdown = "Blend: Match=72.0, Fit=68.0, Final=70.4" }
        };

        Assert.Equal("Blend: Match=72.0, Fit=68.0, Final=70.4", dto.PreferredReason);
    }

    [Fact]
    public void DiscoveryTrack_PreferredReason_ShouldMirrorMatchReason()
    {
        var dto = new DiscoveryTrack
        {
            MatchReason = "Spotify Recommendation (Sonic)"
        };

        Assert.Equal("Spotify Recommendation (Sonic)", dto.PreferredReason);
    }
}
