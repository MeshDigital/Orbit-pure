using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Snapping;

/// <summary>
/// Computes structural, harmonic, transient, and energy confidence metrics
/// to evaluate snapping accuracy and guide UI visualization.
/// </summary>
public sealed class SnappingConfidenceMatrix
{
    // Weights summing exactly to 1.0
    public double StructuralWeight { get; set; } = 0.3;
    public double HarmonicWeight { get; set; } = 0.3;
    public double TransientWeight { get; set; } = 0.2;
    public double EnergyWeight { get; set; } = 0.2;

    /// <summary>
    /// Computes the overall confidence score between 0.0 and 1.0.
    /// </summary>
    /// <param name="structuralConfidence">Confidence of the structural phrase placement (e.g. alignment to expected sections).</param>
    /// <param name="harmonicConfidence">Confidence of chroma vector change presence.</param>
    /// <param name="transientConfidence">Proximity weight of transient clusters.</param>
    /// <param name="energyConfidence">Confidence in energy curve delta matches.</param>
    /// <param name="crossTrackConfidence">Cross-track timing consistency factor.</param>
    /// <returns>Normalized confidence score from 0.0 to 1.0.</returns>
    public double ComputeTotalConfidence(
        double structuralConfidence,
        double harmonicConfidence,
        double transientConfidence,
        double energyConfidence,
        double crossTrackConfidence = 1.0)
    {
        double baseConfidence = (StructuralWeight * Math.Clamp(structuralConfidence, 0.0, 1.0)) +
                                (HarmonicWeight * Math.Clamp(harmonicConfidence, 0.0, 1.0)) +
                                (TransientWeight * Math.Clamp(transientConfidence, 0.0, 1.0)) +
                                (EnergyWeight * Math.Clamp(energyConfidence, 0.0, 1.0));

        return Math.Clamp(baseConfidence * Math.Clamp(crossTrackConfidence, 0.0, 1.0), 0.0, 1.0);
    }

    /// <summary>
    /// Estimates transient proximity confidence relative to snapped positions.
    /// </summary>
    public double EstimateTransientConfidence(
        double snappedTime, 
        IReadOnlyList<TransientDataPoint> transients, 
        double toleranceSeconds = 0.08)
    {
        if (transients == null || transients.Count == 0) return 0.0;

        double closestDistance = double.MaxValue;
        foreach (var t in transients)
        {
            double diff = Math.Abs(t.Timestamp - snappedTime);
            if (diff < closestDistance)
            {
                closestDistance = diff;
            }
        }

        double maxThreshold = toleranceSeconds * 3.0; // Max acceptable distance for confidence calculation
        if (closestDistance > maxThreshold) return 0.0;

        return Math.Max(0.0, 1.0 - (closestDistance / maxThreshold));
    }
}
