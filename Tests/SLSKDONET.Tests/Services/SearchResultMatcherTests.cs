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
            Filename = "Beatles - Yesterday.flac",
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
            Length = 203, // 3s mismatch -> still inside strict tolerance window
            Filename = "Artist - Title.flac",
            PathSegments = new List<string> { "Artist" }
        };

        // Act
        var result = _matcher.CalculateMatchResult(model, candidate);

        // Assert
        // Duration: 20 pts, Artist: 30 pts, Title: 20 pts -> 70 pts minimum
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
            Filename = "Jay Z - Empire State of Mind.flac",
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
            Filename = "Artist - Title.flac",
            PathSegments = new List<string> { "Artist" }
        };

        // Act
        var result = _matcher.CalculateMatchResult(model, candidate);

        // Assert
        // Duration: 0, Artist: 30, Title: 20 -> 50 pts (below 70 threshold)
        Assert.True(result.Score < 70);
        Assert.Equal("Duration Rejected", result.ShortReason);
    }

    [Fact]
    public void CalculateMatchResult_ShouldHardRejectDurationOutsideTolerance()
    {
        var model = new PlaylistTrack { Artist = "Artist", Title = "Title", CanonicalDuration = 200000 };
        var candidate = new Track
        {
            Artist = "Artist",
            Title = "Title",
            Length = 206,
            Filename = "Artist - Title.flac",
            Format = "flac"
        };

        var result = _matcher.CalculateMatchResult(model, candidate);

        Assert.Equal(0, result.Score);
        Assert.Equal("Duration Rejected", result.ShortReason);
        Assert.Contains("tolerance", result.RejectionReason);
    }

    [Fact]
    public void CalculateMatchResult_ShouldAcceptMp3WhenLossyFallbackRequested()
    {
        var model = new PlaylistTrack { Artist = "Artist", Title = "Title", CanonicalDuration = 200000 };
        var candidate = new Track
        {
            Artist = "Artist",
            Title = "Title",
            Length = 200,
            Bitrate = 320,
            Filename = "Artist - Title.mp3",
            Format = "mp3"
        };

        var result = _matcher.CalculateMatchResult(
            model,
            candidate,
            SearchResultMatcher.MatchOptions.LossyFallback(_config.SearchLengthToleranceSeconds));

        Assert.True(result.Score >= 70, $"Lossy fallback should allow MP3 candidates. Actual: {result.Score}");
        Assert.DoesNotContain("Lossy format rejected", result.RejectionReason ?? string.Empty);
    }

    [Fact]
    public void CalculateMatchResult_ShouldPreferStructuredReleaseFoldersOverJunkFolders()
    {
        // Arrange
        var model = new PlaylistTrack { Artist = "Artist", Title = "Title", CanonicalDuration = 200000 };
        var curated = new Track
        {
            Length = 200,
            Bitrate = 950,
            SampleRate = 44100,
            BitDepth = 24,
            Format = "flac",
            Filename = "Artist - Title.flac",
            Directory = @"Artist\[2024] Album [WEB] [FLAC]\Qobuz",
            PathSegments = new List<string> { "Artist", "[2024] Album [WEB] [FLAC]", "Qobuz" }
        };
        var junk = new Track
        {
            Length = 200,
            Bitrate = 950,
            SampleRate = 44100,
            BitDepth = 24,
            Format = "flac",
            Filename = "Artist - Title.flac",
            Directory = @"Users\Quint\Downloads\New Folder",
            PathSegments = new List<string> { "Downloads", "New Folder" }
        };

        // Act
        var curatedResult = _matcher.CalculateMatchResult(model, curated);
        var junkResult = _matcher.CalculateMatchResult(model, junk);

        // Assert
        Assert.True(curatedResult.Score > junkResult.Score, $"Curated release folder should outrank junk folder. Curated={curatedResult.Score}, Junk={junkResult.Score}");
    }

    [Fact]
    public void CalculateMatchResult_ShouldRewardSourceAnchorsInPath()
    {
        // Arrange
        var model = new PlaylistTrack { Artist = "Artist", Title = "Title", CanonicalDuration = 200000 };
        var plain = new Track
        {
            Length = 200,
            Bitrate = 900,
            SampleRate = 44100,
            BitDepth = 16,
            Format = "flac",
            Filename = "Artist - Title.flac",
            PathSegments = new List<string> { "Artist", "Album" }
        };
        var anchored = new Track
        {
            Length = 200,
            Bitrate = 900,
            SampleRate = 44100,
            BitDepth = 16,
            Format = "flac",
            Filename = "Artist - Title.flac",
            PathSegments = new List<string> { "Artist", "Album [WEB] [FLAC]", "Bandcamp" }
        };

        // Act
        var plainResult = _matcher.CalculateMatchResult(model, plain);
        var anchoredResult = _matcher.CalculateMatchResult(model, anchored);

        // Assert
        Assert.True(anchoredResult.Score > plainResult.Score, $"Source anchors should improve score. Anchored={anchoredResult.Score}, Plain={plainResult.Score}");
    }
}
