using System.Collections.Generic;
using SLSKDONET.Services;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Per-genre-family signal weighting and BPM/phrase-template adjustments for phrase/drop/cue
/// analysis. Two concrete strategies (<see cref="GenreFamily.Breakbeat"/>,
/// <see cref="GenreFamily.FourOnTheFloor"/>), selected by <see cref="GenreFamilyClassifier"/> —
/// deliberately kept to this one small interface rather than a plugin/registry framework.
/// </summary>
public interface IGenreFamilyAnalysisStrategy
{
    /// <summary>
    /// Validates/corrects a detected BPM against this family's bracket. Returns the (possibly
    /// unchanged) BPM, a confidence multiplier to apply if it was corrected (1f if not), and an
    /// anomaly note to record when a correction fires (null otherwise).
    /// </summary>
    (float CorrectedBpm, float ConfidencePenalty, string? AnomalyNote) RefineBpm(
        GenreFamilyResult classification, float rawBpm, float confidence);

    /// <summary>Resolves this family's phrase bar-count template. <paramref name="subgenre"/> is
    /// ignored by the Breakbeat strategy, which has no subgenre split.</summary>
    GenreStructurePreset ResolvePreset(FourOnTheFloorSubgenre subgenre);

    /// <summary>Per-signal weights for <c>CueGenerationService</c>'s DSP drop-candidate scoring.</summary>
    DropSignalWeights GetSignalWeights();

    /// <summary>
    /// Family-specific DROP candidates to merge into the existing scored-candidate list — the
    /// moment the track actually hits, e.g. FourOnTheFloor's structural-stripping return (kick
    /// re-enters at full force). Breakbeat has no equivalent independent drop signal beyond what
    /// sub-bass-return/novelty already provide, so its implementation returns an empty list —
    /// see <see cref="GetFamilySpecificBreakdownCandidates"/> for where its resurrected DnB
    /// pre-drop-valley signal actually belongs.
    /// </summary>
    IReadOnlyList<(double Time, float Score)> GetFamilySpecificDropCandidates(
        AnalysisPipelineResult analysis, double bpm, double downbeatAnchor);

    /// <summary>
    /// Family-specific BREAKDOWN candidates (plain timestamps, matching
    /// <c>AnalysisPipelineResult.SubBassDropoutTimestamps</c>'s shape) to merge into breakdown
    /// positioning — e.g. DnB's audio-derived pre-drop valley for Breakbeat, structural-stripping
    /// start timestamps for FourOnTheFloor.
    /// </summary>
    IReadOnlyList<double> GetFamilySpecificBreakdownCandidates(
        AnalysisPipelineResult analysis, double bpm, double downbeatAnchor);
}

/// <summary>Per-signal weights for DSP drop-candidate scoring — replaces the old single
/// <c>IsContinuousBasslineGenre</c> boolean + 2-weight scheme with a genre-family-driven 3-way tuple.</summary>
public readonly record struct DropSignalWeights(float SubBassWeight, float EnergyJumpWeight, float SpectralFluxWeight);
