using System;

namespace SLSKDONET.Services;

public static class SearchCandidateRankingPolicy
{
    public static double CalculateFinalScore(
        double matchScore,
        double fitScore,
        double reliability,
        int queueLength,
        bool hasFreeUploadSlot = false)
    {
        var clampedMatch = Math.Clamp(matchScore, 0, 100);
        var clampedFit = Math.Clamp(fitScore, 0, 100);
        var clampedReliability = Math.Clamp(reliability, 0.0, 1.0);
        var normalizedQueue = Math.Max(0, queueLength);

        var reliabilityBonus = (clampedReliability - 0.5) * 10.0;

        // Peers with a free upload slot will start immediately — apply a strong ranking bonus.
        // This stacks on top of the fit-score HasFreeUploadSlot bonus to push instant-start
        // peers to the top of the candidate list even when their audio quality is comparable.
        var freeSlotBonus = hasFreeUploadSlot ? 12.0 : 0.0;

        // Queue penalty kicks in earlier and scales more steeply than before.
        // Peers with MaxPeerQueueLength already cap at 50, so the effective range is 0-50.
        // Queue 0-5: no penalty; 6-50: up to -22.5 penalty.
        var queuePenalty = normalizedQueue > 5
            ? Math.Min(22.5, (normalizedQueue - 5) * 0.5)
            : 0.0;

        return (clampedMatch * 0.60) + (clampedFit * 0.40) + reliabilityBonus + freeSlotBonus - queuePenalty;
    }

    public static double MatchScoreFromRank(double rank)
        => Math.Clamp(rank, 0.0, 1.0) * 100.0;
}