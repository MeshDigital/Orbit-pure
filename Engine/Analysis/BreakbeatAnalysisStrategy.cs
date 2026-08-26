using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Breakbeat-family (DnB/Jungle) analysis strategy — 170-180 BPM bracket, sub-bass-dominant
/// drop weighting, and the resurrected DnB-specific subsystem
/// (<see cref="DnBTransientDetectionService"/> / <see cref="DnBCueNamingService"/>), which was
/// previously fully built, DI-registered, and never called by anything.
/// </summary>
public sealed class BreakbeatAnalysisStrategy : IGenreFamilyAnalysisStrategy
{
    private const float BracketMin = 170f;
    private const float BracketMax = 180f;

    // Lighter than BpmDetectionService's blind genre-agnostic half-time-correction penalty
    // (0.85f) — this correction is genre-corroborated (a DnB genre hint or bracket match already
    // put us here), so it should be trusted more than an unconditional heuristic guess.
    private const float BpmCorrectionConfidencePenalty = 0.92f;

    private readonly DnBTransientDetectionService _dnbTransient;

    public BreakbeatAnalysisStrategy(DnBTransientDetectionService dnbTransient)
    {
        _dnbTransient = dnbTransient ?? throw new ArgumentNullException(nameof(dnbTransient));
    }

    public (float CorrectedBpm, float ConfidencePenalty, string? AnomalyNote) RefineBpm(
        GenreFamilyResult classification, float rawBpm, float confidence)
    {
        if (rawBpm >= BracketMin && rawBpm <= BracketMax)
            return (rawBpm, 1f, null);

        float doubled = rawBpm * 2f;
        if (doubled >= BracketMin && doubled <= BracketMax)
            return (doubled, BpmCorrectionConfidencePenalty,
                $"bpm_breakbeat_family_corrected:{rawBpm:F1}->{doubled:F1}");

        float halved = rawBpm / 2f;
        if (halved >= BracketMin && halved <= BracketMax)
            return (halved, BpmCorrectionConfidencePenalty,
                $"bpm_breakbeat_family_corrected:{rawBpm:F1}->{halved:F1}");

        return (rawBpm, 1f, null);
    }

    public GenreStructurePreset ResolvePreset(FourOnTheFloorSubgenre subgenre)
        => PhraseAlignmentService.Presets["DnB"];

    public DropSignalWeights GetSignalWeights()
        => new(SubBassWeight: 0.85f, EnergyJumpWeight: 0.35f, SpectralFluxWeight: 0.55f);

    public IReadOnlyList<(double Time, float Score)> GetFamilySpecificDropCandidates(
        AnalysisPipelineResult analysis, double bpm, double downbeatAnchor)
        // DnB has no independent drop signal beyond what sub-bass-return/novelty already supply —
        // the resurrected DnB subsystem's real value is breakdown-valley detection (below) and
        // cue labeling, not a competing drop-time guess.
        => Array.Empty<(double, float)>();

    public IReadOnlyList<double> GetFamilySpecificBreakdownCandidates(
        AnalysisPipelineResult analysis, double bpm, double downbeatAnchor)
    {
        if (bpm <= 0 || analysis.DurationSeconds <= 0 || analysis.EnergyCurve.Length == 0)
            return Array.Empty<double>();

        var (_, phraseBoundaries) = StructuralAnalysisEngine.ComputePhraseBoundaries(
            (float)bpm, analysis.DurationSeconds, barsPerPhrase: 32);

        var dnbResult = _dnbTransient.AnalyzeForDnB(
            analysis.EnergyCurve, (float)bpm, energyWindowSeconds: 1.0, phraseBoundaries);

        return dnbResult.PreDropPositions
            .Select(p => (double)p.TimestampSeconds)
            .Where(t => t >= 0 && t <= analysis.DurationSeconds)
            .ToList();
    }
}
