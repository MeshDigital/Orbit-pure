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
/// <param name="Bpm">The tracker BPM, kept for evidence and callers; the gate reads <paramref name="Rhythm"/>'s folded tempo instead.</param>
/// <param name="Rhythm">The drums' signature (GA4 #248). Null or reliability 0 → the gate is a no-op.</param>
public readonly record struct GenreFusionInput(
    IReadOnlyList<GenreLabelScore> JamendoLabels,
    float Bpm,
    IReadOnlyList<GenreLabelScore>? DiscogsStyles = null,
    RhythmSignature? Rhythm = null);

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
/// <param name="RanHeuristic">True when no model voted and the label is a rhythm + tempo guess (capped confidence).</param>
/// <param name="Gate">Every rhythm &amp; tempo gate factor that was applied.</param>
public sealed record GenreFusionResult(
    string WinnerLabel,
    float Confidence,
    IReadOnlyDictionary<string, double> Distribution,
    IReadOnlyList<GenreFusionCandidate> Candidates,
    bool RanHeuristic,
    GenreBucket Bucket = GenreBucket.Other,
    string? Subgenre = null,
    IReadOnlyList<GenreGateEvidence>? Gate = null);

/// <summary>
/// Pure genre fusion (GA2 #247, Discogs-400 wired in GA5 #250). No DB, no ONNX.
/// <para>
/// Both model heads vote per <see cref="GenreBucket"/> through <see cref="GenreTaxonomy"/>, so a
/// label means the same in every source (Jamendo "breakbeat" is Other, not DnB). A bucket's score
/// from one source is the <b>noisy-OR</b> of its members, <c>1 − Π(1 − wᵢ·pᵢ)</c>: the outputs are
/// independent sigmoids, never softmaxed or summed. Scores under <see cref="ScoreFloor"/> are noise
/// and ignored, otherwise the ~350 non-club Discogs styles would all lift Other a little.
/// Discogs-400 (the fine-grained electronic signal) weighs <see cref="DiscogsWeight"/>, Jamendo
/// <see cref="JamendoWeight"/>. The bucket scores then pass the rhythm &amp; tempo gate
/// (<see cref="RhythmTempoGate"/>, GA6 #251), which replaced the v1 BPM guardrails and heuristic.
/// </para>
/// <see cref="FusionVersion"/> is bumped whenever this scoring changes; GA10 (#255) uses it to
/// detect stale rows.
/// </summary>
public static class GenreFusionEngine
{
    /// <summary>1 = GA2 verbatim port (Jamendo only). 2 = GA5: Discogs-400 + taxonomy buckets + noisy-OR.
    /// 3 = GA6: rhythm &amp; tempo gate replaces the BPM guardrails and heuristic.</summary>
    public const int FusionVersion = 3;

    public const float DiscogsWeight = 0.9f;
    public const float JamendoWeight = 0.6f;

    /// <summary>Sigmoid scores below this are noise and don't vote.</summary>
    public const float ScoreFloor = 0.05f;

    /// <summary>Fuses the input votes. Returns <c>null</c> when nothing survives to a winner.</summary>
    /// <summary>
    /// Monotone map from the raw fused confidence to the accuracy actually observed at that
    /// confidence (GA5/GA6). Fitted on the #249 <c>--audio</c> sample (300 tracks, fixed EffNet input
    /// #270, gate on): raw 0.3–0.5 → 44 % right, 0.5–0.7 → 46 %, 0.7–0.85 → 49 %, 0.85–0.95 → 59 %,
    /// 0.95–1 → 69 % (55 % on the hold-out). Without it 77 % of labels claimed ≥ 0.7 and 31 % of all
    /// tracks were confidently wrong. Even the top bin is under 70 % right, so no fused label is
    /// "confident" yet; refit when the inputs improve (personal head #256, MAEST #257).
    /// </summary>
    public static readonly (float Raw, float Calibrated)[] ConfidenceCalibration =
    {
        (0.00f, 0.25f), (0.40f, 0.44f), (0.60f, 0.46f), (0.775f, 0.49f), (0.90f, 0.59f), (0.975f, 0.69f), (1.00f, 0.69f),
    };

    /// <summary>Piecewise-linear interpolation through <see cref="ConfidenceCalibration"/>.</summary>
    public static float Calibrate(float raw)
    {
        var table = ConfidenceCalibration;
        if (raw <= table[0].Raw) return table[0].Calibrated;
        for (var i = 1; i < table.Length; i++)
        {
            if (raw > table[i].Raw) continue;
            var (x0, y0) = table[i - 1];
            var (x1, y1) = table[i];
            return y0 + (y1 - y0) * (raw - x0) / (x1 - x0);
        }
        return table[^1].Calibrated;
    }

    /// <param name="gateRules">Rule table for the rhythm &amp; tempo gate; null = <see cref="RhythmTempoGate.Rules"/>
    /// (the benchmark passes variants when tuning).</param>
    public static GenreFusionResult? Fuse(GenreFusionInput input, IReadOnlyList<GateRule>? gateRules = null)
    {
        ArgumentNullException.ThrowIfNull(input.JamendoLabels);

        var candidates = new List<GenreFusionCandidate>();

        // 0) Discogs-400 style sigmoids (same ONNX run as the embedding).
        if (input.DiscogsStyles is { Count: > 0 } styles)
            candidates.AddRange(BucketVotes(styles, s => GenreTaxonomy.MapDiscogsStyle(s), "onnx-discogs400", DiscogsWeight));

        // 1) MTG-Jamendo head (87 classes), through the same taxonomy.
        candidates.AddRange(BucketVotes(input.JamendoLabels, s => GenreTaxonomy.MapJamendoLabel(s), "onnx-jamendo", JamendoWeight));

        // 2) Raw per-bucket scores.
        var raw = new Dictionary<GenreBucket, double>();
        var maxConfidence = new Dictionary<GenreBucket, float>();
        var bestSub = new Dictionary<GenreBucket, (string? Sub, float Strength)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Confidence <= 0f) continue;
            var score = candidate.Confidence * candidate.Weight;
            raw[candidate.Bucket] = raw.GetValueOrDefault(candidate.Bucket) + score;
            maxConfidence[candidate.Bucket] = Math.Max(maxConfidence.GetValueOrDefault(candidate.Bucket), candidate.Confidence);
            if (candidate.Subgenre is not null &&
                (!bestSub.TryGetValue(candidate.Bucket, out var current) || score > current.Strength))
                bestSub[candidate.Bucket] = (candidate.Subgenre, score);
        }

        // No model vote at all: a capped rhythm + tempo guess, or nothing.
        if (raw.Count == 0)
        {
            if (RhythmTempoGate.RhythmOnlyGuess(input.Rhythm) is not { } guess) return null;
            var label = GenreTaxonomy.CanonicalDisplay(guess.Bucket, null);
            var vote = new GenreFusionCandidate(label, guess.Confidence, "rhythm", 1f, guess.Bucket);
            return new GenreFusionResult(label, guess.Confidence,
                new Dictionary<string, double> { [label] = Math.Round(guess.Confidence, 4) },
                new[] { vote }, RanHeuristic: true, guess.Bucket, null, new[] { guess.Evidence });
        }

        // 3) Rhythm & tempo gate (GA6). Reliability 0 / no signature → scores unchanged.
        var gate = RhythmTempoGate.Apply(raw, input.Rhythm, gateRules);
        var fused = gate.Scores;

        var ranked = fused.OrderByDescending(kv => kv.Value).ToList();
        var winner = ranked[0];
        var runnerUpScore = ranked.Count > 1 ? ranked[1].Value : 0;
        var margin = Math.Max(0, winner.Value - runnerUpScore);
        // A gate penalty on the winner lowers its confidence; a boost doesn't inflate it.
        var gateFactor = raw[winner.Key] > 0 ? Math.Min(1.0, winner.Value / raw[winner.Key]) : 1.0;
        var winnerConfidence = (maxConfidence.TryGetValue(winner.Key, out var conf) ? conf : 0.5f) * gateFactor;
        var finalConfidence = Calibrate((float)Math.Clamp(winnerConfidence + Math.Min(0.20, margin), 0, 0.99));

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
            finalConfidence, distribution, candidates, RanHeuristic: false, winner.Key, winnerSub, gate.Evidence);
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
}
