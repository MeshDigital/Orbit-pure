namespace SLSKDONET.Services.Playlist;

/// <summary>
/// Energy curve shape applied as a post-ordering pass to the optimized playlist.
/// </summary>
public enum EnergyCurvePattern
{
    /// <summary>No energy shaping — pure harmonic/BPM optimization only.</summary>
    None,

    /// <summary>Build from low to high energy throughout the set.</summary>
    Rising,

    /// <summary>Start low, peak in the middle, end low. Classic wave shape.</summary>
    Wave,

    /// <summary>Maintain steady energy then spike at 2/3 in, return to steady.</summary>
    Peak,
}

/// <summary>
/// Configuration passed to <see cref="PlaylistOptimizer"/> to control weighting and constraints.
/// </summary>
public sealed class PlaylistOptimizerOptions
{
    // ── Edge weight coefficients ───────────────────────────────────────────
    /// <summary>
    /// Multiplier for Camelot key distance. Each step on the wheel = 1 unit.
    /// Default 3.0 makes harmonic compatibility the dominant factor.
    /// </summary>
    public double HarmonicWeight { get; init; } = 3.0;

    /// <summary>
    /// Multiplier for BPM difference. Normalised to 10 BPM = 1 unit by default.
    /// </summary>
    public double TempoWeight { get; init; } = 1.0;

    /// <summary>
    /// Normalisation divisor for BPM differences (default 10 → 10 BPM diff = 1 unit).
    /// </summary>
    public double TempoBpmDivisor { get; init; } = 10.0;

    /// <summary>
    /// Multiplier for EnergyScore difference (scale 1–10).
    /// </summary>
    public double EnergyWeight { get; init; } = 0.5;

    // ── Constraints ────────────────────────────────────────────────────────
    /// <summary>
    /// If set, the optimizer starts the path from this hash.
    /// </summary>
    public string? StartTrackHash { get; init; }

    /// <summary>
    /// If set, the optimizer biases the path to end near this hash (soft constraint).
    /// </summary>
    public string? EndTrackHash { get; init; }

    /// <summary>
    /// Maximum BPM jump allowed between consecutive tracks.
    /// Pairs exceeding this threshold are given a large penalty rather than being excluded
    /// (exclusion would make some playlists unsolvable).
    /// Default 20 BPM.
    /// </summary>
    public double MaxBpmJump { get; init; } = 20.0;

    /// <summary>
    /// Additional cost applied when <see cref="MaxBpmJump"/> is exceeded.
    /// </summary>
    public double BpmJumpPenalty { get; init; } = 10.0;

    // ── Section transition matching ────────────────────────────────────────
    /// <summary>
    /// Weight applied to the outro→intro section feature distance computed by
    /// <see cref="Similarity.SectionVectorService.TransitionCostCached"/>.
    ///
    /// When &gt; 0 the optimizer prefers transitions where the energy/spectral
    /// character of track A's OUTRO closely matches the character of track B's INTRO,
    /// producing smoother mixes at the actual join point.
    ///
    /// Set to 0 to disable section-level matching entirely (pure scalar mode).
    /// Default 2.0 — on par with a 1-step harmonic mismatch on the Camelot wheel.
    /// Only effective if both tracks have phrase-detection data in the database.
    /// </summary>
    public double SectionTransitionWeight { get; init; } = 2.0;

    // ── Post-pass ──────────────────────────────────────────────────────────
    /// <summary>
    /// Desired energy curve shape applied after the greedy ordering pass.
    /// Only affects position of tracks when there is flexibility; it does not override harmonic flow.
    /// </summary>
    public EnergyCurvePattern EnergyCurve { get; init; } = EnergyCurvePattern.None;
}
