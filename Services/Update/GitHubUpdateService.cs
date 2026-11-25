using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
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
    /// <summary>
    /// The installer/package asset URL matching the current OS and CPU architecture (#459):
    /// Windows <c>.exe</c>, macOS <c>.dmg</c> (<c>arm64</c> vs <c>x64</c> or universal), or Linux <c>.AppImage</c>/<c>.tar.gz</c>.
    /// </summary>
    public string? InstallerUrl => GitHubUpdateService.SelectInstallerUrl(
        Assets,
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(),
        RuntimeInformation.ProcessArchitecture);
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

    /// <summary>
    /// Reads the running app's version from <see cref="AssemblyInformationalVersionAttribute"/>
    /// (cross-platform; avoids Windows-only file-version metadata that reads as 0.0.0.0 on macOS, #459).
    /// </summary>
    public static Version GetCurrentAppVersion(Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(GitHubUpdateService).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (ParseVersion(info ?? string.Empty) is { } parsed && parsed > new Version(0, 0, 0))
            return parsed;

        var asmVer = asm.GetName().Version;
        if (asmVer is not null && asmVer > new Version(0, 0, 0, 0))
            return new Version(asmVer.Major, asmVer.Minor, Math.Max(0, asmVer.Build));

        return new Version(0, 0, 0);
    }

    /// <summary>
    /// Picks the right release asset for the host OS and CPU architecture (#459):
    /// prefers <c>arm64</c> on Apple Silicon Macs and <c>x64</c> on Intel Macs, falling back to a universal DMG.
    /// </summary>
    public static string? SelectInstallerUrl(
        IReadOnlyList<GitHubAsset>? assets,
        bool isWindows,
        bool isMacOS,
        Architecture arch)
    {
        if (assets is null || assets.Count == 0)
            return null;

        static bool Has(GitHubAsset a, string token) => a.Name.Contains(token, StringComparison.OrdinalIgnoreCase);
        static bool Ends(GitHubAsset a, string ext) => a.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase);

        if (isWindows)
        {
            return assets.FirstOrDefault(a => Ends(a, ".exe"))?.BrowserDownloadUrl
                ?? assets.FirstOrDefault(a => Ends(a, ".zip") && Has(a, "win"))?.BrowserDownloadUrl;
        }

        if (isMacOS)
        {
            var isArm = arch == Architecture.Arm64;
            var wantedArch = isArm ? "arm64" : "x64";
            var otherArch = isArm ? "x64" : "arm64";

            // 1. Exact arch .dmg (e.g. ORBIT-0.4.0-osx-arm64.dmg)
            var exactDmg = assets.FirstOrDefault(a => Ends(a, ".dmg") && Has(a, wantedArch));
            if (exactDmg is not null) return exactDmg.BrowserDownloadUrl;

            // 2. Universal .dmg (no conflicting arch token)
            var universalDmg = assets.FirstOrDefault(a => Ends(a, ".dmg") && !Has(a, otherArch));
            if (universalDmg is not null) return universalDmg.BrowserDownloadUrl;

            // 3. Archive fallback (osx-arm64 / osx-x64 .tar.gz or .zip)
            var archArchive = assets.FirstOrDefault(a =>
                (Ends(a, ".tar.gz") || Ends(a, ".zip")) && Has(a, "osx") && Has(a, wantedArch));
            return archArchive?.BrowserDownloadUrl;
        }

        // Linux: AppImage first, then linux tar.gz
        var linuxArch = arch == Architecture.Arm64 ? "arm64" : "x64";
        return assets.FirstOrDefault(a => Ends(a, ".AppImage") && Has(a, linuxArch))?.BrowserDownloadUrl
            ?? assets.FirstOrDefault(a => Ends(a, ".AppImage"))?.BrowserDownloadUrl
            ?? assets.FirstOrDefault(a => Ends(a, ".tar.gz") && Has(a, "linux") && Has(a, linuxArch))?.BrowserDownloadUrl
            ?? assets.FirstOrDefault(a => Ends(a, ".tar.gz") && Has(a, "linux"))?.BrowserDownloadUrl;
    }
}
