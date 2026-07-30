using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Extracts BPM and confidence from Essentia JSON output, applies
/// median-smoothing over the BPM histogram, and populates
/// <see cref="AudioFeaturesEntity"/> fields.
/// </summary>
public sealed class BpmDetectionService
{
    // Any BPM below this threshold is considered a potential half-time artefact.
    private const float MinReasonableBpm = 60f;
    // BPM above this is a potential double-time artefact. 200 clipped genuinely fast genres
    // (hardcore/gabber commonly run 170-220) so real tracks in that range were silently halved;
    // 220 covers them while still folding clear double-time misreads (e.g. 240 → 120).
    private const float MaxReasonableBpm = 220f;

    // ── Breakbeat/DnB/Jungle half-time correction ──────────────────────────
    // A beat tracker very commonly locks onto the half-time "footfall" groove of a DnB/Jungle
    // track (true tempo ~160-182) and reports ~78-91 instead. That range sits comfortably inside
    // [MinReasonableBpm, MaxReasonableBpm] above, so the simple range clamp never catches it.
    //
    // The original design for this looked for bimodal BPM-histogram energy at ~2x the chosen
    // candidate as corroborating evidence. Validated against real essentia_streaming_extractor_music
    // output on confirmed half-time-misdetected DnB tracks, that signal never actually appears —
    // this build's beat tracker fully commits to one octave internally and the histogram carries
    // no residual trace of the rejected candidate. Onset rate is the one signal that reliably
    // discriminated a real breakbeat misdetection (~4.4-4.6/sec on confirmed DnB tracks) from
    // the normalisation path leaving genuinely-in-range tempos alone, so it's the sole trigger here.
    //
    // Known open risk: a genuinely slow, percussion-dense track natively in this BPM range (e.g.
    // half-time dubstep/trap authored at ~70-90 BPM) could also clear this onset-rate threshold
    // and get wrongly doubled. There was no such track available to validate against this pass —
    // watch AudioFeaturesEntity.AnomaliesJson ("bpm_halftime_corrected") for over-eager corrections
    // on non-breakbeat genres and retune OnsetRateCorroborationThreshold if that turns up.
    private const float HalfTimeBandMin = 70f;
    private const float HalfTimeBandMax = 95f;
    private const float OnsetRateCorroborationThreshold = 4.0f; // onsets/sec
    private const float HalfTimeCorrectionConfidencePenalty = 0.85f;

    /// <summary>
    /// Extracts BPM from <paramref name="essentiaOutput"/> using histogram median smoothing
    /// and writes the result into <paramref name="target"/>.
    /// </summary>
    public void Detect(EssentiaOutput essentiaOutput, AudioFeaturesEntity target)
    {
        ArgumentNullException.ThrowIfNull(essentiaOutput);
        ArgumentNullException.ThrowIfNull(target);

        var rhythm = essentiaOutput.Rhythm;
        if (rhythm == null) return;

        float rawBpm = rhythm.Bpm;
        // rhythm.BpmConfidence is not populated by the current essentia_streaming_extractor_music
        // build (always 0) — fall back to the histogram's winning-peak weight, which this build
        // does emit and which is a legitimate confidence proxy (how dominant the chosen BPM is
        // relative to the full histogram's mass). That field itself is occasionally 0 even when
        // the histogram clearly shows a dominant peak (an observed Essentia quirk, not something
        // under our control) — when both are 0, fall back further to a self-computed concentration
        // (winning bin's weight over total histogram mass) so a real, meaningful value is used
        // rather than silently reporting confidence 0.
        float confidence = rhythm.BpmConfidence > 0f ? rhythm.BpmConfidence : rhythm.BpmHistogramFirstPeakWeight;
        if (confidence <= 0f && rhythm.BpmHistogram is { Length: > 0 } histForConfidence)
        {
            float total = histForConfidence.Sum();
            if (total > 0f)
                confidence = histForConfidence.Max() / total;
        }

        // ── histogram median smoothing ────────────────────────────────────
        if (rhythm.BpmHistogram is { Length: > 0 } histogram)
        {
            float smoothed = HistogramMedian(histogram);
            // Only use the histogram median if it is not an obvious octave error
            // compared with the direct BPM estimate.
            if (IsOctaveConsistent(rawBpm, smoothed))
            {
                rawBpm = smoothed;
            }
            // Confidence penalty when the histogram is flat (unstable tempo)
            float histMax = histogram.Max();
            float histMean = histogram.Average();
            float peakedness = histMax > 0f ? histMean / histMax : 0f;
            confidence *= (1f - 0.3f * peakedness); // reduce conf for broad histograms
        }

        // ── breakbeat/DnB half-time correction ─────────────────────────────
        if (rawBpm is >= HalfTimeBandMin and <= HalfTimeBandMax
            && rhythm.OnsetRate >= OnsetRateCorroborationThreshold)
        {
            float corrected = rawBpm * 2f;
            RecordAnomaly(target, $"bpm_halftime_corrected:{rawBpm:F1}->{corrected:F1}");
            rawBpm = corrected;
            confidence *= HalfTimeCorrectionConfidencePenalty;
        }

        // ── half/double time correction ───────────────────────────────────
        rawBpm = NormaliseToRange(rawBpm);

        target.Bpm = MathF.Round(rawBpm, 2);
        target.BpmConfidence = Math.Clamp(confidence, 0f, 1f);

        // BPM stability from histogram peak width
        if (rhythm.BpmHistogram is { Length: > 0 } h2)
            target.BpmStability = ComputeStability(h2);
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    /// <summary>
    /// Computes the weighted median BPM from an Essentia BPM histogram.
    /// Each bin index corresponds to 1 BPM; the value is the weight.
    /// </summary>
    private static float HistogramMedian(float[] histogram)
    {
        float total = histogram.Sum();
        if (total <= 0f) return 0f;

        float cumulative = 0f;
        float half = total / 2f;
        for (int i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= half)
                return i + 1f; // bin index is 0-based; BPM starts at 1
        }
        return histogram.Length;
    }

    /// <summary>
    /// Returns true if two BPM values are within the same octave (neither is
    /// an obvious 2×/0.5× artefact).
    /// </summary>
    private static bool IsOctaveConsistent(float a, float b)
    {
        if (a <= 0f || b <= 0f) return false;
        float ratio = a / b;
        // Accept 0.9–1.1 as "same", 1.8–2.2 as double-time (reject), etc.
        return ratio is > 0.9f and < 1.1f;
    }

    /// <summary>Clamps BPM to [60, 200] recovering half/double-time errors.</summary>
    private static float NormaliseToRange(float bpm)
    {
        if (bpm <= 0f) return bpm;
        while (bpm < MinReasonableBpm) bpm *= 2f;
        while (bpm > MaxReasonableBpm) bpm /= 2f;
        return bpm;
    }

    /// <summary>Appends a short note to <see cref="AudioFeaturesEntity.AnomaliesJson"/> for later auditing.</summary>
    private static void RecordAnomaly(AudioFeaturesEntity target, string note)
    {
        List<string> anomalies;
        try
        {
            anomalies = JsonSerializer.Deserialize<List<string>>(target.AnomaliesJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            anomalies = new List<string>();
        }

        anomalies.Add(note);
        target.AnomaliesJson = JsonSerializer.Serialize(anomalies);
    }

    /// <summary>
    /// Measures how concentrated the histogram peak is.
    /// 1.0 = perfect single-peak; 0.0 = completely flat.
    /// </summary>
    private static float ComputeStability(float[] histogram)
    {
        float max = histogram.Max();
        float sum = histogram.Sum();
        if (sum <= 0f) return 1f;
        return Math.Clamp(max / sum * histogram.Length, 0f, 1f);
    }
}
