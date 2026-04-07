using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Detects DJ-relevant cue points from structural analysis and Essentia onset
/// data, then delegates persistence to <see cref="CueGenerationService"/>.
///
/// Detected cue types (in playback order):
///   Intro          — always at t=0
///   Build          — 16 bars before the first detected drop
///   Drop           — peak energy onset (from StructuralAnalysisEngine)
///   Breakdown      — first energy valley after the drop
///   PhraseBoundary — every 32 beats throughout the track
///   Outro          — last measurable phrase before end of track
/// </summary>
public sealed class CuePointDetectionService
{
    private readonly CueGenerationService _cueGenerator;
    private readonly ILogger<CuePointDetectionService> _logger;

    public CuePointDetectionService(
        CueGenerationService cueGenerator,
        ILogger<CuePointDetectionService> logger)
    {
        _cueGenerator = cueGenerator;
        _logger       = logger;
    }

    /// <summary>
    /// Runs cue point detection for a track and persists the results.
    /// </summary>
    /// <param name="trackUniqueHash">Track identity (content hash).</param>
    /// <param name="features">Audio features already stored for the track.</param>
    /// <param name="essentiaOutput">Optional — used to refine onset density if available.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Generated and persisted cue points ordered by timestamp.</returns>
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
        float[] energyCurve = BuildEnergyCurve(features, essentiaOutput);

        // ── 2. Run structural analysis ────────────────────────────────────
        var result = StructuralAnalysisEngine.Analyze(
            bpm: features.Bpm,
            durationSeconds: features.TrackDuration,
            energyCurve: energyCurve);

        _logger.LogDebug("[CueDetection] {Hash}: {Drops} drops, {Phrases} phrases",
            trackUniqueHash, result.Drops.Count, result.PhraseBoundaries.Count);

        // ── 3. Persist via CueGenerationService ───────────────────────────
        var cues = await _cueGenerator
            .GenerateDefaultCuesAsync(trackUniqueHash, result, cancellationToken)
            .ConfigureAwait(false);

        // ── 4. Back-fill shortcut fields on AudioFeaturesEntity ───────────
        if (result.Drops.Count > 0)
        {
            features.DropTimeSeconds = (float)result.Drops[0].TimestampSeconds;
            features.DropConfidence  = result.Drops[0].Confidence;
        }

        return cues;
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    /// <summary>
    /// Reconstructs a simplified energy curve from stored features so the
    /// StructuralAnalysisEngine can work without raw audio.
    /// </summary>
    private static float[] BuildEnergyCurve(AudioFeaturesEntity f, EssentiaOutput? output)
    {
        // Use onset rate as a proxy for rhythmic density.
        // A short synthetic curve is enough for the heuristic algorithm.

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
