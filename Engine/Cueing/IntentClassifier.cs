using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Analysis;

namespace SLSKDONET.Engine.Cueing;

/// <summary>
/// Intents assigned to structural cue points.
/// </summary>
public enum CueIntent
{
    FirstDownbeat,
    MixIn,
    FirstBreakdown,
    FirstDrop,
    Bridge,
    SecondBreakdown,
    SecondDrop,
    MixOutWarning,
    FinalBeat,
    Unknown
}

/// <summary>
/// Classification result with a normalized confidence score [0, 1].
/// </summary>
public readonly record struct ClassificationResult(CueIntent Intent, float Confidence);

/// <summary>
/// Classifies timeline points into DJ-relevant cue intents using weighted multi-feature
/// confidence fusion rather than binary thresholds.
///
/// Feature weights (summing to 1.0):
///   0.30  Spectral Flux Novelty spike strength  — EDM drop signal
///   0.25  Sub-bass return after dropout          — DnB-specific drop signature
///   0.20  RMS energy delta                       — general loudness change
///   0.15  Drum signature change (HPSS)           — pattern shift
///   0.10  Harmonic phase reset (chroma)          — key/chord change
///
/// Essentia AI signals modulate the result:
///   - High Aggressive probability boosts drop confidence
///   - High Voice probability reduces drop confidence (vocal sections ≠ drops)
///   - Low Danceability discounts all structural events
/// </summary>
public sealed class IntentClassifier
{
    // Feature weights
    private const float WNovelty = 0.30f;
    private const float WSubBassReturn = 0.25f;
    private const float WEnergyDelta = 0.20f;
    private const float WDrumChange = 0.15f;
    private const float WHarmonic = 0.10f;

    // Thresholds for drop vs. breakdown classification
    private const float DropThreshold = 0.55f;
    private const float BreakdownThreshold = 0.45f;

    // Proximity window (seconds) for matching events to a candidate timestamp
    private const double ProximitySeconds = 1.5;

    /// <summary>
    /// Classifies a candidate timestamp using the full multi-feature signal set.
    /// </summary>
    public ClassificationResult Classify(
        double timestampSeconds,
        double totalDurationSeconds,
        AnalysisPipelineResult analysis)
    {
        double trackRatio = timestampSeconds / Math.Max(1.0, totalDurationSeconds);

        // ── Positional overrides (track position dominates) ───────────────
        if (timestampSeconds < 3.0)
            return new ClassificationResult(CueIntent.FirstDownbeat, 1.0f);

        if (trackRatio < 0.12)
            return new ClassificationResult(CueIntent.MixIn, 0.80f);

        if (trackRatio > 0.92 && totalDurationSeconds - timestampSeconds < 4.0)
            return new ClassificationResult(CueIntent.FinalBeat, 1.0f);

        if (trackRatio > 0.85)
            return new ClassificationResult(CueIntent.MixOutWarning, 0.78f);

        // ── Feature extraction at this timestamp ──────────────────────────
        float energyDelta = GetEnergyDeltaAtTime(timestampSeconds, analysis.EnergyCurve, totalDurationSeconds);
        float noveltyStrength = GetNoveltyStrengthAtTime(timestampSeconds, analysis.SpectralFluxNovelty, totalDurationSeconds);
        bool hasDrumChange = analysis.DrumSignatureChanges.Any(d => Math.Abs(d - timestampSeconds) < ProximitySeconds);
        bool hasHarmonicReset = analysis.HarmonicResets.Any(h => Math.Abs(h - timestampSeconds) < ProximitySeconds);
        bool hasSubBassReturn = analysis.SubBassReturnTimestamps.Any(r => Math.Abs(r - timestampSeconds) < ProximitySeconds);
        bool hasSubBassDropout = analysis.SubBassDropoutTimestamps.Any(d => Math.Abs(d - timestampSeconds) < ProximitySeconds);

        // ── Weighted drop confidence score ────────────────────────────────
        float dropScore = 0f;
        dropScore += WNovelty * Math.Clamp(noveltyStrength, 0f, 1f);
        dropScore += WSubBassReturn * (hasSubBassReturn ? 1.0f : 0f);
        dropScore += WEnergyDelta * Math.Clamp(energyDelta / 0.40f, 0f, 1f);   // normalize: +0.40 → full score
        dropScore += WDrumChange * (hasDrumChange ? 1.0f : 0f);
        dropScore += WHarmonic * (hasHarmonicReset ? 0.5f : 0f);               // harmonic reset alone ≠ drop

        // ── Weighted breakdown confidence score ───────────────────────────
        float breakdownScore = 0f;
        breakdownScore += WNovelty * Math.Clamp(-noveltyStrength + 0.3f, 0f, 1f); // novelty drops in breakdowns
        breakdownScore += WSubBassReturn * (hasSubBassDropout ? 1.0f : 0f);
        breakdownScore += WEnergyDelta * Math.Clamp(-energyDelta / 0.40f, 0f, 1f);
        breakdownScore += WDrumChange * (hasDrumChange ? 0.8f : 0f);
        breakdownScore += WHarmonic * (hasHarmonicReset ? 0.4f : 0f);

        // ── Essentia AI modulation ────────────────────────────────────────
        // Aggressive mood boosts drop confidence; vocal presence suppresses it
        float aggressiveBoost = Math.Clamp(analysis.EssentiaAggressiveProbability - 0.3f, 0f, 0.15f);
        float vocalPenalty = Math.Clamp(1f - analysis.EssentiaInstrumentalProbability - 0.3f, 0f, 0.20f);
        float danceabilityFactor = Math.Clamp(analysis.EssentiaDanceability, 0.5f, 1.0f);

        dropScore = Math.Clamp((dropScore + aggressiveBoost - vocalPenalty) * danceabilityFactor, 0f, 1f);

        // ── Final classification ──────────────────────────────────────────
        if (dropScore >= DropThreshold)
            return new ClassificationResult(CueIntent.FirstDrop, dropScore);

        if (breakdownScore >= BreakdownThreshold)
            return new ClassificationResult(CueIntent.FirstBreakdown, breakdownScore);

        if (hasHarmonicReset && trackRatio is > 0.35 and < 0.70)
            return new ClassificationResult(CueIntent.Bridge, 0.60f);

        return new ClassificationResult(CueIntent.Unknown, 0f);
    }

    /// <summary>
    /// Batch-classifies an ordered list of candidate timestamps.
    /// Second drops are identified by their position relative to the first detected drop.
    /// </summary>
    public List<(double Timestamp, ClassificationResult Result)> ClassifyAll(
        IEnumerable<double> candidateTimestamps,
        double totalDurationSeconds,
        AnalysisPipelineResult analysis)
    {
        var results = new List<(double, ClassificationResult)>();
        double? firstDropTs = null;

        foreach (var ts in candidateTimestamps.OrderBy(t => t))
        {
            var result = Classify(ts, totalDurationSeconds, analysis);

            // Promote to SecondDrop if a FirstDrop has already been classified
            if (result.Intent == CueIntent.FirstDrop && firstDropTs.HasValue && ts > firstDropTs.Value + 20.0)
                result = new ClassificationResult(CueIntent.SecondDrop, result.Confidence);

            if (result.Intent == CueIntent.FirstDrop && !firstDropTs.HasValue)
                firstDropTs = ts;

            // Promote to SecondBreakdown if after a drop
            if (result.Intent == CueIntent.FirstBreakdown && firstDropTs.HasValue && ts > firstDropTs.Value)
                result = new ClassificationResult(CueIntent.SecondBreakdown, result.Confidence);

            if (result.Intent != CueIntent.Unknown)
                results.Add((ts, result));
        }

        return results;
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static float GetEnergyDeltaAtTime(double ts, float[] energyCurve, double duration)
    {
        if (energyCurve.Length < 2 || duration <= 0) return 0f;
        int idx = (int)Math.Clamp(ts / duration * energyCurve.Length, 1, energyCurve.Length - 1);
        return energyCurve[idx] - energyCurve[idx - 1];
    }

    private static float GetNoveltyStrengthAtTime(double ts, float[] novelty, double duration)
    {
        if (novelty.Length == 0 || duration <= 0) return 0f;
        int idx = (int)Math.Clamp(ts / duration * novelty.Length, 0, novelty.Length - 1);

        // Peak value in a ±0.5s window around ts
        int windowRadius = Math.Max(1, novelty.Length / (int)Math.Max(1, duration * 2));
        int start = Math.Max(0, idx - windowRadius);
        int end = Math.Min(novelty.Length - 1, idx + windowRadius);

        float peak = 0f;
        for (int i = start; i <= end; i++)
            if (novelty[i] > peak) peak = novelty[i];

        return peak;
    }
}
