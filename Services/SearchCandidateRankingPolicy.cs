using System;

namespace SLSKDONET.Services;

public static class SearchCandidateRankingPolicy
{
    public static double CalculateFinalScore(
        double matchScore,
        double fitScore,
        double reliability,
        int queueLength)
    {
        var clampedMatch = Math.Clamp(matchScore, 0, 100);
        var clampedFit = Math.Clamp(fitScore, 0, 100);
        var clampedReliability = Math.Clamp(reliability, 0.0, 1.0);
        var normalizedQueue = Math.Max(0, queueLength);

        var reliabilityBonus = (clampedReliability - 0.5) * 10.0;
        var queuePenalty = normalizedQueue > 10
            ? Math.Min(20.0, (normalizedQueue - 10) * 0.25)
            : 0.0;

        return (clampedMatch * 0.60) + (clampedFit * 0.40) + reliabilityBonus - queuePenalty;
    }

    public static double MatchScoreFromRank(double rank)
        => Math.Clamp(rank, 0.0, 1.0) * 100.0;
}