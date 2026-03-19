using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Utils;
using Soulseek;

namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates the ranking of search results.
/// Refactored to use the deterministic 'TieredTrackComparer' instead of legacy weights.
/// </summary>
public static class ResultSorter
{
    private static readonly HashSet<string> LosslessFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "wav", "aif", "aiff", "ape", "alac"
    };

    private static AppConfig? _config;
    
    /// <summary>
    /// Sets the current configuration for ranking logic.
    /// </summary>
    public static void SetConfig(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    public static AppConfig? GetCurrentConfig() => _config;

    // Legacy method stubs to maintain API compatibility if needed, but they do nothing now
    public static void SetWeights(ScoringWeights weights) { }

    /// <summary>
    /// Calculates the rank/score for a single track against the search criteria.
    /// Useful for streaming scenarios where we want to score on-the-fly.
    /// </summary>
    public static void CalculateRank(Track result, Track searchTrack, FileConditionEvaluator evaluator)
    {
        if (!evaluator.PassesRequired(result))
        {
            result.CurrentRank = 0;
            result.ScoreBreakdown = "Rejected: required conditions";
            return;
        }

        var qualityScore = CalculateQualityScore(result);
        var availabilityScore = CalculateAvailabilityScore(result);
        var metadataScore = CalculateMetadataScore(result, searchTrack);
        var preferredScore = evaluator.ScorePreferred(result);

        var weightedScore =
            (qualityScore * 0.45) +
            (availabilityScore * 0.35) +
            (metadataScore * 0.20);

        var blendedScore = (weightedScore * 0.90) + (preferredScore * 0.10);
        if (result.IsFlagged)
        {
            blendedScore *= 0.70;
        }

        result.CurrentRank = Math.Clamp(blendedScore, 0.0, 1.0);
        result.ScoreBreakdown =
            $"Q:{qualityScore:F2} A:{availabilityScore:F2} M:{metadataScore:F2} Pref:{preferredScore:F2}";
    }

    /// Orders search results based on bitrate.
    /// </summary>
    public static List<Track> OrderResults(
        IEnumerable<Track> results,
        Track searchTrack,
        FileConditionEvaluator? fileConditionEvaluator = null)
    {
        var evaluator = fileConditionEvaluator ?? new FileConditionEvaluator();

        var ranked = results
            .Where(evaluator.PassesRequired)
            .ToList();

        foreach (var track in ranked)
        {
            CalculateRank(track, searchTrack, evaluator);
        }

        return ranked
            .OrderByDescending(t => t.CurrentRank)
            .ThenBy(t => t.QueueLength)
            .ThenByDescending(t => t.HasFreeUploadSlot)
            .ThenByDescending(t => t.Bitrate)
            .ToList();
    }

    private static double CalculateQualityScore(Track result)
    {
        var format = (result.Format ?? result.GetExtension()).ToLowerInvariant();
        var formatScore = LosslessFormats.Contains(format) ? 1.0 : 0.55;

        var bitrateScore = Math.Clamp(result.Bitrate / 1000.0, 0.0, 1.0);
        var sampleRateScore = result.SampleRate.HasValue
            ? Math.Clamp(result.SampleRate.Value / 96000.0, 0.0, 1.0)
            : 0.5;
        var bitDepthScore = result.BitDepth.HasValue
            ? Math.Clamp((result.BitDepth.Value - 16) / 16.0, 0.0, 1.0)
            : 0.5;

        return (formatScore * 0.35) +
               (bitrateScore * 0.40) +
               (sampleRateScore * 0.15) +
               (bitDepthScore * 0.10);
    }

    private static double CalculateAvailabilityScore(Track result)
    {
        var freeSlotScore = result.HasFreeUploadSlot ? 1.0 : 0.35;
        var queueScore = 1.0 - Math.Clamp(result.QueueLength / 50.0, 0.0, 1.0);
        var speedScore = Math.Clamp(result.UploadSpeed / 1_000_000.0, 0.0, 1.0);

        return (freeSlotScore * 0.40) +
               (queueScore * 0.45) +
               (speedScore * 0.15);
    }

    private static double CalculateMetadataScore(Track result, Track searchTrack)
    {
        var query = searchTrack.Title ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return 0.5;

        var candidateTitle = $"{result.Artist} {result.Title}".Trim();
        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            candidateTitle = Path.GetFileNameWithoutExtension(result.Filename ?? string.Empty);
        }

        var similarityScore = StringDistanceUtils.GetNormalizedMatchScore(query, candidateTitle);
        var tokenCoverage = CalculateTokenCoverage(query, candidateTitle);

        return (similarityScore * 0.70) + (tokenCoverage * 0.30);
    }

    private static double CalculateTokenCoverage(string query, string candidate)
    {
        var queryTokens = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 1)
            .Distinct()
            .ToArray();

        if (queryTokens.Length == 0)
            return 0.5;

        var loweredCandidate = candidate.ToLowerInvariant();
        var matched = queryTokens.Count(token => loweredCandidate.Contains(token));
        return (double)matched / queryTokens.Length;
    }
}

