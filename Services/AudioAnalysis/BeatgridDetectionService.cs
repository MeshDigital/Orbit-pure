using System;
using System.Text.Json;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Task 1.5 — Derives a complete beat-grid from Essentia rhythm output and
/// writes the result (beat positions + first-downbeat offset) into
/// <see cref="AudioFeaturesEntity"/> for downstream consumers such as
/// <see cref="BeatGridService"/>, the waveform overlay, and cue snapping.
/// </summary>
public sealed class BeatgridDetectionService
{
    /// <summary>
    /// Populates <see cref="AudioFeaturesEntity.BeatGridJson"/> and
    /// <see cref="AudioFeaturesEntity.DownbeatOffsetSeconds"/> from
    /// <paramref name="essentiaOutput"/>.
    /// </summary>
    public void Detect(EssentiaOutput essentiaOutput, AudioFeaturesEntity target)
    {
        ArgumentNullException.ThrowIfNull(essentiaOutput);
        ArgumentNullException.ThrowIfNull(target);

        var rhythm = essentiaOutput.Rhythm;

        // Prefer the Essentia tick array if available; fall back to synthesising
        // from BPM + duration so we always have a grid.
        double[] beats;
        double downbeatOffset;

        if (rhythm?.Ticks is { Length: > 0 } ticks)
        {
            beats = Array.ConvertAll(ticks, t => (double)t);
            downbeatOffset = beats[0];
        }
        else
        {
            // Synthesise from stored BPM
            float bpm = target.Bpm > 0 ? target.Bpm : (rhythm?.Bpm ?? 0f);
            if (bpm <= 0f) return; // cannot generate a grid without BPM

            var duration = target.TrackDuration > 0
                ? target.TrackDuration
                : (essentiaOutput.Metadata?.Duration ?? 0);

            downbeatOffset = 0.0;
            beats = BeatGridService.ComputeBeatGrid((double)bpm, duration, downbeatOffset);
        }

        target.BeatGridJson = JsonSerializer.Serialize(beats);
        target.DownbeatOffsetSeconds = downbeatOffset;
    }
}
