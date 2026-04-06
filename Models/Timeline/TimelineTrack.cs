using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Models.Timeline;

/// <summary>
/// A single horizontal lane in the <see cref="TimelineSession"/> — conceptually
/// equivalent to an Ableton/Logic DAW audio track.  Each lane holds an ordered
/// list of <see cref="TimelineClip"/> regions that may or may not overlap
/// (the session renderer handles overlaps at mix-time).
/// </summary>
public class TimelineTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name shown in the lane header.</summary>
    public string Name { get; set; } = "Track";

    /// <summary>Zero-based vertical index (render order).</summary>
    public int Index { get; set; }

    /// <summary>Mute silences this lane without removing it from the mix graph.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Solo routes only this lane (and other soloed lanes) to output.</summary>
    public bool IsSoloed { get; set; }

    /// <summary>Lane fader level in dB.  0 = unity; negative = attenuate.</summary>
    public float VolumeDb { get; set; } = 0f;

    /// <summary>Stereo pan position: -1 = full left, 0 = centre, +1 = full right.</summary>
    public float PanPosition { get; set; } = 0f;

    /// <summary>CSS-style hex color for the lane handle and clip tint.</summary>
    public string Color { get; set; } = "#4A90D9";

    /// <summary>Ordered collection of audio clips placed on this lane.</summary>
    public List<TimelineClip> Clips { get; set; } = new();

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Returns the clip that covers <paramref name="beat"/>, or null.</summary>
    public TimelineClip? GetClipAt(double beat) =>
        Clips.FirstOrDefault(c => c.ContainsBeat(beat));

    /// <summary>Insert a clip and keep the list sorted by StartBeat.</summary>
    public void AddClip(TimelineClip clip)
    {
        Clips.Add(clip);
        Clips.Sort((a, b) => a.StartBeat.CompareTo(b.StartBeat));
    }

    /// <summary>Remove clip by id.  Returns true if found and removed.</summary>
    public bool RemoveClip(Guid clipId)
    {
        int idx = Clips.FindIndex(c => c.Id == clipId);
        if (idx < 0) return false;
        Clips.RemoveAt(idx);
        return true;
    }

    /// <summary>
    /// Move a clip to a new start beat, keeping clips sorted.
    /// Returns true if the clip was found.
    /// </summary>
    public bool MoveClip(Guid clipId, double newStartBeat)
    {
        var clip = Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip is null) return false;
        clip.StartBeat = newStartBeat;
        Clips.Sort((a, b) => a.StartBeat.CompareTo(b.StartBeat));
        return true;
    }

    /// <summary>
    /// Split clip at <paramref name="beatPosition"/>.  The original clip is
    /// trimmed to end at the split point; a new clip is inserted starting at
    /// the split point and ending where the original ended.  Returns the new
    /// (right-hand) clip, or null if the split point was outside the clip.
    /// </summary>
    public TimelineClip? SplitClip(Guid clipId, double beatPosition)
    {
        var clip = Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip is null || !clip.ContainsBeat(beatPosition)) return null;

        double originalEnd = clip.EndBeat;
        double splitOffset = beatPosition - clip.StartBeat;

        // Adjust original clip to stop at split point
        double originalLength = clip.LengthBeats;
        clip.LengthBeats = splitOffset;
        clip.FadeOutBeats = Math.Min(clip.FadeOutBeats, splitOffset);

        // Build new right-hand clip
        var right = new TimelineClip
        {
            TrackUniqueHash = clip.TrackUniqueHash,
            StemSource = clip.StemSource,
            StartBeat = beatPosition,
            LengthBeats = originalEnd - beatPosition,
            SourceOffsetSeconds = clip.SourceOffsetSeconds +
                (splitOffset * (60.0 / 128.0)), // caller should recalculate with actual BPM
            FadeInBeats = 0,
            FadeOutBeats = clip.FadeOutBeats,
            GainDb = clip.GainDb,
            OutTransition = clip.OutTransition
        };

        clip.OutTransition = null;
        AddClip(right);
        return right;
    }
}
