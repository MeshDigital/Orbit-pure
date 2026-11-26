using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;

namespace SLSKDONET.Services.ImportProviders;

public enum Spotify403Kind
{
    None,
    NotAllowlisted,
    OwnerPremiumRequired,
    PlaylistNotOwned,
    Unknown
}

/// <summary>
/// Classifies Spotify 403 response bodies, scrubs bearer/token fragments, logs each distinct 403
/// body once per session, and tracks the last error kind for <see cref="Diagnostics.FeedbackReportService"/>
/// (Epic #508 / #516 SP5).
/// </summary>
public static class Spotify403Diagnostics
{
    private static readonly ConcurrentDictionary<string, byte> LoggedBodies = new(StringComparer.Ordinal);
    private static readonly Regex TokenScrubRegex = new(
        @"(Bearer\s+)[A-Za-z0-9\-._~+/]+=*|(""access_token""\s*:\s*"")[^""]+("")|(""refresh_token""\s*:\s*"")[^""]+("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static volatile string _lastErrorKind = "none";

    public static string LastErrorKind => _lastErrorKind;

    public static void ResetForTests()
    {
        LoggedBodies.Clear();
        _lastErrorKind = "none";
    }

    public static Spotify403Kind Classify(string? responseBody, string? exceptionMessage = null)
    {
        var combined = $"{exceptionMessage} {responseBody}";
        if (string.IsNullOrWhiteSpace(combined))
            return Spotify403Kind.Unknown;

        if (combined.Contains("not be registered", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("not registered", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("allowlist", StringComparison.OrdinalIgnoreCase))
        {
            return Spotify403Kind.NotAllowlisted;
        }

        if (combined.Contains("premium", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("subscription", StringComparison.OrdinalIgnoreCase))
        {
            return Spotify403Kind.OwnerPremiumRequired;
        }

        if (combined.Contains("playlist", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("collaborat", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return Spotify403Kind.PlaylistNotOwned;
        }

        return Spotify403Kind.Unknown;
    }

    public static string Scrub(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var scrubbed = TokenScrubRegex.Replace(text, m =>
        {
            if (m.Groups[1].Success) return m.Groups[1].Value + "[REDACTED]";
            if (m.Groups[2].Success) return m.Groups[2].Value + "[REDACTED]" + m.Groups[3].Value;
            if (m.Groups[4].Success) return m.Groups[4].Value + "[REDACTED]" + m.Groups[5].Value;
            return "[REDACTED]";
        });
        return scrubbed.Length > 500 ? scrubbed[..500] + "…" : scrubbed;
    }

    public static Spotify403Kind RecordIfForbidden(Exception ex, ILogger? logger = null)
    {
        if (ex is SpotifyAccountNotAllowlistedException)
        {
            _lastErrorKind = Spotify403Kind.NotAllowlisted.ToString();
            return Spotify403Kind.NotAllowlisted;
        }

        if (ex is not APIException api || api.Response?.StatusCode != HttpStatusCode.Forbidden)
            return Spotify403Kind.None;

        var rawBody = api.Response?.Body?.ToString();
        var kind = Classify(rawBody, api.Message);
        _lastErrorKind = kind.ToString();

        var scrubbed = Scrub(rawBody);
        var key = $"{kind}:{scrubbed}";
        if (LoggedBodies.TryAdd(key, 0))
        {
            logger?.LogWarning(
                "Spotify 403 ({Kind}) response body (logged once per session): {Body}",
                kind,
                string.IsNullOrEmpty(scrubbed) ? "<empty>" : scrubbed);
        }

        return kind;
    }
}
