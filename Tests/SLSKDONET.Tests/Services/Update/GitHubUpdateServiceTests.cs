using System;
using SLSKDONET.Services.Update;
using Xunit;

namespace SLSKDONET.Tests.Services.Update;

public class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("v0.3.1-beta", "0.3.1")]
    [InlineData("0.3.1-rc.1", "0.3.1")]
    [InlineData("v1.2.3+build.5", "1.2.3")]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void ParseVersion_Parses(string tag, string? expected)
        => Assert.Equal(expected, GitHubUpdateService.ParseVersion(tag)?.ToString());

    [Fact]
    public void IsNewer_ComparesAgainstCurrent()
    {
        var current = new Version(0, 3, 0);
        Assert.True(GitHubUpdateService.IsNewer("v0.3.1", current));
        Assert.False(GitHubUpdateService.IsNewer("v0.3.0", current));
        Assert.False(GitHubUpdateService.IsNewer("v0.2.9", current));
    }

    [Fact]
    public void MatchesChannel_FiltersByPrerelease()
    {
        var stable = new GitHubRelease("v0.3.0", "0.3.0", IsPrerelease: false, default, "u", Array.Empty<GitHubAsset>());
        var beta = new GitHubRelease("v0.3.1-beta", "0.3.1-beta", IsPrerelease: true, default, "u", Array.Empty<GitHubAsset>());

        Assert.True(GitHubUpdateService.MatchesChannel(stable, UpdateChannel.Stable));
        Assert.False(GitHubUpdateService.MatchesChannel(beta, UpdateChannel.Stable));
        Assert.True(GitHubUpdateService.MatchesChannel(beta, UpdateChannel.Beta));
        Assert.True(GitHubUpdateService.MatchesChannel(stable, UpdateChannel.Any));
        Assert.True(GitHubUpdateService.MatchesChannel(beta, UpdateChannel.Any));
    }
}
