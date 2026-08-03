using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Runs the structural analysis pass early (right after Essentia/energy-curve extraction) purely
/// to back-fill the legacy <see cref="AudioFeaturesEntity.DropTimeSeconds"/>/<see cref="AudioFeaturesEntity.DropConfidence"/>
/// fields — still read as a last-resort fallback by <see cref="Engine.Analysis.AnalysisPipelineResultBuilder"/>
/// for tracks with no real sub-bass/novelty signals yet.
///
/// Does NOT persist cue points. That used to happen here via the weak <c>Services.CueGenerationService</c>
/// (a plain RMS-derivative heuristic, no sub-bass/spectral-flux data), and again moments later via
/// <see cref="AnalyzeTrackStructureJob"/> using the same weak service — two redundant writes of the
/// same low-quality cue set, which also got read back into Cue Forge's phrase map and silently
/// outranked the real DSP drop signals. <see cref="AnalyzeTrackStructureJob"/> is now the single
/// place cues are generated and persisted, via the stronger <see cref="Engine.Cueing.CueGenerationService"/>.
/// </summary>
public sealed class CuePointDetectionService
{
    private readonly ILogger<CuePointDetectionService> _logger;

    public CuePointDetectionService(ILogger<CuePointDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Runs structural analysis for a track and back-fills legacy drop fields. Cue persistence
    /// happens later, in <see cref="AnalyzeTrackStructureJob"/>.
    /// </summary>
    /// <param name="trackUniqueHash">Track identity (content hash).</param>
    /// <param name="features">Audio features already stored for the track.</param>
    /// <param name="essentiaOutput">Optional — used to refine onset density if available.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<CuePointEntity>> DetectAndPersistAsync(
        string trackUniqueHash,
        AudioFeaturesEntity features,
        EssentiaOutput? essentiaOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (string.IsNullOrWhiteSpace(trackUniqueHash))
            throw new ArgumentNullException(nameof(trackUniqueHash));

        if (features.Bpm <= 0f || features.TrackDuration <= 0)
        {
            _logger.LogWarning("[CueDetection] Skipping {Hash}: BPM={Bpm} Duration={Dur}",
                trackUniqueHash, features.Bpm, features.TrackDuration);
            return Array.Empty<CuePointEntity>();
        }

        // ── 1. Build energy curve from features ───────────────────────────
        float[] energyCurve = BuildEnergyCurve(trackUniqueHash, features, essentiaOutput);

        // ── 2. Run structural analysis ────────────────────────────────────
        var result = StructuralAnalysisEngine.Analyze(
            bpm: features.Bpm,
            durationSeconds: features.TrackDuration,
            energyCurve: energyCurve);

        _logger.LogDebug("[CueDetection] {Hash}: {Drops} drops, {Phrases} phrases",
            trackUniqueHash, result.Drops.Count, result.PhraseBoundaries.Count);

        // ── 3. Back-fill legacy shortcut fields on AudioFeaturesEntity ─────
        // Cue persistence happens later, in AnalyzeTrackStructureJob, via the stronger
        // Engine.Cueing.CueGenerationService — not here (see class doc comment).
        if (result.Drops.Count > 0)
        {
            features.DropTimeSeconds = (float)result.Drops[0].TimestampSeconds;
            features.DropConfidence  = result.Drops[0].Confidence;
        }

        await Task.CompletedTask;
        return Array.Empty<CuePointEntity>();
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    /// <summary>
    /// Real per-second RMS energy curve, computed from raw audio at analysis time
    /// (see <see cref="AudioAnalysisService"/> Step 4b) and stored in
    /// <see cref="AudioFeaturesEntity.EnergyCurveJson"/>. Falls back to a flat synthetic
    /// curve only when that's unavailable (e.g. decode failed for this track before the
    /// RMS pass could run) — a flat curve has ~zero variance, so a derivative-based
    /// drop detector can only "find" a drop at its artificial edges. Real variance is
    /// what makes drop/phrase detection actually work.
    /// </summary>
    private float[] BuildEnergyCurve(string trackUniqueHash, AudioFeaturesEntity f, EssentiaOutput? output)
    {
        if (!string.IsNullOrWhiteSpace(f.EnergyCurveJson) && f.EnergyCurveJson != "[]")
        {
            try
            {
                var real = JsonSerializer.Deserialize<float[]>(f.EnergyCurveJson);
                if (real is { Length: > 0 })
                    return real;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[CueDetection] Could not deserialise EnergyCurveJson for {Hash}; falling back to synthetic curve", trackUniqueHash);
            }
        }

        _logger.LogDebug("[CueDetection] {Hash}: no real energy curve available, using flat synthetic fallback", trackUniqueHash);
        return BuildSyntheticEnergyCurve(f, output);
    }

    /// <summary>
    /// Last-resort fallback when no real RMS curve was computed for this track.
    /// A flat curve modulated only by a single scalar can't represent an actual drop —
    /// this exists purely so downstream code always has a non-empty curve to work with.
    /// </summary>
    private static float[] BuildSyntheticEnergyCurve(AudioFeaturesEntity f, EssentiaOutput? output)
    {
        double duration   = f.TrackDuration;
        double windowSecs = StructuralAnalysisEngine.EnergyWindowSeconds;
        int    bins       = (int)Math.Ceiling(duration / windowSecs);
        if (bins <= 0) return Array.Empty<float>();

        var curve = new float[bins];

        // Populate with a constant energy derived from stored Energy [0,1]
        float baseEnergy = f.Energy;
        for (int i = 0; i < bins; i++)
            curve[i] = baseEnergy;

        // Modulate with onset rate if available (higher onset density → higher energy)
        float onsetRate = output?.Rhythm?.OnsetRate ?? f.OnsetRate;
        if (onsetRate > 0f)
        {
            float norm = Math.Clamp(onsetRate / 10f, 0f, 1f); // 10 onsets/s ≈ full energy
            for (int i = 0; i < bins; i++)
                curve[i] = Math.Clamp(curve[i] * (0.5f + 0.5f * norm), 0f, 1f);
        }

        // Simple intro ramp-up (first 10 %) and outro fade (last 10 %)
        int rampBins = Math.Max(1, bins / 10);
        for (int i = 0; i < rampBins; i++)
        {
            float t = (float)i / rampBins;
            curve[i] *= t;
            curve[bins - 1 - i] *= t;
        }

        return curve;
    }
}
