using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.UpdateCheck;

internal enum UpdateNoticeKind
{
    StableRelease = 0,
    EarlyRelease = 1,
    PreReleaseBuild = 2,
    EarlyChannel = 3
}

internal sealed record UpdateNotice(
    UpdateNoticeKind Kind,
    string Key,
    string Title,
    string Message,
    string Url);

/// <summary>
/// Decides what (if anything) to tell the user about published builds. Kept pure so the tricky
/// parts — pre-release ordering, the 0.10.0-alpha → 0.2.0 version-line reset, and "is this release
/// actually newer than what I'm running" — are unit-testable without HTTP.
/// </summary>
internal static class UpdatePlanner
{
    /// <summary>Landing page used when the newest early build is not newer than this build.</summary>
    internal const string ReleasesPageUrl = "https://github.com/MeshDigital/Orbit-pure/releases";

    public static UpdateNotice? Plan(string? currentVersionText, IReadOnlyList<UpdateRelease> releases)
    {
        var current = OrbitVersion.TryParse(currentVersionText);
        if (current is null || releases.Count == 0)
            return null;

        var currentRelease = FindCurrent(current, releases);

        var stable = Newest(releases, release => release.Channel == UpdateChannel.Stable);
        var early = Newest(releases, release => release.IsEarly);

        // For an unreleased local build we don't know its date, so anchor comparisons to the
        // newest stable release instead of trusting SemVer across a version-line reset.
        var baseline = currentRelease?.PublishedAt ?? stable?.PublishedAt;

        bool IsNewer(UpdateRelease release)
        {
            if (release.Version.Equals(current))
                return false;

            // Publication order, not number order, decides what is newer. ORBIT restarted its
            // version line at 0.2.0 after 0.10.0-alpha, so plain SemVer would call the newer
            // stable build a downgrade and the older alpha an update.
            if (baseline.HasValue && release.PublishedAt.HasValue)
                return release.PublishedAt.Value > baseline.Value;

            // No dates to compare (e.g. a local build and a date-less release): SemVer fallback.
            return release.Version.CompareTo(current) > 0;
        }

        if (stable is not null && IsNewer(stable))
            return StableNotice(stable, current, currentRelease);

        if (early is not null && IsNewer(early))
            return EarlyNotice(early, current);

        // This build itself is a non-release (alpha/beta/RC) build: say so, and point at the
        // stable channel so the user knows where the promoted build will appear.
        if (current.IsPreRelease)
            return PreReleaseNotice(current, currentRelease, stable, releases);

        // A stable user with no newer build still gets told that the early channel exists. Point
        // at the releases page rather than the older tag so this can never look like a downgrade.
        if (early is not null)
            return EarlyChannelNotice(early, baseline, current);

        return null;
    }

    private static UpdateRelease? FindCurrent(OrbitVersion current, IReadOnlyList<UpdateRelease> releases)
    {
        foreach (var release in releases)
        {
            if (release.Version.Equals(current))
                return release;
        }

        return null;
    }

    private static UpdateRelease? Newest(
        IReadOnlyList<UpdateRelease> releases,
        Func<UpdateRelease, bool> predicate)
    {
        UpdateRelease? best = null;

        foreach (var release in releases)
        {
            if (!predicate(release))
                continue;

            if (best is null || CompareRecency(release, best) > 0)
                best = release;
        }

        return best;
    }

    private static int CompareRecency(UpdateRelease left, UpdateRelease right)
    {
        if (left.PublishedAt.HasValue && right.PublishedAt.HasValue)
        {
            var byDate = left.PublishedAt.Value.CompareTo(right.PublishedAt.Value);
            if (byDate != 0)
                return byDate;
        }
        else if (left.PublishedAt.HasValue)
        {
            return 1;
        }
        else if (right.PublishedAt.HasValue)
        {
            return -1;
        }

        return left.Version.CompareTo(right.Version);
    }

    private static UpdateNotice StableNotice(
        UpdateRelease stable,
        OrbitVersion current,
        UpdateRelease? currentRelease)
    {
        var currentChannel = current.IsPreRelease
            ? $" ({currentRelease?.ChannelLabel ?? "pre-release"})"
            : string.Empty;

        return new UpdateNotice(
            UpdateNoticeKind.StableRelease,
            "stable:" + stable.TagName,
            "Update available",
            $"ORBIT {stable.TagName} is available — you're on {current}{currentChannel}. {stable.HtmlUrl}",
            stable.HtmlUrl);
    }

    private static UpdateNotice EarlyNotice(UpdateRelease early, OrbitVersion current)
    {
        return new UpdateNotice(
            UpdateNoticeKind.EarlyRelease,
            "early:" + early.TagName,
            "Early build available",
            $"ORBIT {early.TagName} ({early.ChannelLabel}) is available — you're on {current}. " +
            $"This is a pre-release build; expect rough edges. {early.HtmlUrl}",
            early.HtmlUrl);
    }

    private static UpdateNotice PreReleaseNotice(
        OrbitVersion current,
        UpdateRelease? currentRelease,
        UpdateRelease? stable,
        IReadOnlyList<UpdateRelease> releases)
    {
        var channel = currentRelease?.ChannelLabel ?? "pre-release";
        var url = stable?.HtmlUrl ?? currentRelease?.HtmlUrl ?? releases[0].HtmlUrl;

        var stableText = stable is not null
            ? $" The latest stable release is {stable.TagName}."
            : string.Empty;

        return new UpdateNotice(
            UpdateNoticeKind.PreReleaseBuild,
            "prerelease:" + current,
            "Pre-release build",
            $"You're running ORBIT {current} ({channel}), a pre-release build.{stableText} {url}",
            url);
    }

    private static UpdateNotice EarlyChannelNotice(UpdateRelease early, DateTimeOffset? baseline, OrbitVersion current)
    {
        var publishedText = early.PublishedAt.HasValue
            ? $", {early.PublishedAt.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;

        var olderText = early.PublishedAt.HasValue && baseline.HasValue && early.PublishedAt.Value < baseline.Value
            ? " It predates your current build, so this is informational only."
            : string.Empty;

        return new UpdateNotice(
            UpdateNoticeKind.EarlyChannel,
            "early-channel:" + current,
            "Early builds available",
            $"ORBIT also publishes alpha/beta test builds. Newest early build: " +
            $"{early.TagName} ({early.ChannelLabel}{publishedText}).{olderText} " +
            $"Browse releases: {ReleasesPageUrl}",
            ReleasesPageUrl);
    }
}
