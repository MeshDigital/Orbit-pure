using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Collections.Generic;

namespace SLSKDONET.Tests.Services;

public class SearchResultMatcherTests
{
    private readonly SearchResultMatcher _matcher;
    private readonly AppConfig _config;

    public SearchResultMatcherTests()
    {
        _config = new AppConfig
        {
            FuzzyMatchEnabled = true,
            SearchLengthToleranceSeconds = 3,
            EnableFuzzyNormalization = true
        };
        _matcher = new SearchResultMatcher(NullLogger<SearchResultMatcher>.Instance, _config);
    }

    [Fact]
    public void CalculateScore_ShouldBeLenientWithArtistPrefixes()
    {
        // Arrange
        var model = new PlaylistTrack { Artist = "The Beatles", Title = "Yesterday", CanonicalDuration = 125000 };
        var candidate = new Track 
        { 
            Artist = "Beatles", 
            Title = "Yesterday", 
            Length = 125, 
            Filename = "Beatles - Yesterday.mp3",
            PathSegments = new List<string> { "The Beatles", "Yesterday" }
        };

        // Act
        var result = _matcher.CalculateMatchResult(model, candidate);

        // Assert
        // Duration: 40, Artist tokens in path: 30, Title tokens in path: 20 -> 90 pts
        Assert.True(result.Score >= 90, $"Score should be high for 'The Beatles' vs 'Beatles'. Actual: {result.Score}");
    }

    [Fact]
    public void CalculateScore_ShouldAllowMinorDurationMismatch()
    {
        // Arrange
        var model = new PlaylistTrack { Artist = "Artist", Title = "Title", CanonicalDuration = 200000 };
        var candidate = new Track 
        { 
            Artist = "Artist", 
            Title = "Title", 
            Length = 205, // 5s mismatch -> 20 pts (not 40)
            Filename = "Artist - Title.mp3",
            PathSegments = new List<string> { "Artist" }
        };

        // Act
        var result = _matcher.CalculateMatchResult(model, candidate);

        // Assert
        // Duration: 20 pts, Artist: 30 pts, Title: 20 pts -> 70 pts
        Assert.True(result.Score >= 70, $"Score should be acceptable for minor duration mismatch. Actual: {result.Score}");
    }

    [Fact]
    public void CalculateScore_ShouldRescueArtistMismatchWithHighSimilarity()
    {
        // Arrange
        var model = new PlaylistTrack { Artist = "Jay-Z", Title = "Empire State of Mind", CanonicalDuration = 276000 };
        var candidate = new Track 
        { 
            Length = 276, 
            Filename = "Jay Z - Empire State of Mind.mp3",
            PathSegments = new List<string> { "Jay Z" }
        };

        // Act
        var result = _matcher.CalculateMatchResult(model, candidate);

        // Assert
        // Jay-Z tokens: ["jay", "z"]. Jay Z contains both.
        // Duration: 40, Artist: 30, Title: 20 -> 90 pts
        Assert.True(result.Score >= 90, $"Score should be high for 'Jay-Z' vs 'Jay Z'. Actual: {result.Score}");
    }

    [Fact]
    public void CalculateScore_ShouldRejectExtremeDurationMismatch()
    {
        // Arrange
        var model = new PlaylistTrack { Artist = "Artist", Title = "Title", CanonicalDuration = 200000 };
        var candidate = new Track 
        { 
            Length = 300, // 100s mismatch -> 0 pts
            Filename = "Artist - Title.mp3",
            PathSegments = new List<string> { "Artist" }
        };

        // Act
        var result = _matcher.CalculateMatchResult(model, candidate);

        // Assert
        // Duration: 0, Artist: 30, Title: 20 -> 50 pts (below 70 threshold)
        Assert.True(result.Score < 70);
    }
}
