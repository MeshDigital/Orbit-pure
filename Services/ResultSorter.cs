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
        result.CurrentRank = Math.Min(1.0, result.Bitrate / 320.0);
        result.ScoreBreakdown = $"Bitrate: {result.Bitrate}";
    }

    /// Orders search results based on bitrate.
    /// </summary>
    public static List<Track> OrderResults(
        IEnumerable<Track> results,
        Track searchTrack,
        FileConditionEvaluator? fileConditionEvaluator = null)
    {
        var sortedList = results.OrderByDescending(t => t.Bitrate).ToList();

        // 2. Assign Rank & Breakdown (Post-Processing)
        foreach (var track in sortedList)
        {
            CalculateRank(track, searchTrack, fileConditionEvaluator!);
            
            // Assign original index if needed (though we just re-ordered them)
            // track.OriginalIndex = ...
        }

        return sortedList;
    }
}

