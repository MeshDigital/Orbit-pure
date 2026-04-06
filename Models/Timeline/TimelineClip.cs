using System;
using System.Collections.Generic;

namespace SLSKDONET.Models.Timeline;

/// <summary>
/// The stems a clip can reference independently.
/// <c>FullMix</c> (null equivalent) means the complete stereo track file.
/// </summary>
public enum StemType
{
    FullMix = 0,
    Vocals,
    Drums,
    Bass,
    Other
}

/// <summary>
/// A single audio region placed on a <see cref="TimelineTrack"/>.
/// All beat-relative positions are expressed in the project BPM grid
/// defined by the parent <see cref="TimelineSession"/>.
/// </summary>
public class TimelineClip
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // ── Source reference ──────────────────────────────────────────────────
    /// <summary>Hash of the <c>LibraryEntry</c> / <c>PlaylistTrack</c> that provides audio.</summary>
    public string TrackUniqueHash { get; set; } = string.Empty;

    /// <summary>Which stem to play. <c>FullMix</c> means the whole stereo file.</summary>
    public StemType StemSource { get; set; } = StemType.FullMix;

    // ── Position / length (beats) ─────────────────────────────────────────
    /// <summary>Beat at which this clip starts in the session timeline.</summary>
    public double StartBeat { get; set; }

    /// <summary>Duration of the clip in beats.</summary>
    public double LengthBeats { get; set; }

    // ── Playback window inside the source file ────────────────────────────
    /// <summary>
    /// Offset within the source file in seconds at which playback of
    /// this clip begins.  Allows looping a section mid-track.
    /// </summary>
    public double SourceOffsetSeconds { get; set; } = 0.0;

    // ── Fade in/out ───────────────────────────────────────────────────────
    /// <summary>Fade-in duration in beats (linear ramp from silence).</summary>
    public double FadeInBeats { get; set; } = 0.5;

    /// <summary>Fade-out duration in beats (linear ramp to silence).</summary>
    public double FadeOutBeats { get; set; } = 0.5;

    // ── Gain ──────────────────────────────────────────────────────────────
    /// <summary>Static gain offset applied to the whole clip (dB).</summary>
    public float GainDb { get; set; } = 0f;

    /// <summary>
    /// Per-beat automation points for dynamic volume changes within the clip.
    /// Beat positions are relative to <see cref="StartBeat"/>.
    /// Sorted ascending by <see cref="GainPoint.BeatPosition"/>.
    /// </summary>
    public List<GainPoint> GainEnvelope { get; set; } = new();

    // ── Transition ────────────────────────────────────────────────────────
    /// <summary>
    /// Transition applied at the <em>end</em> (out-point) of this clip.
    /// <c>null</c> = no transition (hard cut is the default).
    /// </summary>
    public TransitionModel? OutTransition { get; set; }

    // ── Computed helpers ──────────────────────────────────────────────────
    /// <summary>Beat at which this clip ends (exclusive).</summary>
    public double EndBeat => StartBeat + LengthBeats;

    /// <summary>Returns true when <paramref name="beat"/> falls within this clip.</summary>
    public bool ContainsBeat(double beat) => beat >= StartBeat && beat < EndBeat;

    /// <summary>
    /// Evaluates the gain envelope at <paramref name="beatOffset"/> (relative to
    /// <see cref="StartBeat"/>) using linear interpolation between adjacent points.
    /// Falls back to <see cref="GainDb"/> if the envelope is empty.
    /// </summary>
    public float EvaluateGainDb(double beatOffset)
    {
        if (GainEnvelope.Count == 0) return GainDb;

        if (beatOffset <= GainEnvelope[0].BeatPosition) return GainDb + GainEnvelope[0].GainDb;
        if (beatOffset >= GainEnvelope[^1].BeatPosition) return GainDb + GainEnvelope[^1].GainDb;

        // Binary search for surrounding pair
        int lo = 0, hi = GainEnvelope.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (GainEnvelope[mid].BeatPosition <= beatOffset) lo = mid; else hi = mid;
        }

        double span = GainEnvelope[hi].BeatPosition - GainEnvelope[lo].BeatPosition;
        if (span <= 0) return GainDb + GainEnvelope[lo].GainDb;

        double t = (beatOffset - GainEnvelope[lo].BeatPosition) / span;
        float interpolated = GainEnvelope[lo].GainDb + (float)t * (GainEnvelope[hi].GainDb - GainEnvelope[lo].GainDb);
        return GainDb + interpolated;
    }
}
