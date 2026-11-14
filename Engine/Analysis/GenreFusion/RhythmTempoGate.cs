using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SLSKDONET.Engine.Analysis.GenreFusion;

/// <summary>One applied gate factor, for the evidence record (#253 persists it, #254 shows it).</summary>
/// <param name="RuleId">Which rule fired ("dnb-break", "hardcore-4x4"…).</param>
/// <param name="Factor">The factor actually applied after scaling and the hard rules (1 = no change).</param>
/// <param name="Inputs">Short human-readable inputs: tempo octaves and the rhythm scores the rule read.</param>
public sealed record GenreGateEvidence(string RuleId, GenreBucket Bucket, double Factor, double Strength, string Inputs);

/// <summary>
/// One row of the gate table (DOCS/GENRE_ACCURACY_PLAN.md → "Rhythm &amp; tempo gate"). Data, so
/// tuning is a one-line diff with a benchmark table in the PR.
/// </summary>
/// <param name="TempoLo">The rule applies when either tempo octave is inside [TempoLo, TempoHi].</param>
/// <param name="Strength">How strongly the drums match the rule, 0..1.</param>
/// <param name="Factors">Multiplicative factor per bucket at full strength and reliability (&gt; 1 boost, &lt; 1 penalty).</param>
public sealed record GateRule(
    string Id,
    double TempoLo,
    double TempoHi,
    Func<RhythmSignature, double> Strength,
    IReadOnlyDictionary<GenreBucket, double> Factors);

/// <summary>
/// The rhythm &amp; tempo gate (GA6, #251): multiplies bucket scores by what the drums and the
/// octave-folded tempo say, killing the cross-scene mistakes a tempo-only rule can't (hardcore
/// 4x4 at 172 is not DnB; half-time at 140 is not techno; two-step at 132 is not house). Pure.
/// <para>Hard rules:</para>
/// <list type="bullet">
/// <item>Both tempo octaves are evaluated; the refined BPM is never used (it depends on the genre).</item>
/// <item>A factor scales with rule strength × <see cref="RhythmSignature.Reliability"/>: reliability 0 is a no-op.</item>
/// <item>A score never drops below <see cref="MinFactor"/> × its raw value, so nothing is zeroed.</item>
/// <item>A bucket with raw score ≥ <see cref="ProtectedScore"/> leading the runner-up by ≥ <see cref="ProtectedLead"/> is never penalised.</item>
/// <item>Half-time DnB (a 170-ish octave with DnB model mass and a once-per-bar snare) is never pushed towards Dubstep.</item>
/// </list>
/// Replaces the v1 <c>ApplyBpmGuardrails</c>/<c>InferGenreHeuristic</c>; it doesn't stack on them.
/// Interaction: <c>GenreFamilyClassifier</c> still overrides Techno/House labels at 160–190 BPM to
/// Breakbeat for cue generation on old rows. After the gate, a Techno/House winner at that tempo
/// needs a strong four-on-the-floor, which the classifier's override then contradicts only for
/// hardcore-like tracks (Other), so the two never penalise the same bucket twice.
/// </summary>
public static class RhythmTempoGate
{
    public const double MinFactor = 0.15;
    public const double ProtectedScore = 0.85;
    public const double ProtectedLead = 0.25;

    /// <summary>Most a tempo + rhythm guess may claim when no model voted at all.</summary>
    public const float RhythmOnlyMaxConfidence = 0.5f;

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    private static Dictionary<GenreBucket, double> F(params (GenreBucket B, double F)[] f) => f.ToDictionary(x => x.B, x => x.F);

    /// <summary>The gate table, in the plan's order.</summary>
    public static readonly IReadOnlyList<GateRule> Rules = new[]
    {
        // 160–185, backbeat or break snare, no 4x4 kick → DnB.
        new GateRule("dnb-break", 160, 185,
            s => Math.Min(Math.Max(s.BackbeatScore, s.BreakIrregularity), 1 - s.FourOnFloorScore),
            F((GenreBucket.DrumAndBass, 1.6), (GenreBucket.House, 0.3), (GenreBucket.Techno, 0.3), (GenreBucket.Dubstep, 0.3))),

        // 160–185 with a strong 4x4 kick → hardcore/gabber (Other), not DnB.
        // "Strong" 4x4 only: DnB kicks often read as half four-on-the-floor (~0.5), and a partial
        // strength here cost DnB 2 pp recall on the #249 audio sample.
        new GateRule("hardcore-4x4", 160, 185,
            s => s.FourOnFloorScore >= 0.7 ? s.FourOnFloorScore : 0,
            F((GenreBucket.Other, 1.4), (GenreBucket.DrumAndBass, 0.4))),

        // 135–150, once-per-bar snare, no 4x4 → the 140 scene. Techno ×0.4 is an addition to the
        // plan's table: techno always has a kick on every beat, so a half-time pattern without one
        // is evidence against it (#251 acceptance: half-time @140 + model Techno 0.5 → Dubstep).
        new GateRule("dubstep-halftime", 135, 150,
            s => Math.Min(s.HalfTimeScore, 1 - s.FourOnFloorScore),
            F((GenreBucket.Dubstep, 1.6), (GenreBucket.DrumAndBass, 0.3), (GenreBucket.House, 0.3), (GenreBucket.Techno, 0.4))),

        // 135–150 with a strong 4x4 → hard techno (or trance, which the models decide), not dubstep.
        new GateRule("hard-techno-4x4", 135, 150,
            s => s.FourOnFloorScore,
            F((GenreBucket.Techno, 1.2), (GenreBucket.Dubstep, 0.4))),

        // 118–135 with a strong 4x4 → house/techno territory: not DnB or dubstep. House vs techno is the models' call.
        new GateRule("club-4x4", 118, 135,
            s => s.FourOnFloorScore,
            F((GenreBucket.DrumAndBass, 0.3), (GenreBucket.Dubstep, 0.3))),

        // A "two-step" rule (125–140, Other ×1.2, House ×0.6) from the plan was measured and left out:
        // on the #249 audio sample it cost House 5 pp recall by firing on house tracks whose rhythm
        // read as broken, while gaining Other 2 pp (see the #251 PR). Re-add it as data when the
        // rhythm signature gets more reliable on shuffled house.
    };

    /// <summary>Result of gating: the adjusted scores and every factor that was applied.</summary>
    public sealed record GateResult(IReadOnlyDictionary<GenreBucket, double> Scores, IReadOnlyList<GenreGateEvidence> Evidence);

    /// <summary>
    /// Applies the gate to raw per-bucket scores. Returns the scores unchanged (and no evidence) when
    /// there is no signature or its reliability is 0.
    /// </summary>
    public static GateResult Apply(IReadOnlyDictionary<GenreBucket, double> raw, RhythmSignature? rhythm,
        IReadOnlyList<GateRule>? rules = null)
    {
        var scores = raw.ToDictionary(kv => kv.Key, kv => kv.Value);
        var evidence = new List<GenreGateEvidence>();
        if (rhythm is null || rhythm.Reliability <= 0 || rhythm.TempoHigh <= 0 || raw.Count == 0)
            return new GateResult(scores, evidence);

        var reliability = Clamp01(rhythm.Reliability);
        var protectedBucket = ProtectedLeader(raw);
        var halftimeDnb = IsHalftimeDnb(raw, rhythm);
        var inputs = Inputs(rhythm);

        // Multiply all applicable factors per bucket first, then clamp once against the raw score.
        var product = raw.Keys.ToDictionary(b => b, _ => 1.0);
        foreach (var rule in rules ?? Rules)
        {
            if (!InBand(rhythm, rule.TempoLo, rule.TempoHi)) continue;
            var strength = Clamp01(rule.Strength(rhythm));
            if (strength <= 0) continue;

            foreach (var (bucket, full) in rule.Factors)
            {
                if (!raw.ContainsKey(bucket)) continue; // the gate re-weights votes; it doesn't invent them
                var factor = 1 + (full - 1) * strength * reliability;
                if (factor < 1 && bucket == protectedBucket) factor = 1;
                if (factor < 1 && bucket == GenreBucket.DrumAndBass && halftimeDnb) factor = 1;
                if (factor > 1 && bucket == GenreBucket.Dubstep && halftimeDnb) factor = 1;
                if (Math.Abs(factor - 1) < 1e-9) continue;

                product[bucket] *= factor;
                evidence.Add(new GenreGateEvidence(rule.Id, bucket, Math.Round(factor, 4), Math.Round(strength, 3), inputs));
            }
        }

        foreach (var (bucket, f) in product)
            scores[bucket] = raw[bucket] * Math.Max(MinFactor, f);

        return new GateResult(scores, evidence);
    }

    /// <summary>
    /// When no model voted: a low-confidence bucket from rhythm and tempo alone (the strongest
    /// applicable rule's boosted bucket), or null. Never above <see cref="RhythmOnlyMaxConfidence"/>.
    /// </summary>
    public static (GenreBucket Bucket, float Confidence, GenreGateEvidence Evidence)? RhythmOnlyGuess(RhythmSignature? rhythm)
    {
        if (rhythm is null || rhythm.Reliability <= 0 || rhythm.TempoHigh <= 0) return null;

        (GateRule Rule, GenreBucket Bucket, double Score)? best = null;
        foreach (var rule in Rules)
        {
            if (!InBand(rhythm, rule.TempoLo, rule.TempoHi)) continue;
            var strength = Clamp01(rule.Strength(rhythm));
            var boosted = rule.Factors.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
            if (boosted.Count != 1) continue; // a rule that only penalises says what it isn't, not what it is
            var score = strength * Clamp01(rhythm.Reliability);
            if (score > (best?.Score ?? 0.25)) best = (rule, boosted[0], score);
        }

        if (best is not { } b) return null;
        var confidence = (float)Math.Min(RhythmOnlyMaxConfidence, 0.5 * b.Score);
        return (b.Bucket, confidence, new GenreGateEvidence(b.Rule.Id, b.Bucket, 1, Math.Round(b.Score, 3), Inputs(rhythm) + " (rhythm only)"));
    }

    private static bool InBand(RhythmSignature r, double lo, double hi) =>
        (r.TempoLow >= lo && r.TempoLow <= hi) || (r.TempoHigh >= lo && r.TempoHigh <= hi);

    /// <summary>Raw ≥ 0.85 and ≥ 0.25 ahead of the runner-up: the model is sure, the gate doesn't overrule it.</summary>
    private static GenreBucket? ProtectedLeader(IReadOnlyDictionary<GenreBucket, double> raw)
    {
        var ranked = raw.OrderByDescending(kv => kv.Value).ToList();
        if (ranked.Count == 0 || ranked[0].Value < ProtectedScore) return null;
        var runnerUp = ranked.Count > 1 ? ranked[1].Value : 0;
        return ranked[0].Value - runnerUp >= ProtectedLead ? ranked[0].Key : null;
    }

    /// <summary>Half-time DnB: a 165–182 octave, a once-per-bar snare, and real DnB model mass.</summary>
    private static bool IsHalftimeDnb(IReadOnlyDictionary<GenreBucket, double> raw, RhythmSignature r)
    {
        var inDnbOctave = (r.TempoHigh >= 165 && r.TempoHigh <= 182) || (r.TempoLow >= 165 && r.TempoLow <= 182);
        var dnb = raw.GetValueOrDefault(GenreBucket.DrumAndBass);
        var max = raw.Count > 0 ? raw.Values.Max() : 0;
        return inDnbOctave && r.HalfTimeScore >= 0.5 && dnb > 0 && dnb >= 0.5 * max;
    }

    private static string Inputs(RhythmSignature r) => string.Create(CultureInfo.InvariantCulture,
        $"tempo {r.TempoLow:F0}/{r.TempoHigh:F0}, 4x4 {r.FourOnFloorScore:F2}, backbeat {r.BackbeatScore:F2}, " +
        $"half {r.HalfTimeScore:F2}, break {r.BreakIrregularity:F2}, rel {r.Reliability:F2}");
}
