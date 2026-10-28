using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

/// <summary>
/// Holds the raw numerical output of the structural analysis engine.
/// This is the intermediate representation before cue points are mapped.
/// </summary>
public sealed class StructuralAnalysisResult
{
    /// <summary>Beats Per Minute used to compute phrase boundaries.</summary>
    public float Bpm { get; init; }

    /// <summary>Total track duration in seconds.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>
    /// Timestamps (seconds) for every detected beat.
    /// Derived from BPM with a constant-tempo assumption.
    /// </summary>
    public IReadOnlyList<double> BeatTimestamps { get; init; } = Array.Empty<double>();

    /// <summary>
    /// Timestamps (seconds) of 16-bar phrase boundaries.
    /// The first downbeat of every 16-bar block is a candidate drop alignment point.
    /// </summary>
    public IReadOnlyList<double> PhraseBoundaries { get; init; } = Array.Empty<double>();

    /// <summary>
    /// Normalised RMS energy values sampled over the track.
    /// Each element corresponds to a window of <see cref="EnergyWindowSeconds"/> seconds.
    /// Values are in [0.0, 1.0].
    /// </summary>
    public IReadOnlyList<float> EnergyCurve { get; init; } = Array.Empty<float>();

    /// <summary>Window size (seconds) used when computing <see cref="EnergyCurve"/>.</summary>
    public double EnergyWindowSeconds { get; init; }

    /// <summary>
    /// Detected drop timestamps with associated confidence scores.
    /// Ordered by confidence (highest first).
    /// </summary>
    public IReadOnlyList<(double TimestampSeconds, float Confidence)> Drops { get; init; }
        = Array.Empty<(double, float)>();

    /// <summary>
    /// Higher-level structural sections inferred from phrase windows and local energy.
    /// These are persisted to TrackPhrases and used by the bridge / transition systems.
    /// </summary>
    public IReadOnlyList<StructuralSection> Sections { get; init; } = Array.Empty<StructuralSection>();
}

/// <summary>
/// Typed structural section inferred from a phrase-sized time span.
/// </summary>
public sealed class StructuralSection
{
    public PhraseType Type { get; init; } = PhraseType.Unknown;
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public float EnergyLevel { get; init; }
    public float Confidence { get; init; }
    public int OrderIndex { get; init; }
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// Pure heuristic structural analysis engine.
/// 
/// This engine works entirely from metadata already stored in the database
/// (BPM, duration, pre-computed energy curve) and does NOT perform raw audio
/// decoding.  All computations are deterministic and unit-testable without any
/// I/O or external dependencies.
///
/// Drop Detection Algorithm
/// ========================
/// A "drop" is characterized by:
///   1. A preceding section of rising energy (the build).
///   2. A sudden, sharp peak in energy — a genuine local maximum of the novelty curve,
///      wherever in the track it actually occurs.
///   3. Sustained high energy for at least 8 bars after the peak
///      (anti-false-drop / anti-fake-drop guard).
///
/// The algorithm computes the first-order derivative (novelty curve) of the energy curve,
/// finds local maxima anywhere in it (NOT constrained to land near a mechanically-spaced
/// 16-bar-from-track-start phrase grid — a real track's arrangement doesn't reliably stay
/// aligned to that grid, so gating candidates near it can miss the real drop by several bars),
/// and returns the top N candidates by novelty strength, at least <see cref="SustainedEnergyMinBars"/>
/// bars apart. The drop's timing is derived from the audio; any phrase/approach-cue grid is then
/// derived FROM the drop, not the other way around.
/// </summary>
public sealed class StructuralAnalysisEngine
{
    // --- tuneable constants ------------------------------------------------

    /// <summary>Energy window duration in seconds used for novelty calculation.</summary>
    public const double EnergyWindowSeconds = 1.0;

    /// <summary>
    /// Maximum number of drops returned.  Prevents over-cuing for long tracks.
    /// </summary>
    public const int MaxDrops = 3;

    /// <summary>
    /// After the suspected drop, energy must remain above this fraction of the
    /// peak energy for at least <see cref="SustainedEnergyMinBars"/> bars.
    /// Guards against "fake drops" (build that drops into silence).
    /// </summary>
    public const float SustainedEnergyThresholdFraction = 0.6f;

    /// <summary>Minimum number of bars of sustained energy required after the drop.</summary>
    public const int SustainedEnergyMinBars = 8;

    /// <summary>
    /// Weight applied to <see cref="DipDepth"/>'s pre-drop-silence bonus when ranking drop
    /// candidates. A soft multiplier (score *= 1 + weight*depth), not a hard filter.
    /// </summary>
    public const float DipDepthWeight = 1.0f;

    /// <summary>
    /// Drop candidates within this many seconds of track start are excluded from dip scoring —
    /// the near-silence at a file's true beginning has no real preceding "buildup" baseline to
    /// measure a dip against, which spuriously inflates <see cref="DipDepth"/> there, and no
    /// genuine drop lands this early anyway.
    /// </summary>
    public const double MinBuildupSeconds = 8.0;

    /// <summary>
    /// A pre-drop dip only counts as a genuine "silence" signature if it drops below this
    /// fraction of the track's peak energy — an ordinary, still-fairly-loud wobble that's merely
    /// quieter than its immediate surroundings (common mid-buildup, especially for tracks with a
    /// gradual filter-sweep rise rather than a sharp riser) must not qualify.
    /// </summary>
    public const float AbsoluteSilenceFraction = 0.35f;

    // -----------------------------------------------------------------------

    /// <summary>
    /// Generates phrase boundaries (timestamps) from BPM and duration.
    /// Boundaries are placed at every 16-bar downbeat.
    /// </summary>
    /// <param name="bpm">Beats per minute (must be &gt; 0).</param>
    /// <param name="durationSeconds">Total track duration in seconds.</param>
    /// <param name="beatsPerBar">Time signature numerator (default 4).</param>
    /// <param name="barsPerPhrase">Bars per structural phrase (default 16).</param>
    public static (IReadOnlyList<double> Beats, IReadOnlyList<double> PhraseBoundaries)
        ComputePhraseBoundaries(float bpm, double durationSeconds, int beatsPerBar = 4, int barsPerPhrase = 16)
    {
        if (bpm <= 0) throw new ArgumentOutOfRangeException(nameof(bpm), "BPM must be positive.");
        if (durationSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be positive.");

        double beatInterval = 60.0 / bpm;
        double phraseInterval = beatInterval * beatsPerBar * barsPerPhrase;

        var beats = new List<double>();
        var phrases = new List<double>();

        for (double t = 0; t < durationSeconds; t += beatInterval)
            beats.Add(t);

        // First phrase boundary is at t=0 (start of track).
        for (double t = 0; t < durationSeconds; t += phraseInterval)
            phrases.Add(t);

        return (beats, phrases);
    }

    /// <summary>
    /// Computes a normalised energy novelty curve (first-order forward derivative
    /// of the energy curve) in the same time domain as <paramref name="energyCurve"/>.
    /// Negative derivatives are clamped to zero (we only care about energy increases).
    /// </summary>
    /// <param name="energyCurve">Normalised RMS energy values per window.</param>
    public static IReadOnlyList<float> ComputeNovelty(IReadOnlyList<float> energyCurve)
    {
        if (energyCurve == null) throw new ArgumentNullException(nameof(energyCurve));

        int n = energyCurve.Count;
        var novelty = new float[n];

        for (int i = 1; i < n; i++)
        {
            float delta = energyCurve[i] - energyCurve[i - 1];
            novelty[i] = Math.Max(0f, delta); // keep only positive spikes
        }

        return novelty;
    }

    /// <summary>
    /// Core drop-detection heuristic.
    ///
    /// Steps:
    ///   1. Compute the novelty curve from the energy curve.
    ///   2. Find every local-maximum novelty peak anywhere in the track (not gated to a
    ///      phrase-boundary grid — see class remarks).
    ///   3. Apply the anti-false-drop guard: energy must remain ≥ threshold for
    ///      <see cref="SustainedEnergyMinBars"/> bars after the peak.
    ///   4. Return up to <see cref="MaxDrops"/> candidates sorted by confidence.
    /// </summary>
    /// <param name="energyCurve">Normalised RMS energy values per window (0–1).</param>
    /// <param name="phraseBoundaries">
    /// Phrase boundary timestamps in seconds. No longer used to constrain candidate search — kept
    /// so an empty/absent grid (no usable BPM/duration) still disqualifies detection.
    /// </param>
    /// <param name="bpm">Track BPM (used to compute bar duration for sustain check).</param>
    /// <param name="energyWindowSeconds">Duration of each energy window in seconds.</param>
    public static IReadOnlyList<(double TimestampSeconds, float Confidence)> FindDrops(
        IReadOnlyList<float> energyCurve,
        IReadOnlyList<double> phraseBoundaries,
        float bpm,
        double energyWindowSeconds = EnergyWindowSeconds)
    {
        if (energyCurve == null || energyCurve.Count == 0)
            return Array.Empty<(double, float)>();

        // phraseBoundaries is kept as a required parameter — an empty grid means the track had
        // no usable BPM/duration to build one at all, which still disqualifies drop detection —
        // but it no longer CONSTRAINS where a drop can be found (see below).
        if (phraseBoundaries == null || phraseBoundaries.Count == 0)
            return Array.Empty<(double, float)>();

        var novelty = ComputeNovelty(energyCurve);

        double barDuration = bpm > 0 ? (60.0 / bpm) * 4 : 2.0;
        double sustainDuration = barDuration * SustainedEnergyMinBars;
        float peakEnergy = energyCurve.Max();

        // Candidates are genuine novelty local maxima anywhere in the track, NOT gated to
        // ±PhraseBoundaryToleranceSeconds of a phrase-boundary grid counted from track start.
        // Verified failure mode (library tracks Gancher & Ruin - Rituals, ShockOne - Follow Me,
        // among others): a real track's arrangement doesn't reliably stay aligned to a rigid
        // 16-bar-from-t=0 grid — an odd-length intro, a non-16-bar breakdown, or BPM-detection
        // drift compounding over several minutes can all push the true drop several bars off the
        // grid, past the ±4s tolerance the old boundary-gated search used, causing the real drop
        // to be missed (or the wrong nearby grid slot to be picked) entirely. The drop's timing
        // should be derived from the audio itself; the phrase/approach-cue grid is then derived
        // FROM the drop (see CueGenerationService.BuildDropApproachCueSet), not the other way
        // around.
        var candidates = new List<(double TimestampSeconds, float NoveltyScore, int WindowIndex)>();
        for (int i = 1; i < novelty.Count - 1; i++)
        {
            if (novelty[i] <= 0f) continue;
            if (novelty[i] < novelty[i - 1] || novelty[i] < novelty[i + 1]) continue; // local max only
            candidates.Add((i * energyWindowSeconds, novelty[i], i));
        }

        if (candidates.Count == 0)
            return Array.Empty<(double, float)>();

        // Score each candidate by novelty amplitude with a soft multiplicative bonus for a
        // genuine "pre-drop silence" immediately before it — real EDM production denies the
        // sub-bass/energy right before beat 1 specifically to make the drop land harder, and a
        // candidate preceded by that dip is far more likely the actual drop than a same-strength
        // novelty spike that isn't (verified against real library tracks: Metrik - Simulation had
        // 5 novelty peaks of broadly comparable strength, only one — the real drop — preceded by
        // a sharp energy dip; Brk - Obsession's real second drop, mislabeled by external phrase
        // data, was independently found by this exact scoring as the single highest-ranked
        // candidate). Deliberately a soft bonus, not a hard filter — a track with a rolling
        // bassline or a vocal fill straight into the drop, with no silence at all, must still be
        // able to win on novelty amplitude alone; requiring the dip would repeat the earlier
        // over-eager "DSP corroboration" mistake that rejected perfectly good signals outright.
        // Candidates within MinBuildupSeconds of track start are excluded from dip scoring: the
        // near-silence at a file's true beginning has no real preceding "buildup" baseline to
        // dip below, which spuriously inflates DipDepth there — and no genuine drop lands that
        // early anyway.
        var scoredCandidates = candidates
            .Where(c => c.WindowIndex * energyWindowSeconds >= MinBuildupSeconds)
            .Select(c => (
                c.TimestampSeconds,
                c.WindowIndex,
                Score: c.NoveltyScore * (1f + DipDepthWeight * DipDepth(energyCurve, c.WindowIndex, peakEnergy))))
            .ToList();

        if (scoredCandidates.Count == 0)
            return Array.Empty<(double, float)>();

        scoredCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Apply anti-false-drop guard and build final results
        var drops = new List<(double TimestampSeconds, float Confidence)>();
        float sustainThreshold = peakEnergy * SustainedEnergyThresholdFraction;

        foreach (var (ts, idx, score) in scoredCandidates)
        {
            if (drops.Count >= MaxDrops) break;

            // Check sustained energy for SustainedEnergyMinBars bars after the candidate
            int sustainWindows = (int)Math.Ceiling(sustainDuration / energyWindowSeconds);
            int sustainEnd = Math.Min(energyCurve.Count - 1, idx + sustainWindows);

            bool hasSustainedEnergy = true;
            for (int i = idx + 1; i <= sustainEnd; i++)
            {
                if (energyCurve[i] < sustainThreshold)
                {
                    hasSustainedEnergy = false;
                    break;
                }
            }

            if (!hasSustainedEnergy) continue;

            // Skip candidates too close to an already-accepted drop (< 8 bars)
            bool tooClose = drops.Any(d => Math.Abs(d.TimestampSeconds - ts) < barDuration * 8);
            if (tooClose) continue;

            // Normalise confidence: combined score relative to the global peak combined score
            float maxScore = scoredCandidates[0].Score;
            float confidence = maxScore > 0 ? Math.Min(1f, score / maxScore) : 0f;

            drops.Add((ts, confidence));
        }

        return drops;
    }

    /// <summary>
    /// Measures how much energy dipped immediately before <paramref name="index"/>, relative to
    /// the energy level during the preceding buildup — the "pre-drop silence" signature (HPF
    /// sweeping out the sub-bass, then a sample-drop/silence right before beat 1) real EDM
    /// productions use to make the drop land harder. Returns 0-1; 0 means no dip (energy was flat
    /// or rising right up to the candidate).
    ///
    /// Gated by <paramref name="peakEnergy"/>: a genuine pre-drop silence is quiet in ABSOLUTE
    /// terms (production intentionally cuts to near-nothing), not merely quieter than whatever
    /// the immediately preceding few seconds happened to be. Without this gate, a track with a
    /// gradual filter-sweep buildup (no sharp silence at all — the norm for techno/progressive
    /// styles, which build tension through modulation rather than a dramatic riser) can have an
    /// ordinary, still-fairly-loud wobble mid-buildup score as if it were a real dip purely
    /// because it's a little quieter than the seconds right around it (verified case: ShockOne -
    /// Follow Me had a minor wobble at ~0.62 during an already-loud buildup section outrank the
    /// track's real, later transition, which has no dip at all — just a slow rise with no single
    /// strong transient).
    /// </summary>
    private static float DipDepth(IReadOnlyList<float> energyCurve, int index, float peakEnergy)
    {
        const int PreDipLookbackStart = 1;
        const int PreDipLookbackEnd = 3;
        const int BuildupLookbackStart = 3;
        const int BuildupLookbackEnd = 9;

        int buildupFrom = Math.Max(0, index - BuildupLookbackEnd);
        int buildupTo = Math.Max(0, index - BuildupLookbackStart);
        if (buildupTo <= buildupFrom) return 0f;

        float buildupEnergy = 0f;
        int buildupCount = 0;
        for (int i = buildupFrom; i < buildupTo; i++)
        {
            buildupEnergy += energyCurve[i];
            buildupCount++;
        }
        if (buildupCount == 0 || buildupEnergy <= 0f) return 0f;
        buildupEnergy /= buildupCount;

        int predipFrom = Math.Max(0, index - PreDipLookbackEnd);
        int predipTo = Math.Max(0, index - PreDipLookbackStart + 1);
        if (predipTo <= predipFrom) return 0f;

        float predipEnergy = float.MaxValue;
        for (int i = predipFrom; i < predipTo; i++)
            predipEnergy = Math.Min(predipEnergy, energyCurve[i]);

        if (predipEnergy > AbsoluteSilenceFraction * peakEnergy) return 0f;

        return Math.Clamp((buildupEnergy - predipEnergy) / buildupEnergy, 0f, 1f);
    }

    private static IReadOnlyList<StructuralSection> BuildSections(
        IReadOnlyList<double> phraseBoundaries,
        double durationSeconds,
        IReadOnlyList<float>? energyCurve,
        IReadOnlyList<(double TimestampSeconds, float Confidence)> drops)
    {
        if (durationSeconds <= 0)
            return Array.Empty<StructuralSection>();

        var orderedBoundaries = (phraseBoundaries ?? Array.Empty<double>())
            .Where(t => t >= 0 && t < durationSeconds)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        if (orderedBoundaries.Count == 0 || orderedBoundaries[0] > 0)
            orderedBoundaries.Insert(0, 0);
        if (orderedBoundaries[^1] < durationSeconds)
            orderedBoundaries.Add(durationSeconds);

        int sectionCount = Math.Max(0, orderedBoundaries.Count - 1);
        if (sectionCount == 0)
            return Array.Empty<StructuralSection>();

        float averageEnergy = energyCurve is { Count: > 0 } ? energyCurve.Average() : 0.5f;
        float highThreshold = Math.Clamp(averageEnergy + 0.18f, 0.62f, 0.92f);
        float lowThreshold = Math.Clamp(averageEnergy - 0.15f, 0.10f, 0.42f);

        var sections = new List<StructuralSection>(sectionCount);
        var counters = new Dictionary<PhraseType, int>();

        for (int i = 0; i < sectionCount; i++)
        {
            double start = orderedBoundaries[i];
            double end = orderedBoundaries[i + 1];
            if (end <= start) continue;

            float sectionEnergy = AverageEnergy(energyCurve, start, end, EnergyWindowSeconds);
            var type = DetermineSectionType(
                index: i,
                sectionCount: sectionCount,
                startSeconds: start,
                endSeconds: end,
                energyLevel: sectionEnergy,
                averageEnergy: averageEnergy,
                highThreshold: highThreshold,
                lowThreshold: lowThreshold,
                drops: drops);

            counters[type] = counters.TryGetValue(type, out int existing) ? existing + 1 : 1;
            float confidence = type switch
            {
                PhraseType.Intro or PhraseType.Outro => 0.96f,
                PhraseType.Drop => Math.Clamp(0.75f + ClosestDropConfidence(start, end, drops) * 0.25f, 0f, 1f),
                PhraseType.Build => 0.78f,
                PhraseType.Breakdown => 0.74f,
                PhraseType.Chorus => 0.70f,
                _ => 0.62f,
            };

            // A section classified as a drop can otherwise report only its enclosing phrase-grid
            // slot's boundary as its Start — losing precisely the timestamp FindDrops worked to
            // locate. A slot spans a full phrase (commonly 20-40s), and the real detected drop can
            // sit anywhere inside it, so use the actual matched drop timestamp when one exists.
            double sectionStart = start;
            if (type == PhraseType.Drop)
            {
                var matchedDrop = FindMatchingDrop(start, end, drops);
                if (matchedDrop.HasValue)
                    sectionStart = Math.Clamp(matchedDrop.Value.TimestampSeconds, 0, end - 0.01);
            }

            sections.Add(new StructuralSection
            {
                Type = type,
                StartSeconds = sectionStart,
                EndSeconds = end,
                EnergyLevel = Math.Clamp(sectionEnergy, 0f, 1f),
                Confidence = confidence,
                OrderIndex = i,
                Label = BuildLabel(type, counters[type]),
            });
        }

        return sections;
    }

    private static PhraseType DetermineSectionType(
        int index,
        int sectionCount,
        double startSeconds,
        double endSeconds,
        float energyLevel,
        float averageEnergy,
        float highThreshold,
        float lowThreshold,
        IReadOnlyList<(double TimestampSeconds, float Confidence)> drops)
    {
        if (index == 0) return PhraseType.Intro;
        if (index == sectionCount - 1) return PhraseType.Outro;

        // Strict [start, end) containment — a drop belongs to whichever section actually spans
        // its timestamp, full stop. This used to have extra slop on both ends (a drop up to
        // EnergyWindowSeconds before startSeconds, or up to EnergyWindowSeconds after endSeconds,
        // still "contained"; a separate closeToDrop check softened it further), added back when
        // FindDrops only ever returned a timestamp near a phrase-boundary and needed some
        // rounding tolerance to land in the "right" section. Now that FindDrops is unconstrained
        // and returns the drop's real position, that slop actively misclassifies: a drop at
        // t=45.0 sitting just inside section [44.39, 66.59) would leak backward into the
        // genuinely-low-energy buildup section [22.20, 44.39) too (45.0 < 44.39 + 1.0), tagging a
        // quiet buildup section "Drop" (verified case: Metrik - Simulation). Sections tile the
        // track with no gaps, so every real drop already falls inside exactly one section's
        // strict bounds — no tolerance needed.
        bool containsDrop = drops.Any(d => d.TimestampSeconds >= startSeconds && d.TimestampSeconds < endSeconds);
        bool immediatelyBeforeDrop = !containsDrop && drops.Any(d => d.TimestampSeconds >= endSeconds && d.TimestampSeconds <= endSeconds + (endSeconds - startSeconds));

        if (containsDrop)
            return PhraseType.Drop;

        if (immediatelyBeforeDrop || energyLevel >= (highThreshold * 0.85f))
            return PhraseType.Build;

        if (energyLevel <= lowThreshold)
            return PhraseType.Breakdown;

        if (energyLevel >= highThreshold)
            return PhraseType.Chorus;

        return index % 2 == 0 ? PhraseType.Verse : PhraseType.Bridge;
    }

    private static float AverageEnergy(IReadOnlyList<float>? energyCurve, double startSeconds, double endSeconds, double windowSeconds)
    {
        if (energyCurve == null || energyCurve.Count == 0)
            return 0.5f;

        int startIndex = Math.Clamp((int)Math.Floor(startSeconds / Math.Max(windowSeconds, 0.25)), 0, energyCurve.Count - 1);
        int endIndex = Math.Clamp((int)Math.Ceiling(endSeconds / Math.Max(windowSeconds, 0.25)), startIndex + 1, energyCurve.Count);
        int count = Math.Max(1, endIndex - startIndex);

        float sum = 0f;
        for (int i = startIndex; i < startIndex + count && i < energyCurve.Count; i++)
            sum += energyCurve[i];

        return sum / count;
    }

    private static float ClosestDropConfidence(
        double startSeconds,
        double endSeconds,
        IReadOnlyList<(double TimestampSeconds, float Confidence)> drops)
    {
        // Strict [start, end) — matches DetermineSectionType's containsDrop check, so this always
        // finds the same drop that justified classifying the section "Drop" in the first place,
        // not a different, incorrectly nearby one.
        foreach (var drop in drops)
        {
            if (drop.TimestampSeconds >= startSeconds && drop.TimestampSeconds < endSeconds)
                return drop.Confidence;
        }

        return drops.FirstOrDefault().Confidence;
    }

    /// <summary>
    /// Finds the highest-confidence detected drop that justified classifying [startSeconds,
    /// endSeconds) as a Drop section, so the section's reported Start can be moved to the drop's
    /// real timestamp instead of the section's grid boundary. Uses the same strict [start, end)
    /// window as <see cref="DetermineSectionType"/>'s containsDrop check — a looser window here
    /// once let one real drop event that landed near a shared grid boundary get claimed as the
    /// Start override by BOTH the section ending there
    /// and the section starting there, producing a spurious near-zero-duration "Drop" sliver
    /// immediately next to the real one (verified case: Maduk, Lexurus, RIENK - New Beginning had
    /// exactly this: Drop@88.78 dur=0.01s directly beside the real Drop@89.00 dur=21.98s).
    /// Requiring the match to fall within this section's own [start, end) keeps the override
    /// anchored to a timestamp that's actually inside the slot being relabeled.
    /// </summary>
    private static (double TimestampSeconds, float Confidence)? FindMatchingDrop(
        double startSeconds,
        double endSeconds,
        IReadOnlyList<(double TimestampSeconds, float Confidence)> drops)
    {
        (double TimestampSeconds, float Confidence)? best = null;
        foreach (var drop in drops)
        {
            bool inRange = drop.TimestampSeconds >= startSeconds &&
                           drop.TimestampSeconds < endSeconds;
            if (!inRange) continue;
            if (best == null || drop.Confidence > best.Value.Confidence)
                best = drop;
        }
        return best;
    }

    private static string BuildLabel(PhraseType type, int ordinal)
        => type switch
        {
            PhraseType.Intro => "Intro",
            PhraseType.Outro => "Outro",
            PhraseType.Drop => $"Drop {ordinal}",
            PhraseType.Build => $"Build {ordinal}",
            PhraseType.Breakdown => $"Breakdown {ordinal}",
            PhraseType.Chorus => $"Chorus {ordinal}",
            PhraseType.Verse => $"Verse {ordinal}",
            PhraseType.Bridge => $"Bridge {ordinal}",
            _ => $"Section {ordinal}",
        };

    /// <summary>
    /// Convenience method: runs the complete analysis pipeline from already-computed data.
    /// </summary>
    /// <param name="bpm">Track BPM.</param>
    /// <param name="durationSeconds">Track duration in seconds.</param>
    /// <param name="energyCurve">
    /// Pre-computed normalised RMS energy values.
    /// If empty, phrase boundaries are still returned but drop detection is skipped.
    /// </param>
    public static StructuralAnalysisResult Analyze(
        float bpm,
        double durationSeconds,
        IReadOnlyList<float>? energyCurve = null)
    {
        if (bpm <= 0 || durationSeconds <= 0)
        {
            return new StructuralAnalysisResult
            {
                Bpm = bpm,
                DurationSeconds = durationSeconds,
            };
        }

        var (beats, phrases) = ComputePhraseBoundaries(bpm, durationSeconds);

        IReadOnlyList<(double, float)> drops = Array.Empty<(double, float)>();
        if (energyCurve != null && energyCurve.Count > 0)
        {
            drops = FindDrops(energyCurve, phrases, bpm, EnergyWindowSeconds);
        }

        var sections = BuildSections(phrases, durationSeconds, energyCurve, drops);

        return new StructuralAnalysisResult
        {
            Bpm = bpm,
            DurationSeconds = durationSeconds,
            BeatTimestamps = beats,
            PhraseBoundaries = phrases,
            EnergyCurve = energyCurve ?? Array.Empty<float>(),
            EnergyWindowSeconds = EnergyWindowSeconds,
            Drops = drops,
            Sections = sections,
        };
    }
}
