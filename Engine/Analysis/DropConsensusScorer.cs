using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// One Drop 2 candidate with the features the consensus scorer needs. All signal values are
/// normalized to [0, 1]; <see cref="SectionDurationBars"/> and the context flags come from the
/// phrase/structural layers. <see cref="CueDetrAgreement"/> is null when the track has no cached
/// CUE-DETR result — the scorer then redistributes that weight instead of scoring it as zero.
/// </summary>
public sealed record DropScoreCandidate
{
    public required double TimeSeconds { get; init; }
    public required double SectionDurationBars { get; init; }
    public double PhraseConfidence { get; init; }
    public double SubBassReturnStrength { get; init; }
    public double SpectralFluxNovelty { get; init; }
    public double SustainedPostEnergy { get; init; }
    public bool InsideBuildOrBreakdown { get; init; }
    public double? CueDetrAgreement { get; init; }

    /// <summary>Populated by <see cref="DropConsensusScorer"/> so callers/logs can explain a pick.</summary>
    public double FinalScore { get; set; }
}

public sealed record DropConsensusResult(
    DropScoreCandidate? Drop2,
    IReadOnlyList<DropScoreCandidate> Ranked);

/// <summary>
/// Pure Drop 2 consensus ranking. This does not detect candidates and does not mutate cue output;
/// it scores the candidates produced by the structural/phrase layers and returns the best one with
/// a transparent, genre-weighted score.
/// </summary>
public static class DropConsensusScorer
{
    private const double BuildPenalty = 0.15;
    private const double ShortDropPenalty = 0.40;
    private const double OutroRunwayPenalty = 0.45;
    private const double DropGapPenalty = 0.25;

    public static DropConsensusResult RankAndSelectDrop2(
        IEnumerable<DropScoreCandidate> candidates,
        double drop1TimeSeconds,
        double trackDurationSeconds,
        double bpm,
        GenreStructurePreset preset,
        DropScoringWeights weights)
    {
        if (bpm <= 0) return new DropConsensusResult(null, Array.Empty<DropScoreCandidate>());

        double secondsPerBar = 240.0 / bpm;
        var list = candidates?.ToList() ?? new List<DropScoreCandidate>();
        if (list.Count == 0) return new DropConsensusResult(null, list);

        bool hasCueDetr = list.Any(c => c.CueDetrAgreement.HasValue);
        var effectiveWeights = hasCueDetr ? weights : weights.WithoutCueDetr();

        foreach (var candidate in list)
        {
            if (candidate.TimeSeconds <= drop1TimeSeconds + 5.0)
            {
                candidate.FinalScore = double.NegativeInfinity;
                continue;
            }

            double score = 0;
            score += effectiveWeights.SubBassReturn * Clamp01(candidate.SubBassReturnStrength);
            score += effectiveWeights.SpectralFluxNovelty * Clamp01(candidate.SpectralFluxNovelty);
            score += effectiveWeights.SustainedEnergy * Clamp01(candidate.SustainedPostEnergy);
            score += effectiveWeights.PhraseConfidence * Clamp01(candidate.PhraseConfidence);

            double lengthFactor = preset.MinSubstantialDropBars > 0
                ? Math.Clamp(candidate.SectionDurationBars / preset.MinSubstantialDropBars, 0, 1)
                : 1;
            score += effectiveWeights.SectionLength * lengthFactor;

            if (candidate.CueDetrAgreement is { } cue)
                score += effectiveWeights.CueDetrAgreement * Clamp01(cue);

            // Soft role penalty: a phrase label can be wrong, so only bury it when the DSP
            // evidence is not strong enough to override the label.
            bool stronglyCorroborated = candidate.SubBassReturnStrength >= 0.70
                && candidate.SustainedPostEnergy >= 0.75;
            if (candidate.InsideBuildOrBreakdown && !stronglyCorroborated)
                score -= BuildPenalty;

            double barsAfterDrop1 = (candidate.TimeSeconds - drop1TimeSeconds) / secondsPerBar;
            if (barsAfterDrop1 < preset.MinBarsBetweenDrops)
                score -= DropGapPenalty;

            double barsBeforeEnd = (trackDurationSeconds - candidate.TimeSeconds) / secondsPerBar;
            if (barsBeforeEnd < preset.MinOutroRunwayBars)
                score -= OutroRunwayPenalty;

            if (candidate.SectionDurationBars < preset.MinSubstantialDropBars * 0.5)
                score -= ShortDropPenalty;

            candidate.FinalScore = score;
        }

        var ranked = list
            .Where(c => !double.IsNegativeInfinity(c.FinalScore))
            .OrderByDescending(c => c.FinalScore)
            .ThenBy(c => c.TimeSeconds)
            .ToList();

        return new DropConsensusResult(ranked.FirstOrDefault(), ranked);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}
