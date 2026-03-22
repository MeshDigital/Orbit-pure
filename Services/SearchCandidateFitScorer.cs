using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;

namespace SLSKDONET.Services;

public static class SearchCandidateFitScorer
{
    public static double CalculateScore(
        Track candidate,
        TargetMetadata target,
        IEnumerable<string> formatFilter,
        int minBitrate,
        int lengthToleranceSeconds)
    {
        if (candidate.IsFlagged)
            return 0;

        var formats = formatFilter
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();

        double score = 0;

        if (target.HasArtist && ContainsNormalizedToken(candidate.Artist, target.NormalizedArtist))
            score += 20;

        if (target.HasTitle && ContainsNormalizedToken(candidate.Title, target.NormalizedTitle))
            score += 20;

        if (!string.IsNullOrWhiteSpace(target.Album) &&
            (ContainsNormalizedToken(candidate.Album, target.Album) || ContainsNormalizedToken(candidate.Directory, target.Album)))
        {
            score += 5;
        }

        if (target.DurationSeconds.HasValue && candidate.Length.HasValue)
        {
            var durationDelta = Math.Abs(target.DurationSeconds.Value - candidate.Length.Value);
            var tolerance = Math.Max(1, lengthToleranceSeconds);

            if (durationDelta <= 1)
            {
                score += 25;
            }
            else if (durationDelta <= Math.Min(2, tolerance))
            {
                score += 22;
            }
            else if (durationDelta <= tolerance)
            {
                score += 15;
            }
        }

        if (candidate.QueueLength == 0)
        {
            score += 12;
        }
        else if (candidate.QueueLength <= 3)
        {
            score += 8;
        }
        else if (candidate.QueueLength <= 10)
        {
            score += 4;
        }

        if (candidate.HasFreeUploadSlot)
        {
            score += 8;
        }

        var ext = candidate.GetExtension().ToLowerInvariant();
        var format = (candidate.Format ?? string.Empty).ToLowerInvariant();
        var isLossless = ext is "flac" or "wav" or "aif" or "aiff" or "ape" or "alac" ||
                         format is "flac" or "wav" or "aif" or "aiff" or "ape" or "alac";

        if (isLossless)
        {
            score += 6;
        }

        var effectiveMinBitrate = Math.Max(minBitrate, 192);
        if (candidate.Bitrate >= effectiveMinBitrate)
        {
            score += 4;
        }

        if (formats.Length > 0)
        {
            var formatMatched = formats.Contains(format, StringComparer.OrdinalIgnoreCase) ||
                                formats.Contains(ext, StringComparer.OrdinalIgnoreCase);
            if (!formatMatched)
            {
                score *= 0.8;
            }
        }

        return Math.Clamp(score, 0, 100);
    }

    public static bool ContainsNormalizedToken(string? candidate, string expected)
    {
        var normalizedCandidate = StringDistanceUtils.Normalize(candidate ?? string.Empty);
        var normalizedExpected = StringDistanceUtils.Normalize(expected);

        if (string.IsNullOrWhiteSpace(normalizedExpected))
            return true;

        return normalizedCandidate.Contains(normalizedExpected, StringComparison.Ordinal);
    }
}