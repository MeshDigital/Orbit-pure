using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Inputs;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SafetyFilterTests
{
    private readonly Mock<ILogger<SafetyFilterService>> _loggerMock;
    private readonly AppConfig _config;
    private readonly SafetyFilterService _service;

    public SafetyFilterTests()
    {
        _loggerMock = new Mock<ILogger<SafetyFilterService>>();
        _config = new AppConfig
        {
            SearchPolicy = SearchPolicy.QualityFirst()
        };
        _service = new SafetyFilterService(_loggerMock.Object, _config);
    }

    [Fact]
    public void IsSafe_ShouldReject_BlacklistedUser()
    {
        // Arrange
        _config.BlacklistedUsers = new List<string> { "BannedUser" };
        var track = new Track { Username = "BannedUser", Length = 300, Bitrate = 320, Filename = "Test Query.mp3" };
        var searchTrack = new Track { Length = 300 };

        // Act
        bool isSafe = _service.IsSafe(track, "Test Query", searchTrack.Length);

        // Assert
        Assert.False(isSafe);
    }

    [Fact]
    public void IsSafe_ShouldAllow_NormalUser()
    {
        // Arrange
        _config.BlacklistedUsers = new List<string> { "BannedUser" };
        var track = new Track { Username = "GoodUser", Length = 300, Bitrate = 320, Filename = "Test Query.mp3" };
        var searchTrack = new Track { Length = 300 };

        // Act
        bool isSafe = _service.IsSafe(track, "Test Query", searchTrack.Length);

        // Assert
        Assert.True(isSafe);
    }

    [Fact]
    public void IsSafe_ShouldReject_DurationMismatch_IfStrict()
    {
        // Arrange
        _config.SearchPolicy.DurationToleranceSeconds = 10;
        var track = new Track { Length = 500, Bitrate = 320, Filename = "Test Query.mp3" }; // 200s diff
        var searchTrack = new Track { Length = 300 };

        // Act
        bool isSafe = _service.IsSafe(track, "Test Query", searchTrack.Length);

        // Assert
        Assert.False(isSafe);
    }

    [Fact]
    public void IsSafe_ShouldAllow_DurationMatch()
    {
        // Arrange
        _config.SearchPolicy.DurationToleranceSeconds = 10;
        var track = new Track { Length = 305, Bitrate = 320, Filename = "Test Query.mp3" }; // 5s diff
        var searchTrack = new Track { Length = 300 };

        // Act
        bool isSafe = _service.IsSafe(track, "Test Query", searchTrack.Length);

        // Assert
        Assert.True(isSafe);
    }
}
