using SLSKDONET.ViewModels.Workstation;

namespace SLSKDONET.Services.Playlist;

/// <summary>
/// Row-friendly (non-canvas) façade over the harmonic/energy compatibility formulas the
/// Workstation "SET PLAN" Flow timeline already uses
/// (<see cref="WorkstationViewModel.ComputeFlowHarmonicCompatibility"/>,
/// <see cref="WorkstationViewModel.ComputeFlowEnergyCompatibility"/>). Deliberately delegates
/// rather than re-implements, so the inline Mix transition badge and the Workstation timeline
/// always agree on how compatible two adjacent tracks are — one scoring implementation, two
/// presentations.
/// </summary>
public static class TrackPairCompatibilityScorer
{
    public readonly record struct PairScore(
        double HarmonicScore, string HarmonicLabel,
        double EnergyScore, string EnergyLabel,
        double CombinedScore);

    public static PairScore Score(
        string? outgoingCamelotKey, string? incomingCamelotKey,
        double? outgoingEnergy, double? incomingEnergy,
        double transitionLengthSeconds = 8.0)
    {
        var harmonic = WorkstationViewModel.ComputeFlowHarmonicCompatibility(outgoingCamelotKey, incomingCamelotKey, semitoneShift: 0);
        var energy = WorkstationViewModel.ComputeFlowEnergyCompatibility(outgoingEnergy, incomingEnergy, transitionLengthSeconds);
        var combined = WorkstationViewModel.ComputeCombinedFlowCompatibilityScore(harmonic.Score, energy.Score);

        return new PairScore(harmonic.Score, harmonic.Label, energy.Score, energy.Label, combined);
    }

    /// <summary>Compatibility-score bucket → the badge color used for both the Flow timeline and the inline Mix badge.</summary>
    public static string CompatibilityColor(double combinedScore) => combinedScore switch
    {
        >= 80 => "#66B8E986",
        >= 60 => "#66FFD58A",
        >= 40 => "#66FFA94B",
        _ => "#66FF6B6B",
    };
}
