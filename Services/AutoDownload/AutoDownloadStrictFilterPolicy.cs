using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AutoDownload;

internal static class AutoDownloadStrictFilterPolicy
{
    private static readonly List<string> DefaultAllowedExtensions =
    [
        "flac",
        "wav",
        "aiff",
        "aif",
        "ape",
        "alac"
    ];

    internal static List<string> ResolveAllowedExtensions(PlaylistTrack track, AppConfig config)
    {
        if (track.Status == TrackStatus.OnHold && config.EnableMp3Fallback)
        {
            return ["mp3"];
        }

        var perTrackFormats = ParseFormats(track.PreferredFormats);
        if (perTrackFormats.Count > 0)
        {
            return perTrackFormats;
        }

        var autoDownloadFormats = ParseFormats(config.AutoDownloadAllowedExtensions);
        if (autoDownloadFormats.Count > 0)
        {
            return autoDownloadFormats;
        }

        var preferredFormats = ParseFormats(config.PreferredFormats);
        if (preferredFormats.Count > 0)
        {
            return preferredFormats;
        }

        return [.. DefaultAllowedExtensions];
    }

    internal static int ResolveMinBitrateKbps(PlaylistTrack track, AppConfig config)
    {
        if (track.MinBitrateOverride.HasValue && track.MinBitrateOverride.Value > 0)
        {
            return track.MinBitrateOverride.Value;
        }

        if (config.AutoDownloadMinBitrateKbps > 0)
        {
            return config.AutoDownloadMinBitrateKbps;
        }

        if (config.PreferredMinBitrate > 0)
        {
            return config.PreferredMinBitrate;
        }

        return 320;
    }

    internal static int? ResolveExpectedDurationSeconds(PlaylistTrack track)
    {
        if (!track.CanonicalDuration.HasValue || track.CanonicalDuration.Value <= 0)
        {
            return null;
        }

        var rawDuration = track.CanonicalDuration.Value;

        // Backward-compatibility guard: some metadata sources may persist duration in seconds.
        var expectedSeconds = rawDuration <= 1000
            ? rawDuration
            : (int)Math.Round(TimeSpan.FromMilliseconds(rawDuration).TotalSeconds);

        if (expectedSeconds <= 0)
        {
            return null;
        }

        // Reject clearly malformed values to avoid over-filtering all candidates.
        if (expectedSeconds > 4 * 60 * 60)
        {
            return null;
        }

        return expectedSeconds;
    }

    private static List<string> ParseFormats(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return [];
        }

        return csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeFormat)
            .Where(IsLikelyFormatToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ParseFormats(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeFormat)
            .Where(IsLikelyFormatToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeFormat(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        normalized = normalized.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '{', '}');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var semicolonSeparator = normalized.IndexOf(';');
        var commaSeparator = normalized.IndexOf(',');
        var metadataSeparator = semicolonSeparator >= 0 && commaSeparator >= 0
            ? Math.Min(semicolonSeparator, commaSeparator)
            : Math.Max(semicolonSeparator, commaSeparator);
        if (metadataSeparator >= 0)
        {
            normalized = normalized[..metadataSeparator].Trim();
            normalized = normalized.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '{', '}');
        }

        var whitespaceSeparator = normalized.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (whitespaceSeparator >= 0)
        {
            normalized = normalized[..whitespaceSeparator].Trim();
            normalized = normalized.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '{', '}');
        }

        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            normalized = normalized[(slashIndex + 1)..].Trim();
            normalized = normalized.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '{', '}');
        }

        normalized = normalized.TrimStart('.');
        if (normalized.StartsWith("x-", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized switch
        {
            "mpeg" => "mp3",
            "mpg" => "mp3",
            "wave" => "wav",
            "vnd.wave" => "wav",
            "mp4" => "m4a",
            _ => normalized
        };
    }

    private static bool IsLikelyFormatToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' or '+');
    }
}