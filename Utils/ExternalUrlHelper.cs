using System;
using System.Diagnostics;

namespace SLSKDONET.Utils;

/// <summary>
/// Single, validated way to open an external link. Restricts to absolute http/https URIs so a
/// value that came from an external source (a peer, an API response, an imported playlist, the
/// update check) can never reach <c>ShellExecute</c> as a <c>file:</c> path or a custom protocol
/// handler that would launch a local program.
/// </summary>
public static class ExternalUrlHelper
{
    /// <summary>
    /// Opens <paramref name="url"/> in the default browser, but only if it is an absolute
    /// http/https URI. Returns <c>false</c> (and logs) instead of throwing or opening anything else.
    /// </summary>
    public static bool TryOpen(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Serilog.Log.Warning("[ExternalUrlHelper] Refusing to open non-http(s) URL: {Url}", url);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[ExternalUrlHelper] Failed to open URL: {Url}", uri.AbsoluteUri);
            return false;
        }
    }

    /// <summary>
    /// The local path behind a <c>file:</c> URI (a CSV import's source, #332), or null for anything else. The file is
    /// never opened or executed: callers only reveal it in the file manager (<see cref="TryRevealLocalFile"/>).
    /// </summary>
    public static string? LocalFilePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    }

    /// <summary>
    /// Shows <paramref name="path"/> selected in the OS file manager (Explorer / Finder / the folder on Linux). It never
    /// runs the file. False (logged) when it couldn't. Routes through <see cref="SLSKDONET.Services.Platform.PlatformShell.Current"/> (#452).
    /// </summary>
    public static bool TryRevealLocalFile(string path)
        => SLSKDONET.Services.Platform.PlatformShell.Current.RevealInFolder(path);
}
