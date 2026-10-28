using System;
using System.Net;
using System.Net.Http;
using SpotifyAPI.Web;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// Turns Spotify API / network failures into a short, actionable <see cref="ImportSourceException"/>
/// for the import preview's error state, instead of the raw exception text.
/// </summary>
public static class SpotifyImportErrors
{
    /// <summary>
    /// Spotify answers every Web API call with 403 "the user may not be registered" when the signed-in
    /// account isn't on the allowlist of a Client ID that is still in development mode. The browser
    /// sign-in itself succeeds, so without this check it looks like a per-playlist permission problem
    /// and "Connect Spotify" loops forever.
    /// </summary>
    public static bool IsAccountNotAllowlisted(Exception ex)
    {
        if (ex is SpotifyAccountNotAllowlistedException) return true;
        if (ex is not APIException api || api.Response?.StatusCode != HttpStatusCode.Forbidden) return false;
        var text = api.Message + " " + api.Response?.Body;
        return text.Contains("not be registered", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not registered", StringComparison.OrdinalIgnoreCase);
    }

    public static ImportSourceException Translate(Exception ex)
    {
        switch (ex)
        {
            case ImportSourceException already:
                return already;
            case SpotifyAccountNotAllowlistedException notAllowed:
                return new ImportSourceException(notAllowed.Message, inner: ex);
            case APIException api when IsAccountNotAllowlisted(api):
                // Reconnecting can't fix this, so no "Connect Spotify" button.
                return new ImportSourceException(SpotifyAccountNotAllowlistedException.UserMessage, inner: ex);
            case APIUnauthorizedException:
                return new ImportSourceException("Spotify sign-in expired. Reconnect Spotify and try again.", requiresSignIn: true, ex);
            case APITooManyRequestsException tooMany:
                return new ImportSourceException(
                    $"Spotify is rate-limiting requests. Try again in {Math.Max(1, (int)tooMany.RetryAfter.TotalSeconds)} s.", inner: ex);
            case APIException api when api.Response?.StatusCode == HttpStatusCode.NotFound:
                return new ImportSourceException(
                    "Spotify could not find this playlist. It may be private, deleted, or a Spotify-generated mix the API doesn't share.", inner: ex);
            case APIException api when api.Response?.StatusCode == HttpStatusCode.Forbidden:
                return new ImportSourceException(
                    "Spotify refused access to this playlist. Connect the account that owns or follows it.", requiresSignIn: true, ex);
            case APIException api:
                return new ImportSourceException($"Spotify returned an error: {api.Message}", inner: ex);
            case HttpRequestException:
                return new ImportSourceException("Couldn't reach Spotify. Check your connection and try again.", inner: ex);
            case InvalidOperationException ioe when ioe.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase):
                return new ImportSourceException("Connect Spotify to import this playlist.", requiresSignIn: true, ex);
            case InvalidOperationException ioe:
                return new ImportSourceException(ioe.Message, inner: ex);
            default:
                return new ImportSourceException($"Import failed: {ex.Message}", inner: ex);
        }
    }
}

/// <summary>
/// Sign-in worked in the browser, but Spotify refuses every API call for this account because it
/// isn't allowlisted on the (development-mode) Client ID ORBIT is using.
/// </summary>
public sealed class SpotifyAccountNotAllowlistedException : Exception
{
    public const string UserMessage =
        "Spotify signed you in, but won't let this account use ORBIT's Spotify app yet: apps in Spotify's " +
        "development mode only work for accounts on their allowlist. Ask whoever gave you ORBIT to add the " +
        "email of your Spotify account, or set up your own Spotify app in Settings → Connection (three steps).";

    public SpotifyAccountNotAllowlistedException(Exception? inner = null) : base(UserMessage, inner) { }
}
