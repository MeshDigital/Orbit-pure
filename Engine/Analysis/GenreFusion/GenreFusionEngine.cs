using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis.GenreFusion;

/// <summary>One model label with its 0–1 score (Jamendo label, Discogs-400 style key…).</summary>
public readonly record struct GenreLabelScore(string Label, float Score);

/// <summary>
/// Everything the fusion step needs, decoupled from the ONNX/DB layers. <c>DiscogsStyles</c> is
/// accepted but ignored until GA5 (#250) wires the 400-style output; the rhythm signature (GA4,
/// #248) and personal head (GA11, #256) plug in here later.
/// </summary>
public readonly record struct GenreFusionInput(
    IReadOnlyList<GenreLabelScore> JamendoLabels,
    float Bpm,
    IReadOnlyList<GenreLabelScore>? DiscogsStyles = null);

/// <summary>A single vote before fusion, with its provenance and weight (kept for evidence).</summary>
public readonly record struct GenreFusionCandidate(
    string Label,
    float Confidence,
    string Source,
    float Weight);

/// <summary>Result of fusing genre votes. Distribution values are the fused scores rounded to 4
/// decimals (<c>Math.Round</c> returns <see cref="double"/>), matching the pre-refactor JSON.</summary>
public sealed record GenreFusionResult(
    string WinnerLabel,
    float Confidence,
    IReadOnlyDictionary<string, double> Distribution,
    IReadOnlyList<GenreFusionCandidate> Candidates,
    bool RanHeuristic);

/// <summary>
/// Pure genre fusion: builds candidates from the live Jamendo head (and, from #250, the
/// Discogs-400 style scores), fuses them with the BPM guardrails, and returns a winner +
/// confidence + top-6 distribution. No DB, no ONNX — the scoring logic was extracted verbatim
/// from <c>AudioAnalysisService.InferAndApplyGenreAsync</c> so results are bit-identical to the
/// pre-refactor behaviour. <see cref="FusionVersion"/> is bumped whenever this scoring changes;
/// GA10 (#255) uses it to detect stale rows.
/// </summary>
public static class GenreFusionEngine
{
    public const int FusionVersion = 1;

    /// <summary>Fuses the input votes. Returns <c>null</c> when nothing survives to a winner.</summary>
    public static GenreFusionResult? Fuse(GenreFusionInput input)
    {
        ArgumentNullException.ThrowIfNull(input.JamendoLabels);

        var candidates = new List<GenreFusionCandidate>();
        var ranHeuristic = false;

        // 0) ONNX-based MTG-Jamendo classifier head — the only live audio genre source today.
        foreach (var vote in input.JamendoLabels.Take(10))
        {
            var canonical = MapJamendoToElectronicGenre(vote.Label);
            if (string.IsNullOrWhiteSpace(canonical)) continue;
            candidates.Add(new GenreFusionCandidate(canonical, vote.Score, "onnx-jamendo", 0.85f));
        }

        // 1) Discogs-400 style scores — wired in GA5 (#250); deliberately ignored for now.

        // 2) Fallback heuristic only when model-derived candidates are missing.
        if (candidates.Count == 0)
        {
            var heuristic = InferGenreHeuristic(input.Bpm);
            if (!string.IsNullOrWhiteSpace(heuristic.Label))
            {
                candidates.Add(new GenreFusionCandidate(heuristic.Label, heuristic.Confidence, "heuristic", 0.35f));
                ranHeuristic = true;
            }
        }

        if (candidates.Count == 0)
            return null;

        // 3) Fuse candidate scores with guardrails + DnB calibration.
        var fusedScores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var maxConfidence = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Label) || candidate.Confidence <= 0f)
                continue;

            var guarded = ApplyBpmGuardrails(candidate.Label, candidate.Confidence, input.Bpm);
            if (guarded <= 0f)
                continue;

            var score = guarded * candidate.Weight;
            if (fusedScores.TryGetValue(candidate.Label, out var current))
                fusedScores[candidate.Label] = current + score;
            else
                fusedScores[candidate.Label] = score;

            if (maxConfidence.TryGetValue(candidate.Label, out var existingMax))
                maxConfidence[candidate.Label] = Math.Max(existingMax, guarded);
            else
                maxConfidence[candidate.Label] = guarded;
        }

        if (fusedScores.Count == 0)
            return null;

        var ranked = fusedScores.OrderByDescending(kv => kv.Value).ToList();

        var winner = ranked[0];
        var runnerUpScore = ranked.Count > 1 ? ranked[1].Value : 0f;
        var margin = Math.Max(0f, winner.Value - runnerUpScore);
        var winnerConfidence = maxConfidence.TryGetValue(winner.Key, out var conf) ? conf : 0.5f;
        var finalConfidence = Math.Clamp(winnerConfidence + Math.Min(0.20f, margin), 0f, 0.99f);

        var distribution = fusedScores
            .OrderByDescending(kv => kv.Value)
            .Take(6)
            .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4));

        return new GenreFusionResult(winner.Key, finalConfidence, distribution, candidates, ranHeuristic);
    }

    private static (string Label, float Confidence) InferGenreHeuristic(float bpm)
    {
        if (bpm >= 160 && bpm <= 182)
            return ("Drum and Bass", 0.62f);

        if (bpm >= 120 && bpm <= 132)
            return ("House", 0.56f);

        if (bpm >= 125 && bpm <= 142)
            return ("Techno", 0.53f);

        return (string.Empty, 0f);
    }

    private static float ApplyBpmGuardrails(string genre, float confidence, float bpm)
    {
        var adjusted = confidence;

        if (genre.Equals("Drum and Bass", StringComparison.OrdinalIgnoreCase))
        {
            if (bpm >= 160 && bpm <= 182) adjusted += 0.10f; // calibration bias for DnB
            if (bpm < 150) adjusted -= 0.15f;
        }

        if (genre.Equals("House", StringComparison.OrdinalIgnoreCase))
        {
            if (bpm >= 118 && bpm <= 132) adjusted += 0.08f;
            if (bpm >= 155) adjusted -= 0.12f;
        }

        if (genre.Equals("Techno", StringComparison.OrdinalIgnoreCase))
        {
            if (bpm >= 124 && bpm <= 145) adjusted += 0.08f;
            if (bpm >= 165) adjusted -= 0.10f;
        }

        return Math.Clamp(adjusted, 0f, 1f);
    }

    private static string MapJamendoToElectronicGenre(string label)
    {
        var canonical = CanonicalizeGenre(label);
        if (!string.IsNullOrWhiteSpace(canonical) &&
            (canonical.Equals("Drum and Bass", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("House", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Techno", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Trance", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Ambient", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Dubstep", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Trap", StringComparison.OrdinalIgnoreCase)))
        {
            return canonical;
        }

        var normalized = new string(label
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (normalized.Contains("drumnbass") || normalized.Contains("breakbeat")) return "Drum and Bass";
        if (normalized.Contains("deephouse") || normalized.Contains("club")) return "House";
        if (normalized.Contains("minimal") || normalized.Equals("idm", StringComparison.OrdinalIgnoreCase)) return "Techno";

        return string.Empty;
    }

    private static string CanonicalizeGenre(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = new string(raw
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (normalized.Contains("dnb") || normalized.Contains("drumandbass"))
            return "Drum and Bass";

        if (normalized.Contains("techhouse"))
            return "Tech House";

        if (normalized.Contains("melodictechno"))
            return "Melodic Techno";

        if (normalized.Contains("techno"))
            return "Techno";

        if (normalized.Contains("house"))
            return "House";

        if (normalized.Contains("trance"))
            return "Trance";

        if (normalized.Contains("dubstep"))
            return "Dubstep";

        if (normalized.Contains("trap"))
            return "Trap";

        if (normalized.Contains("ambient"))
            return "Ambient";

        return raw.Trim();
    }
}
