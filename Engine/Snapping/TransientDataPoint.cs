using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Snapping;

/// <summary>
/// Model representing a detected transient point with its clustered acoustic class.
/// </summary>
public sealed class TransientDataPoint
{
    public double Timestamp { get; set; }
    public string ClusterClass { get; set; } = string.Empty; // e.g., "Kick", "Snare", "Perc", "FX"
}

/// <summary>
/// Implements structural snapping onto 32-beat phrases and audits snapped boundaries
/// against transients, harmonic phase tracking resets, and drum signatures.
/// </summary>
public sealed class TransientAwareSnappingEngine
{
    /// <summary>
    /// Proximity tolerance window in seconds (e.g., 80ms) for transient alignment.
    /// </summary>
    public double SnappingToleranceSeconds { get; set; } = 0.08;

    /// <summary>
    /// Snaps a raw time coordinate onto the nearest 32-beat phrase ledger boundary.
    /// </summary>
    /// <param name="rawTime">The unaligned time in seconds.</param>
    /// <param name="bpm">The track tempo in Beats Per Minute.</param>
    /// <param name="downbeatAnchor">The downbeat anchor timestamp (T_0) in seconds.</param>
    /// <returns>Phrase-snapped time coordinate in seconds.</returns>
    public double SnapRawTimeToPhraseLedger(double rawTime, double bpm, double downbeatAnchor)
    {
        if (bpm <= 0) return rawTime;

        // Delta Beat duration: ΔB = 60 / BPM
        double beatDuration = 60.0 / bpm;

        // Nearest beat index: N_beat = ⌊(T_raw - T_0) / ΔB + 0.5⌋
        double nearestBeatIdx = Math.Floor((rawTime - downbeatAnchor) / beatDuration + 0.5);

        // Phrase index (32-beat): N_phrase = ⌊N_beat / 32 + 0.5⌋
        double phraseIdx = Math.Floor(nearestBeatIdx / 32.0 + 0.5);

        // Snapped time: T_0 + (N_phrase * 32 * ΔB)
        double snappedTime = downbeatAnchor + (phraseIdx * 32.0 * beatDuration);

        return Math.Max(0.0, snappedTime);
    }

    /// <summary>
    /// Snaps a raw time coordinate onto the nearest single beat grid line.
    /// </summary>
    public double SnapRawTimeToBeat(double rawTime, double bpm, double downbeatAnchor)
    {
        if (bpm <= 0) return rawTime;

        double beatDuration = 60.0 / bpm;
        double nearestBeatIdx = Math.Floor((rawTime - downbeatAnchor) / beatDuration + 0.5);
        double snappedTime = downbeatAnchor + (nearestBeatIdx * beatDuration);

        return Math.Max(0.0, snappedTime);
    }

    /// <summary>
    /// Validates if a phrase boundary cue matches physical audio changes.
    /// </summary>
    /// <param name="snappedTime">The snapped timeline position in seconds.</param>
    /// <param name="transients">List of detected transients with clustered classes.</param>
    /// <param name="harmonicResets">List of detected harmonic reset timestamps in seconds.</param>
    /// <param name="drumSignatureChanges">List of detected drum pattern switch timestamps in seconds.</param>
    /// <returns>True if the boundary is acoustically and structurally supported.</returns>
    public bool IsCueBoundaryValid(
        double snappedTime, 
        IReadOnlyList<TransientDataPoint> transients,
        IReadOnlyList<double> harmonicResets,
        IReadOnlyList<double> drumSignatureChanges)
    {
        if (transients == null || transients.Count == 0) return true; // Default fallback if no data

        // 1. Kick/Snare transient lands near the snapped boundary
        var nearbyTransients = transients
            .Where(t => Math.Abs(t.Timestamp - snappedTime) <= SnappingToleranceSeconds)
            .ToList();

        bool hasNearTransient = nearbyTransients.Any(t => t.ClusterClass.Equals("Kick", StringComparison.OrdinalIgnoreCase) || 
                                                          t.ClusterClass.Equals("Snare", StringComparison.OrdinalIgnoreCase));

        // 2. The transient cluster changes class across the boundary
        bool hasClusterClassChange = CheckClassChangeAcrossBoundary(snappedTime, transients);

        // 3. Drum signature changes OR harmonic phase resets
        bool hasDrumChangeNear = drumSignatureChanges.Any(t => Math.Abs(t - snappedTime) <= SnappingToleranceSeconds * 2.5);
        bool hasHarmonicResetNear = harmonicResets.Any(t => Math.Abs(t - snappedTime) <= SnappingToleranceSeconds * 2.5);

        return hasNearTransient && hasClusterClassChange && (hasDrumChangeNear || hasHarmonicResetNear);
    }

    private bool CheckClassChangeAcrossBoundary(double boundaryTime, IReadOnlyList<TransientDataPoint> transients)
    {
        // Check local window around the boundary (e.g. 0.5s or half a beat)
        double windowSize = 0.5;

        var classBefore = transients
            .Where(t => t.Timestamp >= boundaryTime - windowSize && t.Timestamp < boundaryTime)
            .OrderByDescending(t => t.Timestamp)
            .Select(t => t.ClusterClass)
            .FirstOrDefault();

        var classAfter = transients
            .Where(t => t.Timestamp >= boundaryTime && t.Timestamp <= boundaryTime + windowSize)
            .OrderBy(t => t.Timestamp)
            .Select(t => t.ClusterClass)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(classBefore) || string.IsNullOrEmpty(classAfter))
        {
            // Fallback: if there are no transients nearby, treat as valid to avoid blocking cues
            return true;
        }

        return !classBefore.Equals(classAfter, StringComparison.OrdinalIgnoreCase);
    }
}
