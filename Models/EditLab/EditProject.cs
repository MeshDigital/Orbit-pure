using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Models.EditLab;

/// <summary>Which part of a source track a clip plays.</summary>
public enum EditStem
{
    FullMix,
    Vocals,
    Drums,
    Bass,
    Other,
    /// <summary>Drums + bass + other summed (the track without vocals).</summary>
    Instrumental,
}

/// <summary>How a clip follows the project tempo.</summary>
public enum ClipTempoMode
{
    /// <summary>Follow the project's "Auto-match tempo" toggle.</summary>
    Project,
    /// <summary>Always stretch to the project tempo (key-locked).</summary>
    Follow,
    /// <summary>Always play at the source's own tempo.</summary>
    Native,
}

/// <summary>Where a clip's semitone shift comes from.</summary>
public enum ClipKeyMode
{
    /// <summary>The suggested shift when "Auto-match key" is on, otherwise none.</summary>
    Auto,
    /// <summary><see cref="EditClip.ManualSemitones"/>, whatever the project toggle says.</summary>
    Manual,
}

/// <summary>
/// An Edit Lab project: a bar grid at one tempo/key, lanes of clips cut from library tracks
/// (whole mix or a separated stem), rendered live for preview or offline to a new library track.
/// Bars are 0-based internally (bar 0 = the timeline start); the UI shows them 1-based.
/// </summary>
public sealed class EditProject
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled edit";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The track the project tempo/key came from and the render is named after.</summary>
    public string? BaseTrackHash { get; set; }

    /// <summary>Project tempo (precise BPM). Every clip that follows the tempo is stretched to it.</summary>
    public double Bpm { get; set; } = 120.0;
    /// <summary>Project key in Camelot notation ("8A"), or null when unknown.</summary>
    public string? Key { get; set; }
    /// <summary>True when the user typed the tempo, so changing the base track won't overwrite it.</summary>
    public bool BpmIsManual { get; set; }
    public bool KeyIsManual { get; set; }

    public bool AutoMatchTempo { get; set; } = true;
    public bool AutoMatchKey { get; set; }

    public int BeatsPerBar { get; set; } = 4;

    /// <summary>Loop region for preview / "render loop region" (0-based bars), or null.</summary>
    public double? LoopStartBar { get; set; }
    public double? LoopEndBar { get; set; }

    public List<EditSource> Sources { get; set; } = new();
    public List<EditLane> Lanes { get; set; } = new();
    public List<EditRenderRecord> Renders { get; set; } = new();

    public EditSource? FindSource(string? hash) =>
        hash == null ? null : Sources.FirstOrDefault(s => string.Equals(s.TrackHash, hash, StringComparison.Ordinal));

    public EditSource? BaseSource => FindSource(BaseTrackHash);

    public IEnumerable<EditClip> AllClips => Lanes.SelectMany(l => l.Clips);

    public EditClip? FindClip(Guid id) => AllClips.FirstOrDefault(c => c.Id == id);

    public EditLane? LaneOf(Guid clipId) => Lanes.FirstOrDefault(l => l.Clips.Any(c => c.Id == clipId));

    /// <summary>End of the last clip (0-based bars); 0 for an empty project.</summary>
    public double LengthBars => AllClips.Select(c => c.EndBar).DefaultIfEmpty(0).Max();

    public bool HasLoopRegion => LoopStartBar is { } s && LoopEndBar is { } e && e > s;
}

/// <summary>A library track used by the project, with the analysis it was added with.</summary>
public sealed class EditSource
{
    public string TrackHash { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    /// <summary>Precise analysed BPM; 0 when the track hasn't been analysed.</summary>
    public double Bpm { get; set; }
    public string? Key { get; set; }
    /// <summary>First downbeat (seconds) — bar 1 of the source.</summary>
    public double DownbeatSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public int SampleRate { get; set; }
    public List<EditPhrase> Phrases { get; set; } = new();

    /// <summary>Separated stem files (cache paths) by stem, when separation has run.</summary>
    public Dictionary<EditStem, string> StemPaths { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";
}

/// <summary>A phrase from the source's analysis (used by the "Extended intro" template).</summary>
public sealed class EditPhrase
{
    public string Label { get; set; } = string.Empty;
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public int Bars { get; set; }
}

public sealed class EditLane
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Lane";
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public List<EditClip> Clips { get; set; } = new();
}

/// <summary>A piece of a source track (or one of its stems) placed on the bar grid.</summary>
public sealed class EditClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceHash { get; set; } = string.Empty;
    public EditStem Stem { get; set; } = EditStem.FullMix;

    /// <summary>Where in the source the clip starts playing (seconds of the source file).</summary>
    public double SourceInSeconds { get; set; }
    /// <summary>Timeline position (0-based bars, fractional = beats).</summary>
    public double StartBar { get; set; }
    public double LengthBars { get; set; } = 4;

    public double GainDb { get; set; }
    public double FadeInBars { get; set; }
    public double FadeOutBars { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }

    public ClipTempoMode TempoMode { get; set; } = ClipTempoMode.Project;
    public ClipKeyMode KeyMode { get; set; } = ClipKeyMode.Auto;
    public int ManualSemitones { get; set; }

    public double EndBar => StartBar + LengthBars;

    /// <summary>A copy with a new id (all fields are values).</summary>
    public EditClip CloneWithNewId()
    {
        var copy = (EditClip)MemberwiseClone();
        copy.Id = Guid.NewGuid();
        return copy;
    }
}

/// <summary>A render of the project that was added to the library.</summary>
public sealed class EditRenderRecord
{
    public string TrackHash { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime RenderedUtc { get; set; } = DateTime.UtcNow;
    public bool LoopRegionOnly { get; set; }
}
