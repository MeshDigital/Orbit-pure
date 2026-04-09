using System;
using System.Collections.Generic;

namespace SLSKDONET.Models.Stem;

/// <summary>
/// Snapshot of the full Workstation state, persisted to disk so the session
/// can be restored after an unexpected app close.
/// </summary>
public class WorkstationSession
{
    /// <summary>The mode the workstation was in (maps to <see cref="WorkstationMode"/>).</summary>
    public int ActiveModeIndex { get; set; } = 0;

    public double TimelineOffsetSeconds { get; set; } = 0;
    public double TimelineWindowSeconds { get; set; } = 60;

    public List<WorkstationDeckState> Decks { get; set; } = [];

    public DateTime LastSaved { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Snapshot of a single deck slot — enough to reload the track and seek to the
/// same position next session.
/// </summary>
public class WorkstationDeckState
{
    public string DeckLabel       { get; set; } = "";

    /// <summary>Absolute path used to reload the audio file.</summary>
    public string? FilePath       { get; set; }

    /// <summary>SHA-1 / hex hash stored in the library DB — used to reload cue points.</summary>
    public string? TrackUniqueHash { get; set; }
    public string? TrackTitle     { get; set; }
    public string? TrackArtist    { get; set; }
    public double  Bpm            { get; set; }
    public string? Key            { get; set; }

    /// <summary>Playback cursor position at save time (seconds from start).</summary>
    public double  PositionSeconds { get; set; }
}
