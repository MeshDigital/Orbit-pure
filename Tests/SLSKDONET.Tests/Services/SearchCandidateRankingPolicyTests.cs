using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SearchCandidateRankingPolicyTests
{
    [Fact]
    public void CalculateFinalScore_ShouldIncreaseWithHigherReliability()
    {
        var lowReliability = SearchCandidateRankingPolicy.CalculateFinalScore(
            matchScore: 80,
            fitScore: 80,
            reliability: 0.2,
            queueLength: 0);

        var highReliability = SearchCandidateRankingPolicy.CalculateFinalScore(
            matchScore: 80,
            fitScore: 80,
            reliability: 0.9,
            queueLength: 0);

        Assert.True(highReliability > lowReliability);
    }

    [Fact]
    public void CalculateFinalScore_ShouldPenalizeLongQueues()
    {
        var shortQueue = SearchCandidateRankingPolicy.CalculateFinalScore(
            matchScore: 85,
            fitScore: 85,
            reliability: 0.5,
            queueLength: 2);

        var longQueue = SearchCandidateRankingPolicy.CalculateFinalScore(
            matchScore: 85,
            fitScore: 85,
            reliability: 0.5,
            queueLength: 50);

        Assert.True(shortQueue > longQueue);
    }

    [Fact]
    public void MatchScoreFromRank_ShouldMapUnitIntervalToPercent()
    {
        Assert.Equal(0, SearchCandidateRankingPolicy.MatchScoreFromRank(-1));
        Assert.Equal(0, SearchCandidateRankingPolicy.MatchScoreFromRank(0));
        Assert.Equal(50, SearchCandidateRankingPolicy.MatchScoreFromRank(0.5));
        Assert.Equal(100, SearchCandidateRankingPolicy.MatchScoreFromRank(1));
        Assert.Equal(100, SearchCandidateRankingPolicy.MatchScoreFromRank(2));
    }
}
