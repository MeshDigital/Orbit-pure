using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis.GenreFusion;

/// <summary>One model label with its 0–1 score (Jamendo label, Discogs-400 style key…).</summary>
public readonly record struct GenreLabelScore(string Label, float Score);

/// <summary>
/// Everything the fusion step needs, decoupled from the ONNX/DB layers. <c>DiscogsStyles</c> are
/// the 400 Discogs-EffNet style sigmoids keyed "Parent---Name" (GA5, #250); null or empty when the
/// row predates them. The rhythm signature (GA4/GA6) and personal head (GA11) plug in here later.
/// </summary>
public readonly record struct GenreFusionInput(
    IReadOnlyList<GenreLabelScore> JamendoLabels,
    float Bpm,
    IReadOnlyList<GenreLabelScore>? DiscogsStyles = null);

/// <summary>A single bucket vote before fusion, with its provenance and weight (kept for evidence).</summary>
/// <param name="Label">Canonical display text of the vote (e.g. "Drum and Bass", "Trance").</param>
/// <param name="Bucket">The bucket voted for.</param>
/// <param name="Subgenre">The strongest member's subgenre, if any (e.g. "Jungle", "Hard Techno").</param>
public readonly record struct GenreFusionCandidate(
    string Label,
    float Confidence,
    string Source,
    float Weight,
    GenreBucket Bucket = GenreBucket.Other,
    string? Subgenre = null);

/// <summary>Result of fusing genre votes.</summary>
/// <param name="WinnerLabel">Stable display text (<see cref="GenreTaxonomy.CanonicalDisplay"/>): "Drum and Bass", "Hard Techno", "Trance"…</param>
/// <param name="Distribution">Fused score per bucket (keyed by its display text, top 6, rounded to 4 decimals).</param>
/// <param name="Bucket">The winning bucket.</param>
/// <param name="Subgenre">The winner's subgenre even when the display text can't carry it (Jungle → "Drum and Bass").</param>
public sealed record GenreFusionResult(
    string WinnerLabel,
    float Confidence,
    IReadOnlyDictionary<string, double> Distribution,
    IReadOnlyList<GenreFusionCandidate> Candidates,
    bool RanHeuristic,
    GenreBucket Bucket = GenreBucket.Other,
    string? Subgenre = null);

/// <summary>
/// Pure genre fusion (GA2 #247, Discogs-400 wired in GA5 #250). No DB, no ONNX.
/// <para>
/// Both model heads vote per <see cref="GenreBucket"/> through <see cref="GenreTaxonomy"/>, so a
/// label means the same in every source (Jamendo "breakbeat" is Other, not DnB). A bucket's score
/// from one source is the <b>noisy-OR</b> of its members, <c>1 − Π(1 − wᵢ·pᵢ)</c>: the outputs are
/// independent sigmoids, never softmaxed or summed. Scores under <see cref="ScoreFloor"/> are noise
/// and ignored, otherwise the ~350 non-club Discogs styles would all lift Other a little.
/// Discogs-400 (the fine-grained electronic signal) weighs <see cref="DiscogsWeight"/>, Jamendo
/// <see cref="JamendoWeight"/>. A track with no model votes falls back to the BPM heuristic; the
/// BPM guardrails stay until the rhythm &amp; tempo gate (GA6 #251) replaces both.
/// </para>
/// <see cref="FusionVersion"/> is bumped whenever this scoring changes; GA10 (#255) uses it to
/// detect stale rows.
/// </summary>
public static class GenreFusionEngine
{
    /// <summary>1 = GA2 verbatim port (Jamendo only). 2 = GA5: Discogs-400 + taxonomy buckets + noisy-OR.</summary>
    public const int FusionVersion = 2;

    public const float DiscogsWeight = 0.9f;
    public const float JamendoWeight = 0.6f;

    /// <summary>Sigmoid scores below this are noise and don't vote.</summary>
    public const float ScoreFloor = 0.05f;

    private const float HeuristicWeight = 0.35f;

    /// <summary>
    /// A tempo-only guess is never "confident": v1 let the DnB heuristic reach 0.92 (0.62 + 0.10
    /// guardrail + 0.20 margin), which the benchmark (#249) showed as hip-hop at 175 BPM labelled DnB.
    /// </summary>
    public const float HeuristicMaxConfidence = 0.45f;

    /// <summary>Fuses the input votes. Returns <c>null</c> when nothing survives to a winner.</summary>
    public static GenreFusionResult? Fuse(GenreFusionInput input)
    {
        ArgumentNullException.ThrowIfNull(input.JamendoLabels);

        var candidates = new List<GenreFusionCandidate>();
        var ranHeuristic = false;

        // 0) Discogs-400 style sigmoids (same ONNX run as the embedding).
        if (input.DiscogsStyles is { Count: > 0 } styles)
            candidates.AddRange(BucketVotes(styles, s => GenreTaxonomy.MapDiscogsStyle(s), "onnx-discogs400", DiscogsWeight));

        // 1) MTG-Jamendo head (87 classes), through the same taxonomy.
        candidates.AddRange(BucketVotes(input.JamendoLabels, s => GenreTaxonomy.MapJamendoLabel(s), "onnx-jamendo", JamendoWeight));

        // 2) Fuse per bucket with the BPM guardrails; the BPM heuristic only when no model vote survives.
        var fused = new Dictionary<GenreBucket, float>();
        var maxConfidence = new Dictionary<GenreBucket, float>();
        var bestSub = new Dictionary<GenreBucket, (string? Sub, float Strength)>();

        Accumulate(candidates, input.Bpm, fused, maxConfidence, bestSub);
        if (fused.Count == 0)
        {
            var heuristic = InferGenreHeuristic(input.Bpm);
            if (!string.IsNullOrWhiteSpace(heuristic.Label))
            {
                var map = GenreTaxonomy.MapFreeText(heuristic.Label);
                var vote = new GenreFusionCandidate(heuristic.Label, heuristic.Confidence, "heuristic", HeuristicWeight, map.Bucket);
                candidates.Add(vote);
                ranHeuristic = true;
                Accumulate(new[] { vote }, input.Bpm, fused, maxConfidence, bestSub);
            }
        }

        if (fused.Count == 0)
            return null;

        var ranked = fused.OrderByDescending(kv => kv.Value).ToList();
        var winner = ranked[0];
        var runnerUpScore = ranked.Count > 1 ? ranked[1].Value : 0f;
        var margin = Math.Max(0f, winner.Value - runnerUpScore);
        var winnerConfidence = maxConfidence.TryGetValue(winner.Key, out var conf) ? conf : 0.5f;
        var finalConfidence = Math.Clamp(winnerConfidence + Math.Min(0.20f, margin), 0f, 0.99f);
        if (ranHeuristic) finalConfidence = Math.Min(finalConfidence, HeuristicMaxConfidence);

        string? SubOf(GenreBucket b) => bestSub.TryGetValue(b, out var s) ? s.Sub : null;

        var distribution = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bucket, score) in ranked.Take(6))
        {
            var key = GenreTaxonomy.CanonicalDisplay(bucket, SubOf(bucket));
            distribution[key] = Math.Round(distribution.GetValueOrDefault(key) + score, 4);
        }

        var winnerSub = SubOf(winner.Key);
        return new GenreFusionResult(
            GenreTaxonomy.CanonicalDisplay(winner.Key, winnerSub),
            finalConfidence, distribution, candidates, ranHeuristic, winner.Key, winnerSub);
    }

    private static void Accumulate(
        IEnumerable<GenreFusionCandidate> candidates, float bpm,
        Dictionary<GenreBucket, float> fused, Dictionary<GenreBucket, float> maxConfidence,
        Dictionary<GenreBucket, (string? Sub, float Strength)> bestSub)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Confidence <= 0f) continue;

            var guarded = ApplyBpmGuardrails(candidate.Bucket, candidate.Confidence, bpm);
            if (guarded <= 0f) continue;

            var score = guarded * candidate.Weight;
            fused[candidate.Bucket] = fused.GetValueOrDefault(candidate.Bucket) + score;
            maxConfidence[candidate.Bucket] = Math.Max(maxConfidence.GetValueOrDefault(candidate.Bucket), guarded);

            if (candidate.Subgenre is not null &&
                (!bestSub.TryGetValue(candidate.Bucket, out var current) || score > current.Strength))
                bestSub[candidate.Bucket] = (candidate.Subgenre, score);
        }
    }

    /// <summary>
    /// One candidate per bucket from one source: noisy-OR over the source's labels that map to the
    /// bucket (junk and sub-floor scores skipped). The subgenre is the strongest member's.
    /// </summary>
    private static IEnumerable<GenreFusionCandidate> BucketVotes(
        IReadOnlyList<GenreLabelScore> scores, Func<string, GenreBucketMapping> map, string source, float weight)
    {
        var miss = new Dictionary<GenreBucket, double>();          // Π(1 − w·p)
        var top = new Dictionary<GenreBucket, (string? Sub, double Strength)>();

        foreach (var s in scores)
        {
            if (string.IsNullOrWhiteSpace(s.Label) || float.IsNaN(s.Score) || s.Score < ScoreFloor) continue;
            var m = map(s.Label);
            if (m.IsJunk || m.Weight <= 0f) continue;

            var p = Math.Clamp(m.Weight * s.Score, 0.0, 1.0);
            miss[m.Bucket] = miss.TryGetValue(m.Bucket, out var current) ? current * (1 - p) : 1 - p;
            if (!top.TryGetValue(m.Bucket, out var best) || p > best.Strength)
                top[m.Bucket] = (m.Subgenre, p);
        }

        foreach (var (bucket, product) in miss)
        {
            var score = (float)(1 - product);
            var sub = top[bucket].Sub;
            yield return new GenreFusionCandidate(GenreTaxonomy.CanonicalDisplay(bucket, sub), score, source, weight, bucket, sub);
        }
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

    /// <summary>The v1 BPM guardrails, now per bucket. Replaced by the rhythm &amp; tempo gate in GA6 (#251).</summary>
    private static float ApplyBpmGuardrails(GenreBucket bucket, float confidence, float bpm)
    {
        var adjusted = confidence;

        if (bucket == GenreBucket.DrumAndBass)
        {
            if (bpm >= 160 && bpm <= 182) adjusted += 0.10f; // calibration bias for DnB
            if (bpm < 150) adjusted -= 0.15f;
        }

        if (bucket == GenreBucket.House)
        {
            if (bpm >= 118 && bpm <= 132) adjusted += 0.08f;
            if (bpm >= 155) adjusted -= 0.12f;
        }

        if (bucket == GenreBucket.Techno)
        {
            if (bpm >= 124 && bpm <= 145) adjusted += 0.08f;
            if (bpm >= 165) adjusted -= 0.10f;
        }

        return Math.Clamp(adjusted, 0f, 1f);
    }
}
