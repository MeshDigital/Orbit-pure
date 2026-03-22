using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.InputParsers;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SearchCandidateFitScorerTests
{
    [Fact]
    public void CalculateScore_ShouldPreferDurationAccurateCandidate()
    {
        var target = new TargetMetadata("Artist", "Track", "Album", 180);

        var accurate = new Track
        {
            Artist = "Artist",
            Title = "Track",
            Album = "Album",
            Format = "mp3",
            Bitrate = 320,
            Length = 180,
            QueueLength = 1,
            HasFreeUploadSlot = true
        };

        var inaccurate = new Track
        {
            Artist = "Artist",
            Title = "Track",
            Album = "Album",
            Format = "mp3",
            Bitrate = 320,
            Length = 210,
            QueueLength = 1,
            HasFreeUploadSlot = true
        };

        var accurateScore = SearchCandidateFitScorer.CalculateScore(accurate, target, new[] { "mp3" }, 192, 3);
        var inaccurateScore = SearchCandidateFitScorer.CalculateScore(inaccurate, target, new[] { "mp3" }, 192, 3);

        Assert.True(accurateScore > inaccurateScore);
    }

    [Fact]
    public void CalculateScore_ShouldPenalizeFormatOutsideFilter()
    {
        var target = new TargetMetadata("Artist", "Track", null, 180);

        var candidate = new Track
        {
            Artist = "Artist",
            Title = "Track",
            Format = "mp3",
            Bitrate = 320,
            Length = 180,
            QueueLength = 0,
            HasFreeUploadSlot = true
        };

        var matchingFilterScore = SearchCandidateFitScorer.CalculateScore(candidate, target, new[] { "mp3" }, 192, 3);
        var mismatchedFilterScore = SearchCandidateFitScorer.CalculateScore(candidate, target, new[] { "flac" }, 192, 3);

        Assert.True(matchingFilterScore > mismatchedFilterScore);
    }

    [Fact]
    public void ContainsNormalizedToken_ShouldIgnoreSymbolsAndCase()
    {
        var result = SearchCandidateFitScorer.ContainsNormalizedToken("ARTIST_(Original Mix)", "artist");

        Assert.True(result);
    }
}
