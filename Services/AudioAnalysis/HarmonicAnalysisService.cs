using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Pure harmonic analysis layer for A10.2.
/// Uses current Essentia tonal data and chord-stack fallback until richer HPCP streams are exposed.
/// </summary>
public class HarmonicAnalysisService
{
    public virtual HarmonicVector BuildHarmonicVector(EssentiaOutput? output, AudioFeaturesEntity features)
    {
        ArgumentNullException.ThrowIfNull(features);

        var chosenKey = ChooseBestKey(output?.Tonal?.KeyEdma, output?.Tonal?.KeyKrumhansl);
        var primaryKey = !string.IsNullOrWhiteSpace(features.CamelotKey)
            ? features.CamelotKey
            : KeyDetectionService.ToCamelotKey(chosenKey?.Key ?? features.Key, chosenKey?.Scale ?? features.Scale);
        var primaryConfidence = Math.Clamp(chosenKey?.Strength ?? features.KeyConfidence, 0f, 1f);
        var primaryPosition = ResolvePrimaryKeyPosition(primaryKey);

        var secondaries = ResolveSecondaryKeyCandidates(primaryKey, output?.Tonal?.ChordsKey, primaryConfidence);
        var modulationScore = ComputeModulationScore(primaryKey, secondaries.Select(s => s.Key).ToArray(), secondaries.Select(s => s.Confidence).ToArray());
        var stabilityScore = Math.Clamp((1f - modulationScore) * 0.7f + primaryConfidence * 0.3f, 0f, 1f);

        return new HarmonicVector
        {
            PrimaryKey = string.IsNullOrWhiteSpace(primaryKey) ? null : primaryKey,
            PrimaryConfidence = primaryConfidence,
            SecondaryKeys = secondaries.Select(s => s.Key).ToArray(),
            SecondaryConfidences = secondaries.Select(s => s.Confidence).ToArray(),
            PrimaryKeyPositionNormalized = primaryPosition,
            ModulationScore = modulationScore,
            StabilityScore = stabilityScore,
        };
    }

    public float ComputeCompatibility(HarmonicVector? a, HarmonicVector? b)
    {
        if (a is null || b is null || string.IsNullOrWhiteSpace(a.PrimaryKey) || string.IsNullOrWhiteSpace(b.PrimaryKey))
            return 0f;

        if (!TryParseCamelot(a.PrimaryKey, out var aNumber, out var aMode) ||
            !TryParseCamelot(b.PrimaryKey, out var bNumber, out var bMode))
            return 0f;

        var primaryDistance = CamelotDistance(aNumber, bNumber);
        var primaryPrimaryScore = 1f - (primaryDistance / 6f);

        var nearestSecondaryDistances = ResolveNearestDistance(a.PrimaryKey, b.SecondaryKeys)
            .Concat(ResolveNearestDistance(b.PrimaryKey, a.SecondaryKeys))
            .ToArray();
        var primarySecondaryScore = nearestSecondaryDistances.Length > 0
            ? 1f - (nearestSecondaryDistances.Min() / 6f)
            : (primaryDistance == 0 ? 1f : 0f);

        var modeCompatibility = aMode == bMode ? 1f : 0.7f;
        var stabilityPenalty = Math.Max(0f, ((a.ModulationScore + b.ModulationScore) * 0.5f) - ((a.StabilityScore + b.StabilityScore) * 0.25f));
        var creativeTransitionBonus = aMode != bMode && primaryDistance <= 1 ? 0.08f : 0f;

        var score =
            primaryPrimaryScore * 0.45f +
            primarySecondaryScore * 0.25f +
            modeCompatibility * 0.15f +
            ((a.PrimaryConfidence + b.PrimaryConfidence) * 0.5f) * 0.15f -
            stabilityPenalty * 0.15f +
            creativeTransitionBonus;

        return Math.Clamp(score, 0f, 1f);
    }

    private static IEnumerable<float> ResolveNearestDistance(string primaryKey, IReadOnlyList<string> secondaryKeys)
    {
        if (!TryParseCamelot(primaryKey, out var primaryNumber, out _))
            return Array.Empty<float>();

        return secondaryKeys
            .Where(k => TryParseCamelot(k, out _, out _))
            .Select(k =>
            {
                TryParseCamelot(k, out var number, out _);
                return CamelotDistance(primaryNumber, number);
            });
    }

    private static KeyData? ChooseBestKey(KeyData? edma, KeyData? krumhansl)
    {
        if (edma is null) return krumhansl;
        if (krumhansl is null) return edma;
        return edma.Strength >= krumhansl.Strength ? edma : krumhansl;
    }

    private static float ResolvePrimaryKeyPosition(string? camelot)
    {
        if (TryParseCamelot(camelot, out var number, out var mode))
        {
            var ordinal = ((number - 1) * 2) + (mode == 'B' ? 1 : 0);
            return Math.Clamp(ordinal / 23f, 0f, 1f);
        }

        return 0f;
    }

    private static List<(string Key, float Confidence)> ResolveSecondaryKeyCandidates(string? primaryKey, JsonElement? chordsKey, float primaryConfidence)
    {
        var candidates = new List<(string Key, float Confidence)>();

        if (chordsKey is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element)
        {
            candidates.AddRange(ParseSecondaryCandidatesFromChordStack(element));
        }

        if (candidates.Count == 0 && TryParseCamelot(primaryKey, out var number, out var mode))
        {
            candidates.Add((FormatCamelot(RotateCamelot(number, -1), mode), Math.Clamp(primaryConfidence * 0.65f, 0f, 1f)));
            candidates.Add((FormatCamelot(RotateCamelot(number, 1), mode), Math.Clamp(primaryConfidence * 0.6f, 0f, 1f)));
            candidates.Add((FormatCamelot(number, mode == 'A' ? 'B' : 'A'), Math.Clamp(primaryConfidence * 0.55f, 0f, 1f)));
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Key))
            .GroupBy(c => c.Key)
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .Where(c => !string.Equals(c.Key, primaryKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Confidence)
            .Take(3)
            .ToList();
    }

    private static IEnumerable<(string Key, float Confidence)> ParseSecondaryCandidatesFromChordStack(JsonElement element)
    {
        var results = new List<(string Key, float Confidence)>();

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                results.AddRange(ParseChordCandidate(item));
            }
        }
        else
        {
            results.AddRange(ParseChordCandidate(element));
        }

        return results;
    }

    private static IEnumerable<(string Key, float Confidence)> ParseChordCandidate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var camelot = ParseChordLabelToCamelot(element.GetString());
            if (!string.IsNullOrWhiteSpace(camelot))
                return [(camelot, 0.45f)];
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("key", out var keyElement) &&
                element.TryGetProperty("scale", out var scaleElement))
            {
                var camelot = KeyDetectionService.ToCamelotKey(keyElement.GetString() ?? string.Empty, scaleElement.GetString() ?? string.Empty);
                var confidence = element.TryGetProperty("strength", out var strengthElement) && strengthElement.TryGetSingle(out var strength)
                    ? Math.Clamp(strength, 0f, 1f)
                    : 0.45f;
                if (!string.IsNullOrWhiteSpace(camelot))
                    return [(camelot, confidence)];
            }

            if (element.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String)
            {
                var camelot = ParseChordLabelToCamelot(valueElement.GetString());
                var confidence = element.TryGetProperty("probability", out var probabilityElement) && probabilityElement.TryGetSingle(out var probability)
                    ? Math.Clamp(probability, 0f, 1f)
                    : 0.45f;
                if (!string.IsNullOrWhiteSpace(camelot))
                    return [(camelot, confidence)];
            }
        }

        return Array.Empty<(string Key, float Confidence)>();
    }

    private static string ParseChordLabelToCamelot(string? chordLabel)
    {
        if (string.IsNullOrWhiteSpace(chordLabel))
            return string.Empty;

        var trimmed = chordLabel.Trim();
        var token = new string(trimmed.TakeWhile(ch => char.IsLetterOrDigit(ch) || ch == '#' || ch == 'b').ToArray());
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var scale = token.EndsWith("m", StringComparison.OrdinalIgnoreCase) ? "minor" : "major";
        var key = scale == "minor" ? token[..^1] : token;
        return KeyDetectionService.ToCamelotKey(key, scale);
    }

    private static float ComputeModulationScore(string? primaryKey, IReadOnlyList<string> secondaryKeys, IReadOnlyList<float> secondaryConfidences)
    {
        if (string.IsNullOrWhiteSpace(primaryKey) || secondaryKeys.Count == 0)
            return 0f;

        if (!TryParseCamelot(primaryKey, out var primaryNumber, out var primaryMode))
            return 0f;

        var weightedDistance = 0f;
        var weightTotal = 0f;
        var modeSwitchWeight = 0f;

        for (var index = 0; index < secondaryKeys.Count; index++)
        {
            var candidate = secondaryKeys[index];
            var confidence = index < secondaryConfidences.Count ? Math.Clamp(secondaryConfidences[index], 0f, 1f) : 0.35f;
            if (!TryParseCamelot(candidate, out var number, out var mode))
                continue;

            weightedDistance += CamelotDistance(primaryNumber, number) * confidence;
            if (mode != primaryMode)
                modeSwitchWeight += confidence;
            weightTotal += confidence;
        }

        if (weightTotal <= 0f)
            return 0f;

        var normalizedDistance = Math.Clamp((weightedDistance / weightTotal) / 6f, 0f, 1f);
        var normalizedModeSwitch = Math.Clamp(modeSwitchWeight / weightTotal, 0f, 1f);
        return Math.Clamp(normalizedDistance * 0.7f + normalizedModeSwitch * 0.3f, 0f, 1f);
    }

    private static float CamelotDistance(int a, int b)
    {
        var diff = Math.Abs(a - b);
        return Math.Min(diff, 12 - diff);
    }

    private static int RotateCamelot(int number, int delta)
    {
        var rotated = ((number - 1 + delta) % 12 + 12) % 12;
        return rotated + 1;
    }

    private static string FormatCamelot(int number, char mode) => string.Create(CultureInfo.InvariantCulture, $"{number}{mode}");

    private static bool TryParseCamelot(string? camelot, out int number, out char mode)
    {
        number = 0;
        mode = 'A';

        if (string.IsNullOrWhiteSpace(camelot))
            return false;

        var trimmed = camelot.Trim().ToUpperInvariant();
        if (trimmed.Length < 2)
            return false;

        var modeCandidate = trimmed[^1];
        if (modeCandidate is not ('A' or 'B'))
            return false;

        if (!int.TryParse(trimmed[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return false;

        if (number is < 1 or > 12)
            return false;

        mode = modeCandidate;
        return true;
    }
}