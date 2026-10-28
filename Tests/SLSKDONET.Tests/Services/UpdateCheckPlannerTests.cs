using System;
using System.Collections.Generic;
using System.Globalization;
using SLSKDONET.Services;
using SLSKDONET.Services.UpdateCheck;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class UpdateCheckPlannerTests
{
    [Fact]
    public void StableUser_NewerStable_ShowsUpdateNotice()
    {
        var notice = UpdatePlanner.Plan("0.2.0", new[]
        {
            Release("v0.2.0", "2026-10-28T22:30:59Z"),
            Release("v0.3.0", "2026-11-05T10:00:00Z")
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.StableRelease, notice!.Kind);
        Assert.Equal("Update available", notice.Title);
        Assert.Contains("v0.3.0", notice.Message);
        Assert.Equal("https://github.com/MeshDigital/Orbit-pure/releases/tag/v0.3.0", notice.Url);
    }

    [Fact]
    public void StableUser_NewerAlpha_ShowsEarlyNotice()
    {
        var notice = UpdatePlanner.Plan("0.2.0", new[]
        {
            Release("v0.2.0", "2026-10-28T22:30:59Z"),
            Release("v0.3.0-alpha.1", "2026-11-05T10:00:00Z", isPrerelease: true)
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.EarlyRelease, notice!.Kind);
        Assert.Equal("Early build available", notice.Title);
        Assert.Contains("alpha", notice.Message);
        Assert.Contains("pre-release", notice.Message);
        Assert.Equal("https://github.com/MeshDigital/Orbit-pure/releases/tag/v0.3.0-alpha.1", notice.Url);
    }

    [Fact]
    public void StableUser_NewerBeta_IsNotLabelledAsStableUpdate()
    {
        var notice = UpdatePlanner.Plan("0.2.0", new[]
        {
            Release("v0.2.0", "2026-10-28T22:30:59Z"),
            Release("v0.3.0-beta.2", "2026-11-05T10:00:00Z", isPrerelease: true)
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.EarlyRelease, notice!.Kind);
        Assert.DoesNotContain("Update available", notice.Title);
    }

    [Fact]
    public void StableUser_OlderAlpha_GetsEarlyChannelPointerButNotAnUpdate()
    {
        // 0.10.0-alpha predates the current stable 0.2.0 line. It must not be presented as an
        // upgrade, but the early channel is still discoverable via the releases page.
        var notice = UpdatePlanner.Plan("0.2.0", new[]
        {
            Release("v0.2.0", "2026-10-28T22:30:59Z"),
            Release("v0.10.0-alpha", "2026-10-24T13:23:12Z", isPrerelease: true)
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.EarlyChannel, notice!.Kind);
        Assert.Equal("Early builds available", notice.Title);
        Assert.Contains("v0.10.0-alpha", notice.Message);
        Assert.Contains("predates your current build", notice.Message);
        Assert.Equal(UpdatePlanner.ReleasesPageUrl, notice.Url);
    }

    [Fact]
    public void PreReleaseUser_NewerStableWithLowerSemVer_StillGetsUpdateNotice()
    {
        // The real 0.10.0-alpha -> 0.2.0 version-line reset. SemVer alone would call this a
        // downgrade and stay silent forever.
        var notice = UpdatePlanner.Plan("0.10.0-alpha", new[]
        {
            Release("v0.10.0-alpha", "2026-10-24T13:23:12Z", isPrerelease: true),
            Release("v0.2.0", "2026-10-28T22:30:59Z")
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.StableRelease, notice!.Kind);
        Assert.Contains("v0.2.0", notice.Message);
        Assert.Contains("alpha", notice.Message);
        Assert.Equal("https://github.com/MeshDigital/Orbit-pure/releases/tag/v0.2.0", notice.Url);
    }

    [Fact]
    public void PreReleaseUser_NoNewerBuild_IsToldTheyRunAPreRelease()
    {
        var notice = UpdatePlanner.Plan("0.10.0-alpha", new[]
        {
            Release("v0.5.0", "2026-06-19T18:11:54Z"),
            Release("v0.10.0-alpha", "2026-10-24T13:23:12Z", isPrerelease: true)
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.PreReleaseBuild, notice!.Kind);
        Assert.Equal("Pre-release build", notice.Title);
        Assert.Contains("0.10.0-alpha", notice.Message);
        Assert.Contains("v0.5.0", notice.Message);
    }

    [Fact]
    public void StableUser_NoNewerAndNoEarlyBuilds_NoNotice()
    {
        var notice = UpdatePlanner.Plan("0.2.0", new[]
        {
            Release("v0.2.0", "2026-10-28T22:30:59Z"),
            Release("v0.1.0", "2026-03-24T21:50:17Z")
        });

        Assert.Null(notice);
    }

    [Fact]
    public void ReleaseFlaggedPrereleaseWithoutTagSuffix_IsTreatedAsANonReleaseBuild()
    {
        // v0.4.0 exists with GitHub's pre-release flag on but no "-alpha"/"-beta" suffix.
        var release = Release("v0.4.0", "2026-11-05T10:00:00Z", isPrerelease: true);
        Assert.Equal(UpdateChannel.PreRelease, release.Channel);

        var notice = UpdatePlanner.Plan("0.3.0", new[]
        {
            Release("v0.3.0", "2026-10-28T22:30:59Z"),
            release
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.EarlyRelease, notice!.Kind);
        Assert.Contains("pre-release", notice.Message);
    }

    [Fact]
    public void BetaTagPublishedWithoutPrereleaseFlag_IsStillTreatedAsEarly()
    {
        var release = Release("v0.7.0-beta", "2026-08-03T16:19:56Z", isPrerelease: false);

        Assert.Equal(UpdateChannel.Beta, release.Channel);
        Assert.True(release.IsEarly);
    }

    [Fact]
    public void CurrentBuildWithCommitMetadata_MatchesPublishedRelease()
    {
        var notice = UpdatePlanner.Plan("0.2.0+3b51e77", new[]
        {
            Release("v0.2.0", "2026-10-28T22:30:59Z"),
            Release("v0.1.0", "2026-03-24T21:50:17Z")
        });

        Assert.Null(notice);
    }

    [Fact]
    public void UnreleasedStableBuild_NewerEarlyBuild_Notifies()
    {
        var notice = UpdatePlanner.Plan("0.3.0", new[]
        {
            Release("v0.2.0", "2026-10-28T22:30:59Z"),
            Release("v0.3.1-alpha.1", "2026-11-05T10:00:00Z", isPrerelease: true)
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.EarlyRelease, notice!.Kind);
        Assert.Contains("v0.3.1-alpha.1", notice.Message);
    }

    [Theory]
    [InlineData("0.2.0-alpha", "0.2.0-alpha.1", -1)]
    [InlineData("0.2.0-alpha.2", "0.2.0-alpha.10", -1)]
    [InlineData("0.2.0-alpha.10", "0.2.0-beta", -1)]
    [InlineData("0.2.0-beta", "0.2.0-rc.1", -1)]
    [InlineData("0.2.0-rc.1", "0.2.0", -1)]
    [InlineData("0.2.0", "0.2.0+build.5", 0)]
    [InlineData("v0.10.0-alpha", "0.2.0", 1)]
    public void SemVerOrdering_IsCorrect(string left, string right, int expectedSign)
    {
        var leftVersion = OrbitVersion.TryParse(left);
        var rightVersion = OrbitVersion.TryParse(right);

        Assert.NotNull(leftVersion);
        Assert.NotNull(rightVersion);
        Assert.Equal(expectedSign, Math.Sign(leftVersion!.CompareTo(rightVersion)));
    }

    [Fact]
    public void UnreleasedPreReleaseBuild_DoesNotGetPointedAtOlderCrossLineAlpha()
    {
        // A local 0.3.0-alpha build is numerically below 0.10.0-alpha, but that alpha predates
        // the current stable line. It must not be presented as a newer early build.
        var notice = UpdatePlanner.Plan("0.3.0-alpha.1", new[]
        {
            Release("v0.2.0", "2026-10-28T22:30:59Z"),
            Release("v0.10.0-alpha", "2026-10-24T13:23:12Z", isPrerelease: true)
        });

        Assert.NotNull(notice);
        Assert.Equal(UpdateNoticeKind.PreReleaseBuild, notice!.Kind);
        Assert.DoesNotContain("Early build available", notice.Title);
    }

    [Fact]
    public void ParseReleases_SkipsDraftsAndNonVersionTags()
    {
        const string json = """
        [
          { "tag_name": "v0.3.0", "name": "draft", "html_url": "https://example.test/draft",
            "draft": true, "prerelease": false, "published_at": "2026-11-01T00:00:00Z" },
          { "tag_name": "nightly-build", "name": "not a version", "html_url": "https://example.test/nightly",
            "draft": false, "prerelease": true, "published_at": "2026-11-01T00:00:00Z" },
          { "tag_name": "v0.3.0-alpha.1", "name": "alpha", "html_url": "https://example.test/alpha",
            "draft": false, "prerelease": true, "published_at": "2026-11-02T00:00:00Z" }
        ]
        """;

        var releases = UpdateCheckService.ParseReleases(json);

        var release = Assert.Single(releases);
        Assert.Equal("v0.3.0-alpha.1", release.TagName);
        Assert.Equal(UpdateChannel.Alpha, release.Channel);
        Assert.Equal("https://example.test/alpha", release.HtmlUrl);
    }

    private static UpdateRelease Release(string tag, string publishedAt, bool isPrerelease = false)
    {
        var release = UpdateRelease.TryCreate(
            tag,
            tag,
            $"https://github.com/MeshDigital/Orbit-pure/releases/tag/{tag}",
            isPrerelease,
            DateTimeOffset.Parse(
                publishedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

        Assert.NotNull(release);
        return release!;
    }
}
