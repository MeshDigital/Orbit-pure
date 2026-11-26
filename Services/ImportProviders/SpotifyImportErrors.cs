using System;
using System.Net;
using System.Net.Http;
using SpotifyAPI.Web;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>Outcome of the public-page scraper fallback when the Spotify API refused or wasn't used.</summary>
public enum SpotifyScraperOutcome
{
    NotAttempted,
    Empty,
    PrivateOrUnavailable,
    PersonalisedMix,
    NetworkError
}

/// <summary>
/// Turns Spotify API / network failures into a short, actionable <see cref="ImportSourceException"/>
/// for the import preview's error state, instead of the raw exception text.
/// </summary>
public static class SpotifyImportErrors
{
    public const string PersonalisedMixMessage =
        "This looks like a personalised Spotify mix (such as Daily Mix or Discover Weekly). Spotify doesn't " +
        "share these with apps or public pages — copy the tracks into a playlist you own, then import that.";

    public const string ConnectedNotOwnedMessage =
        "Spotify only shares playlists you own or collaborate on with apps, and this one isn't publicly readable " +
        "(or it's a personalised mix like Daily Mix). Make it public, or copy its tracks into a playlist you own.";

    /// <summary>
    /// Detects Spotify-generated personalised mix links/IDs (e.g. Daily Mix, Discover Weekly,
    /// Release Radar, Daylist, On Repeat — IDs starting with <c>37i9dQZF1E</c>).
    /// </summary>
    public static bool IsPersonalisedMixInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        return input.Contains("37i9dQZF1E", StringComparison.OrdinalIgnoreCase)
            || input.Contains("discover-weekly", StringComparison.OrdinalIgnoreCase)
            || input.Contains("daily-mix", StringComparison.OrdinalIgnoreCase)
            || input.Contains("release-radar", StringComparison.OrdinalIgnoreCase);
    }

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

    public static ImportSourceException Translate(
        Exception ex,
        bool isAuthenticated = false,
        SpotifyScraperOutcome scraperOutcome = SpotifyScraperOutcome.NotAttempted,
        string? input = null)
    {
        bool isMix = scraperOutcome == SpotifyScraperOutcome.PersonalisedMix || IsPersonalisedMixInput(input);

        switch (ex)
        {
            case ImportSourceException already:
                return already;
            case SpotifyAccountNotAllowlistedException notAllowed:
                return new ImportSourceException(notAllowed.Message, requiresSignIn: false, inner: ex, canRetry: false);
            case APIException api when IsAccountNotAllowlisted(api):
                // Reconnecting can't fix this, so no "Connect Spotify" or Retry button.
                return new ImportSourceException(SpotifyAccountNotAllowlistedException.UserMessage, requiresSignIn: false, inner: ex, canRetry: false);
            case APIUnauthorizedException:
                return new ImportSourceException("Spotify sign-in expired. Reconnect Spotify and try again.", requiresSignIn: true, inner: ex, canRetry: false);
            case APITooManyRequestsException tooMany:
                return new ImportSourceException(
                    $"Spotify is rate-limiting requests. Try again in {Math.Max(1, (int)tooMany.RetryAfter.TotalSeconds)} s.", requiresSignIn: false, inner: ex, canRetry: true);
            case APIException api when api.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound:
                if (api.Response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var kind = Spotify403Diagnostics.RecordIfForbidden(api);
                    if (kind == Spotify403Kind.OwnerPremiumRequired)
                    {
                        return new ImportSourceException(
                            "The Spotify account that owns this app needs an active Premium subscription (Spotify rule since Feb 2026). Public playlists can still be imported by link without signing in.",
                            requiresSignIn: false, inner: ex, canRetry: false);
                    }
                }
                if (isMix)
                    return new ImportSourceException(PersonalisedMixMessage, requiresSignIn: false, inner: ex, canRetry: false);
                if (scraperOutcome == SpotifyScraperOutcome.NetworkError)
                    return new ImportSourceException("Spotify refused API access and the public page couldn't be reached. Check your connection and try again.", requiresSignIn: false, inner: ex, canRetry: true);
                if (isAuthenticated)
                    return new ImportSourceException(ConnectedNotOwnedMessage, requiresSignIn: false, inner: ex, canRetry: false);
                return api.Response.StatusCode == HttpStatusCode.NotFound
                    ? new ImportSourceException(
                        "Spotify could not find this playlist. It may be private, deleted, or a Spotify-generated mix the API doesn't share.",
                        requiresSignIn: false, inner: ex, canRetry: false)
                    : new ImportSourceException(
                        "Spotify refused access to this playlist. Make it public, or connect Spotify if you own or collaborate on it.",
                        requiresSignIn: true, inner: ex, canRetry: false);
            case APIException api:
                return new ImportSourceException($"Spotify returned an error: {api.Message}", requiresSignIn: false, inner: ex, canRetry: true);
            case HttpRequestException:
                return new ImportSourceException("Couldn't reach Spotify. Check your connection and try again.", requiresSignIn: false, inner: ex, canRetry: true);
            case InvalidOperationException ioe when ioe.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase):
                return new ImportSourceException("Connect Spotify to import this playlist.", requiresSignIn: true, inner: ex, canRetry: false);
            case InvalidOperationException ioe:
                return new ImportSourceException(ioe.Message, requiresSignIn: false, inner: ex, canRetry: false);
            default:
                return new ImportSourceException($"Import failed: {ex.Message}", requiresSignIn: false, inner: ex, canRetry: true);
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
