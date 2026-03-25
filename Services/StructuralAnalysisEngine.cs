using System;
using System.Collections.Generic;
using System.Linq;

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
///   2. A sudden, sharp peak in energy at a 8-bar or 16-bar phrase boundary.
///   3. Sustained high energy for at least 8 bars after the peak
///      (anti-false-drop / anti-fake-drop guard).
///
/// The algorithm computes the first-order derivative (novelty curve) of the
/// energy curve, finds the highest positive derivatives that align within one
/// bar of a phrase boundary, and returns the top N candidates.
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
    /// A drop candidate must be within this many seconds of a phrase boundary.
    /// One bar at 128 BPM ≈ 1.875 s; we allow two bars of tolerance.
    /// </summary>
    public const double PhraseBoundaryToleranceSeconds = 4.0;

    /// <summary>
    /// After the suspected drop, energy must remain above this fraction of the
    /// peak energy for at least <see cref="SustainedEnergyMinBars"/> bars.
    /// Guards against "fake drops" (build that drops into silence).
    /// </summary>
    public const float SustainedEnergyThresholdFraction = 0.6f;

    /// <summary>Minimum number of bars of sustained energy required after the drop.</summary>
    public const int SustainedEnergyMinBars = 8;

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
    ///   2. Find novelty peaks that fall within <see cref="PhraseBoundaryToleranceSeconds"/>
    ///      of a phrase boundary.
    ///   3. Apply the anti-false-drop guard: energy must remain ≥ threshold for
    ///      <see cref="SustainedEnergyMinBars"/> bars after the peak.
    ///   4. Return up to <see cref="MaxDrops"/> candidates sorted by confidence.
    /// </summary>
    /// <param name="energyCurve">Normalised RMS energy values per window (0–1).</param>
    /// <param name="phraseBoundaries">Phrase boundary timestamps in seconds.</param>
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

        if (phraseBoundaries == null || phraseBoundaries.Count == 0)
            return Array.Empty<(double, float)>();

        var novelty = ComputeNovelty(energyCurve);

        double barDuration = bpm > 0 ? (60.0 / bpm) * 4 : 2.0;
        double sustainDuration = barDuration * SustainedEnergyMinBars;
        float peakEnergy = energyCurve.Max();

        // Build candidate list: novelty peaks near phrase boundaries
        var candidates = new List<(double TimestampSeconds, float NoveltyScore, int WindowIndex)>();

        foreach (double boundary in phraseBoundaries)
        {
            // Find the window index closest to this boundary
            int windowIdx = (int)Math.Round(boundary / energyWindowSeconds);
            if (windowIdx <= 0 || windowIdx >= energyCurve.Count) continue;

            // Search for the local novelty maximum within ±tolerance
            int toleranceWindows = (int)Math.Ceiling(PhraseBoundaryToleranceSeconds / energyWindowSeconds);
            int searchStart = Math.Max(1, windowIdx - toleranceWindows);
            int searchEnd = Math.Min(energyCurve.Count - 1, windowIdx + toleranceWindows);

            float bestNovelty = 0f;
            int bestIdx = windowIdx;
            for (int i = searchStart; i <= searchEnd; i++)
            {
                if (novelty[i] > bestNovelty)
                {
                    bestNovelty = novelty[i];
                    bestIdx = i;
                }
            }

            if (bestNovelty <= 0f) continue;

            double candidateTimestamp = bestIdx * energyWindowSeconds;
            candidates.Add((candidateTimestamp, bestNovelty, bestIdx));
        }

        if (candidates.Count == 0)
            return Array.Empty<(double, float)>();

        // Sort by novelty score descending
        candidates.Sort((a, b) => b.NoveltyScore.CompareTo(a.NoveltyScore));

        // Apply anti-false-drop guard and build final results
        var drops = new List<(double TimestampSeconds, float Confidence)>();
        float sustainThreshold = peakEnergy * SustainedEnergyThresholdFraction;

        foreach (var (ts, noveltyScore, idx) in candidates)
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

            // Normalise confidence: novelty score relative to the global peak novelty
            float maxNovelty = candidates[0].NoveltyScore;
            float confidence = maxNovelty > 0 ? Math.Min(1f, noveltyScore / maxNovelty) : 0f;

            drops.Add((ts, confidence));
        }

        return drops;
    }

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

        return new StructuralAnalysisResult
        {
            Bpm = bpm,
            DurationSeconds = durationSeconds,
            BeatTimestamps = beats,
            PhraseBoundaries = phrases,
            EnergyCurve = energyCurve ?? Array.Empty<float>(),
            EnergyWindowSeconds = EnergyWindowSeconds,
            Drops = drops,
        };
    }
}
