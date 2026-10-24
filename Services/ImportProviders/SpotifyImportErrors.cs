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
    public static ImportSourceException Translate(Exception ex)
    {
        switch (ex)
        {
            case ImportSourceException already:
                return already;
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
