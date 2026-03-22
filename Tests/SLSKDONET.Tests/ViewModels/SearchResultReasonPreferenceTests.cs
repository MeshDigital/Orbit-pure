using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class SearchResultReasonPreferenceTests
{
    [Fact]
    public void MatchReason_ShouldPreferModelMatchReason_WhenPresent()
    {
        var track = new Track
        {
            MatchReason = "strong fit • trusted peer • score 92",
            ScoreBreakdown = "Blend: Match=80.0, Fit=90.0, Rel=0.90, Queue=0, Final=92.0"
        };

        var vm = new AnalyzedSearchResultViewModel(new SearchResult(track));

        Assert.Equal("strong fit • trusted peer • score 92", vm.MatchReason);
        Assert.True(vm.HasMatchReason);
    }

    [Fact]
    public void MatchReason_ShouldFallbackToScoreBreakdown_WhenModelReasonMissing()
    {
        var track = new Track
        {
            MatchReason = null,
            ScoreBreakdown = "Blend: Match=72.0, Fit=68.0, Rel=0.50, Queue=1, Final=70.4"
        };

        var vm = new AnalyzedSearchResultViewModel(new SearchResult(track));

        Assert.Equal("Blend: Match=72.0, Fit=68.0, Rel=0.50, Queue=1, Final=70.4", vm.MatchReason);
        Assert.True(vm.HasMatchReason);
    }
}
