using System;
using System.Collections.Generic;
using SLSKDONET.Services;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Dubstep / halftime / riddim strategy. Same breakbeat DNA as DnB, but novelty-dominant and
/// shorter sections: 8-bar drops are normal, and the sub-bass impact is a huge but not sufficient
/// signal on its own.
/// </summary>
public sealed class BassMusicAnalysisStrategy : IGenreFamilyAnalysisStrategy
{
    public (float CorrectedBpm, float ConfidencePenalty, string? AnomalyNote) RefineBpm(
        GenreFamilyResult classification, float rawBpm, float confidence)
    {
        // Halftime readings are common (e.g. 140 reported as 70); unlike DnB there is no reliable
        // one-size bracket, so leave the detected tempo alone rather than guessing.
        return (rawBpm, 1f, null);
    }

    public GenreStructurePreset ResolvePreset(FourOnTheFloorSubgenre subgenre)
        => PhraseAlignmentService.Presets["Dubstep"];

    public DropSignalWeights GetSignalWeights()
        => new(SubBassWeight: 0.70f, EnergyJumpWeight: 0.45f, SpectralFluxWeight: 0.80f);

    public DropScoringWeights ScoringWeights => new(
        SubBassReturn: 0.25,
        SpectralFluxNovelty: 0.30,
        SustainedEnergy: 0.05,
        PhraseConfidence: 0.15,
        SectionLength: 0.15,
        CueDetrAgreement: 0.10);

    public IReadOnlyList<(double Time, float Score)> GetFamilySpecificDropCandidates(
        AnalysisPipelineResult analysis, double bpm, double downbeatAnchor)
        => Array.Empty<(double, float)>();

    public IReadOnlyList<double> GetFamilySpecificBreakdownCandidates(
        AnalysisPipelineResult analysis, double bpm, double downbeatAnchor)
        => analysis.SubBassDropoutTimestamps;
}
