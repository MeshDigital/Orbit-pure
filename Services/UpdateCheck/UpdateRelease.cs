using System;

namespace SLSKDONET.Services.UpdateCheck;

/// <summary>How a GitHub release relates to the stable release channel.</summary>
internal enum UpdateChannel
{
    Stable = 0,
    PreRelease = 1,
    Alpha = 2,
    Beta = 3,
    ReleaseCandidate = 4,
    Development = 5
}

/// <summary>
/// One published (non-draft) GitHub release, reduced to what the update notice needs.
/// </summary>
internal sealed record UpdateRelease(
    string TagName,
    string DisplayName,
    string HtmlUrl,
    UpdateChannel Channel,
    DateTimeOffset? PublishedAt,
    OrbitVersion Version)
{
    public bool IsEarly => Channel != UpdateChannel.Stable;

    public string ChannelLabel => Channel switch
    {
        UpdateChannel.Alpha => "alpha",
        UpdateChannel.Beta => "beta",
        UpdateChannel.ReleaseCandidate => "RC",
        UpdateChannel.Development => "development",
        UpdateChannel.PreRelease => "pre-release",
        _ => "stable"
    };

    /// <summary>
    /// Parses one GitHub release. Returns null for tags that are not versions (or for malformed
    /// release payloads) so unrelated tags can never become an "update".
    /// </summary>
    public static UpdateRelease? TryCreate(
        string? tagName,
        string? name,
        string? htmlUrl,
        bool isPrerelease,
        DateTimeOffset? publishedAt)
    {
        if (string.IsNullOrWhiteSpace(tagName) || string.IsNullOrWhiteSpace(htmlUrl))
            return null;

        var version = OrbitVersion.TryParse(tagName);
        if (version is null)
            return null;

        var channel = Classify(version, isPrerelease);

        return new UpdateRelease(
            tagName.Trim(),
            string.IsNullOrWhiteSpace(name) ? tagName.Trim() : name.Trim(),
            htmlUrl.Trim(),
            channel,
            publishedAt,
            version);
    }

    private static UpdateChannel Classify(OrbitVersion version, bool isPrerelease)
    {
        // The tag suffix is authoritative: v0.7.0-beta was accidentally published with GitHub's
        // "pre-release" flag off, and v0.4.0 with it on. Trusting only the flag would call a beta
        // a stable update (or hide a stable one).
        if (!version.IsPreRelease)
            return isPrerelease ? UpdateChannel.PreRelease : UpdateChannel.Stable;

        foreach (var identifier in version.PreRelease)
        {
            switch (identifier.ToLowerInvariant())
            {
                case "alpha":
                    return UpdateChannel.Alpha;
                case "beta":
                    return UpdateChannel.Beta;
                case "rc":
                    return UpdateChannel.ReleaseCandidate;
                case "nightly":
                case "dev":
                case "development":
                    return UpdateChannel.Development;
                case "preview":
                    return UpdateChannel.PreRelease;
            }
        }

        return UpdateChannel.PreRelease;
    }
}
