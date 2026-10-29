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
}
