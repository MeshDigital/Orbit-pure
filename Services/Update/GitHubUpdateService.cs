using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.Update;

/// <summary>Which release line the user wants updates from.</summary>
public enum UpdateChannel
{
    /// <summary>Full, non-pre-release releases only.</summary>
    Stable = 0,
    /// <summary>Beta / pre-releases.</summary>
    Beta = 1,
    /// <summary>Whatever is newest (stable or pre-release).</summary>
    Any = 2,
}

/// <summary>A single downloadable file attached to a GitHub release.</summary>
public sealed record GitHubAsset(string Name, string BrowserDownloadUrl);

/// <summary>A GitHub release, trimmed to what the updater needs.</summary>
public sealed record GitHubRelease(
    string TagName,
    string Name,
    bool IsPrerelease,
    DateTimeOffset PublishedAt,
    string HtmlUrl,
    IReadOnlyList<GitHubAsset> Assets)
{
    /// <summary>The installer (.exe) asset URL, or null when none is attached.</summary>
    public string? InstallerUrl =>
        Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl;
}

/// <summary>
/// Checks the ORBIT GitHub releases for updates and filters them by channel + "newer than current".
/// Pure version/channel logic is static so it can be unit-tested without network.
/// </summary>
public sealed class GitHubUpdateService
{
    private const string ReleasesUrl = "https://api.github.com/repos/MeshDigital/Orbit-pure/releases";

    private readonly HttpClient _http;
    private readonly Version _currentVersion;

    public GitHubUpdateService(HttpClient http, Version currentVersion)
    {
        _http = http;
        _currentVersion = currentVersion;
    }

    /// <summary>Returns the newest release in <paramref name="channel"/> newer than the current version, or null.</summary>
    public async Task<GitHubRelease?> CheckForUpdatesAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        var releases = await _http.GetFromJsonAsync<List<GitHubRelease>>(ReleasesUrl, ct).ConfigureAwait(false);
        if (releases is null)
            return null;

        return releases
            .Where(r => MatchesChannel(r, channel))
            .Where(r => IsNewer(r.TagName, _currentVersion))
            .OrderByDescending(r => r.PublishedAt)
            .FirstOrDefault();
    }

    public static bool MatchesChannel(GitHubRelease release, UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => !release.IsPrerelease,
        UpdateChannel.Beta => release.IsPrerelease,
        UpdateChannel.Any => true,
        _ => false,
    };

    public static bool IsNewer(string tagName, Version currentVersion)
    {
        var version = ParseVersion(tagName);
        return version is not null && version > currentVersion;
    }

    /// <summary>Parses "v0.3.0", "0.3.0-beta", "v0.3.1-rc.1" to a <see cref="Version"/>; null when unparseable.</summary>
    public static Version? ParseVersion(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return null;

        var s = tagName.Trim();
        if (s[0] is 'v' or 'V')
            s = s[1..];

        // Strip a pre-release ("-beta") or build ("+abc") suffix.
        var dash = s.IndexOf('-');
        var plus = s.IndexOf('+');
        var cut = (dash >= 0, plus >= 0) switch
        {
            (true, true) => Math.Min(dash, plus),
            (true, false) => dash,
            (false, true) => plus,
            _ => -1,
        };
        if (cut >= 0)
            s = s[..cut];

        return Version.TryParse(s, out var version) ? version : null;
    }
}
