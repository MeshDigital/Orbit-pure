using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Services;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Four-on-the-floor family (House/Techno/Trance/EDM) analysis strategy — narrower per-subgenre
/// BPM brackets, spectral-flux-dominant drop weighting (brickwall limiting washes out raw RMS
/// for these genres), and the "structural stripping" breakdown signal
/// (<see cref="StructuralStrippingEngine"/>) instead of a sub-bass energy dropout, since these
/// genres rarely have the bass fully cut out the way DnB does.
/// </summary>
public sealed class FourOnTheFloorAnalysisStrategy : IGenreFamilyAnalysisStrategy
{
    public (float CorrectedBpm, float ConfidencePenalty, string? AnomalyNote) RefineBpm(
        GenreFamilyResult classification, float rawBpm, float confidence)
    {
        // Four-on-the-floor tracks essentially never have a beat-tracker octave-lock problem —
        // a rigid quarter-note kick is exactly what a general-purpose beat tracker is built to
        // lock onto (confirmed: BeatgridDetectionService already does this reliably; there's no
        // evidence of a real problem here to correct). No-op by design, not an oversight.
        return (rawBpm, 1f, null);
    }

    public GenreStructurePreset ResolvePreset(FourOnTheFloorSubgenre subgenre) => subgenre switch
    {
        FourOnTheFloorSubgenre.House => PhraseAlignmentService.Presets["House"],
        FourOnTheFloorSubgenre.TechHouseTechno => PhraseAlignmentService.Presets["TechHouse"],
        FourOnTheFloorSubgenre.Trance => PhraseAlignmentService.Presets["Trance"],
        _ => PhraseAlignmentService.Presets["EDM"],
    };

    public DropSignalWeights GetSignalWeights()
        => new(SubBassWeight: 0.50f, EnergyJumpWeight: 0.35f, SpectralFluxWeight: 0.85f);

    public IReadOnlyList<(double Time, float Score)> GetFamilySpecificDropCandidates(
        AnalysisPipelineResult analysis, double bpm, double downbeatAnchor)
        => analysis.StructuralStrippingReturnTimestamps
            .Select(t => (t, 0.90f))
            .ToList();

    public IReadOnlyList<double> GetFamilySpecificBreakdownCandidates(
        AnalysisPipelineResult analysis, double bpm, double downbeatAnchor)
        => analysis.StructuralStrippingStartTimestamps;
}
