namespace SLSKDONET.Models;

/// <summary>
/// Multi-dimensional compatibility score between two tracks, expressed as
/// normalised 0–1 values per dimension plus a composite weighted result.
///
/// Computed by <see cref="Services.Similarity.TrackMatchScorer"/>.
///
/// Design intent:
///   Each sub-score captures a distinct musical compatibility axis so the UI
///   can display not just "how similar are these?" but *why* and *in what way*:
///
///   • <see cref="HarmonyScore"/>  — Camelot wheel compatibility (key/scale)
///   • <see cref="BeatScore"/>    — BPM ratio match (1:1, 2:1, 1:2, 3:2)
///   • <see cref="SoundScore"/>   — Deep audio texture (embedding cosine similarity)
///   • <see cref="DropSonicScore"/> — How similar the two drops feel (energy + spectral)
///
///   <see cref="DoubleDropScore"/> is the most demanding: it requires harmony,
///   tight BPM lock, AND a matching drop character simultaneously — exactly the
///   conditions for an epic double-drop in a live or studio mix.
/// </summary>
public sealed record TrackMatchScore
{
    // ── Per-dimension scores (0-1) ─────────────────────────────────────────

    /// <summary>
    /// Camelot wheel compatibility — 1.0 = same key, 0.0 = maximum clash (6 steps).
    /// Formula: <c>1 - CamelotDistance / 6</c>.
    /// </summary>
    public float HarmonyScore { get; init; }

    /// <summary>
    /// Tempo compatibility including common DJ ratios (1:1, 2:1, 1:2, 3:2, 2:3).
    /// Uses the best-matching ratio via exponential decay: <c>exp(-bestDiff / 3)</c>.
    /// Score approaches 1.0 when BPMs align under the best ratio.
    /// </summary>
    public float BeatScore { get; init; }

    /// <summary>
    /// Overall audio-texture similarity from the 128-D or 2048-D embedding cosine
    /// similarity already computed by the <see cref="SimilarityIndex"/>. Range 0–1.
    /// </summary>
    public float SoundScore { get; init; }

    /// <summary>
    /// Section-level sonic similarity specifically at the Drop/peak moment.
    /// When both tracks have a detected Drop phrase this is computed from the 4-D
    /// section feature vector distance. Falls back to <see cref="SoundScore"/>
    /// when either track lacks phrase data.
    /// </summary>
    public float DropSonicScore { get; init; }

    // ── Composite scores ───────────────────────────────────────────────────

    /// <summary>
    /// Double-drop compatibility score (0–1).
    /// Geometric mean of <see cref="HarmonyScore"/>, a *tighter* BPM match
    /// (±1 BPM tolerance), and <see cref="DropSonicScore"/>.
    ///
    /// The multiplicative form means all three must be simultaneously high —
    /// matching sound with clashing keys, or matching keys with drifting tempo,
    /// will still produce a low score.
    ///
    /// Interpretation:
    ///   ≥ 0.75 → ⚡ "Double drop ready"  (all three axes align strongly)
    ///   ≥ 0.50 → potential double-drop candidate worth auditioning
    ///   &lt; 0.50 → interesting combination but double-dropping would feel rough
    /// </summary>
    public float DoubleDropScore { get; init; }

    /// <summary>
    /// Weighted overall match quality (0–1):
    /// <code>0.35 × SoundScore + 0.25 × HarmonyScore + 0.20 × BeatScore + 0.20 × DropSonicScore</code>
    /// </summary>
    public float OverallScore { get; init; }

    // ── Explanatory labels ─────────────────────────────────────────────────

    /// <summary>Human-readable Camelot compatibility description, e.g. "Same key", "Energy shift +2".</summary>
    public string HarmonyLabel { get; init; } = string.Empty;

    /// <summary>Human-readable BPM relationship, e.g. "Same BPM", "2× (double time)", "±5 BPM".</summary>
    public string BeatLabel { get; init; } = string.Empty;

    /// <summary>
    /// Drop-specific verdict, e.g. "⚡ Double drop ready", "Drop compatible", or empty string.
    /// </summary>
    public string DropLabel { get; init; } = string.Empty;

    // ── BPM metadata ──────────────────────────────────────────────────────

    /// <summary>BPM of the first (seed) track. 0 if unknown.</summary>
    public float BpmA { get; init; }

    /// <summary>BPM of the second (candidate) track. 0 if unknown.</summary>
    public float BpmB { get; init; }

    // ── Convenience flags ─────────────────────────────────────────────────

    /// <summary>True when <see cref="DoubleDropScore"/> ≥ 0.75.</summary>
    public bool IsPotentialDoubleDrop => DoubleDropScore >= 0.75f;

    /// <summary>True when <see cref="HarmonyScore"/> ≥ 0.95 (same Camelot key).</summary>
    public bool IsSameKey => HarmonyScore >= 0.95f;

    /// <summary>Neutral/unknown placeholder — all zeros, no labels.</summary>
    public static TrackMatchScore Unknown { get; } = new();
}
