using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>Set-level health outputs (all 0-100; higher is better, except risk).</summary>
public sealed record SetHealthReport(
    double OverallScore,
    double NarrativeScore,
    double TechnicalScore,
    double PerformanceRiskScore);

/// <summary>
/// Phase 1: set-level health scoring. Complements the edge-level <see cref="KnowledgeScoringService"/>
/// by scoring the whole narrative of a SetProject timeline. See
/// DOCS/workstation/knowledge-system-vnext.md §3.3.
/// </summary>
public static class SetHealthService
{
    public static SetHealthReport Compute(
        IReadOnlyList<double> edgeScores,          // 0-100 recScore per adjacent primary-spine pair
        IReadOnlyList<double> slotEnergyTargets,   // 0-100 per slot
        IReadOnlyList<SetSlotRole> slotRoles,      // role per slot
        EventProfileEntity? eventProfile,
        double preparationCompleteness = 1.0)      // 0-1 fraction of slots "set ready"
    {
        var technical = TransitionQuality(edgeScores);
        var narrative = NarrativeScore(slotEnergyTargets, slotRoles, eventProfile);
        var overall = 0.6 * technical + 0.4 * narrative;
        var risk = 100.0 * (1.0 - Clamp01(preparationCompleteness));

        return new SetHealthReport(Clamp(overall), Clamp(narrative), Clamp(technical), Clamp(risk));
    }

    /// <summary>Mean edge quality over the primary spine (0-100); 0 with no edges.</summary>
    public static double TransitionQuality(IReadOnlyList<double> edgeScores)
        => edgeScores is null || edgeScores.Count == 0 ? 0.0 : Clamp(edgeScores.Average());

    public static double NarrativeScore(
        IReadOnlyList<double> energyTargets,
        IReadOnlyList<SetSlotRole> roles,
        EventProfileEntity? eventProfile)
        => 0.4 * EnergyFlow(energyTargets, eventProfile)
         + 0.3 * PeakPlacement(roles)
         + 0.3 * CrowdRecovery(roles);

    /// <summary>Energy-flow fit: 100 = slots follow the event curve exactly; 50 = no curve/neutral.</summary>
    public static double EnergyFlow(IReadOnlyList<double> energyTargets, EventProfileEntity? eventProfile)
    {
        var curve = ParseCurve(eventProfile?.TargetEnergyCurveJson);
        if (curve.Count == 0 || energyTargets is null || energyTargets.Count == 0)
            return 50.0;

        double squaredError = 0.0;
        var n = energyTargets.Count;
        for (var i = 0; i < n; i++)
        {
            var expected = curve[i * curve.Count / n];
            var diff = energyTargets[i] - expected;
            squaredError += diff * diff;
        }
        var rmse = Math.Sqrt(squaredError / n);   // 0..100 root-mean-square error
        return Clamp(100.0 - rmse);
    }

    /// <summary>Fraction of Peak slots positioned in the middle 60% of the set timeline.</summary>
    public static double PeakPlacement(IReadOnlyList<SetSlotRole> roles)
    {
        if (roles is null || roles.Count == 0)
            return 50.0;

        var peaks = roles
            .Select((role, index) => (role, index))
            .Where(x => x.role == SetSlotRole.Peak)
            .ToList();
        if (peaks.Count == 0)
            return 50.0;

        var n = roles.Count;
        var inWindow = peaks.Count(x => x.index >= n * 0.2 && x.index <= n * 0.8);
        return 100.0 * inWindow / peaks.Count;
    }

    /// <summary>Fraction of Peaks followed by a Reset/Atmosphere before the next Peak (or set end).</summary>
    public static double CrowdRecovery(IReadOnlyList<SetSlotRole> roles)
    {
        if (roles is null || roles.Count < 2)
            return 50.0;

        var peakIndices = new List<int>();
        for (var i = 0; i < roles.Count; i++)
            if (roles[i] == SetSlotRole.Peak)
                peakIndices.Add(i);
        if (peakIndices.Count == 0)
            return 50.0;

        var recovered = 0;
        for (var p = 0; p < peakIndices.Count; p++)
        {
            var start = peakIndices[p] + 1;
            var end = p + 1 < peakIndices.Count ? peakIndices[p + 1] : roles.Count;
            for (var i = start; i < end; i++)
            {
                if (roles[i] is SetSlotRole.Reset or SetSlotRole.Atmosphere)
                {
                    recovered++;
                    break;
                }
            }
        }
        return 100.0 * recovered / peakIndices.Count;
    }

    private static IReadOnlyList<double> ParseCurve(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<double>();
        try
        {
            return JsonSerializer.Deserialize<double[]>(json) ?? Array.Empty<double>();
        }
        catch
        {
            return Array.Empty<double>();
        }
    }

    private static double Clamp(double value) => Math.Clamp(value, 0.0, 100.0);
    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
