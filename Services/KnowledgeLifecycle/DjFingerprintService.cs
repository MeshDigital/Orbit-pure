using System;
using System.Collections.Generic;
using System.Text.Json;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): pure math for the per-DJ fingerprint. Computes the
/// <c>djFit</c> filter multiplier (how well a transition type matches this DJ's style) and folds
/// observed transition types into the preference weights via EMA convergence. See
/// DOCS/workstation/KNOWLEDGE_LIFECYCLE_ARCHITECTURE.md §3.
/// </summary>
public static class DjFingerprintService
{
    /// <summary>Weight for a transition type the DJ has no recorded preference for.</summary>
    public const double DefaultTypeWeight = 0.5;

    /// <summary>Multiplier applied when a transition matches an explicit avoidance pattern.</summary>
    public const double AvoidancePenaltyFactor = 0.3;

    /// <summary>EMA α — how fast the fingerprint converges from stated to observed behaviour.</summary>
    public const double ConvergenceAlpha = 0.2;

    /// <summary>0-1 filter: 1.0 = fully aligned with the DJ's style, lower = mismatch/avoided.</summary>
    public static double ComputeDjFit(
        KnowledgeTransitionType type,
        string? preferredTypesJson,
        string? avoidancePatternsJson)
    {
        var weight = PreferredWeight(type, preferredTypesJson);
        if (IsAvoided(type, avoidancePatternsJson))
            weight *= AvoidancePenaltyFactor;
        return Math.Clamp(weight, 0.0, 1.0);
    }

    /// <summary>Reinforces the preference weight for each observed transition type (EMA toward 1.0).</summary>
    public static void UpdateTransitionPreferences(
        DjFingerprintEntity fingerprint,
        IEnumerable<KnowledgeTransitionType> observed)
    {
        var dict = ParseWeightMap(fingerprint.PreferredTransitionTypesJson);
        foreach (var type in observed)
        {
            var key = type.ToString();
            var current = dict.TryGetValue(key, out var weight) ? weight : DefaultTypeWeight;
            dict[key] = Math.Clamp(current + ConvergenceAlpha * (1.0 - current), 0.0, 1.0);
        }
        fingerprint.PreferredTransitionTypesJson = JsonSerializer.Serialize(dict);
    }

    private static double PreferredWeight(KnowledgeTransitionType type, string? json)
    {
        var map = ParseWeightMap(json);
        return map.TryGetValue(type.ToString(), out var weight) ? weight : DefaultTypeWeight;
    }

    private static bool IsAvoided(KnowledgeTransitionType type, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            return list is not null && list.Contains(type.ToString(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, double> ParseWeightMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json)
                ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
