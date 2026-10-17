using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models.EditLab;

namespace SLSKDONET.Services.EditLab;

public enum EditTemplateKind { CustomIntro, Acapella, Instrumental, BasslineSwap, ExtendedIntro }

/// <summary>A template's name, help text and which options it uses.</summary>
public sealed record EditTemplateInfo(EditTemplateKind Kind, string Name, string Description, bool NeedsTrackB, bool UsesBars, bool UsesStartBar, bool UsesRepeats)
{
    public override string ToString() => Name;
}

/// <summary>What to build. Bars are timeline bars at the project tempo; <see cref="StartBar"/> is 0-based.</summary>
public sealed record EditTemplateOptions(EditTemplateKind Kind, string TrackA, string? TrackB = null, int Bars = 16, double StartBar = 32, int Repeats = 2);

/// <summary>
/// Starting points for common DJ edits. A template only lays out ordinary lanes and clips (it
/// replaces the arrangement, as one undo step); everything it creates is edited like a hand-made
/// clip afterwards. In-points are bar-aligned to each source's own downbeat and tempo.
/// </summary>
public static class EditTemplates
{
    public static IReadOnlyList<EditTemplateInfo> Catalog { get; } = new[]
    {
        new EditTemplateInfo(EditTemplateKind.CustomIntro, "Custom intro",
            "Track B's vocal over track A's drums for N bars, then track A carries on from that point.", true, true, false, false),
        new EditTemplateInfo(EditTemplateKind.Acapella, "Acapella",
            "Track A's vocals only, the whole track.", false, false, false, false),
        new EditTemplateInfo(EditTemplateKind.Instrumental, "Instrumental",
            "Track A without vocals (drums + bass + other), the whole track.", false, false, false, false),
        new EditTemplateInfo(EditTemplateKind.BasslineSwap, "Bassline swap",
            "Track A, with track B's bassline replacing A's for N bars from the start bar.", true, true, true, false),
        new EditTemplateInfo(EditTemplateKind.ExtendedIntro, "Extended intro",
            "Track A's intro phrase played N times, then the rest of track A.", false, false, false, true),
    };

    public static EditTemplateInfo Info(EditTemplateKind kind) => Catalog.First(c => c.Kind == kind);

    /// <summary>Replaces <paramref name="project"/>'s lanes with the template. Returns a one-line summary.</summary>
    public static string Apply(EditProject project, EditTemplateOptions options)
    {
        var a = project.FindSource(options.TrackA) ?? throw new InvalidOperationException("Pick track A first.");
        var info = Info(options.Kind);
        EditSource? b = null;
        if (info.NeedsTrackB)
        {
            b = project.FindSource(options.TrackB) ?? throw new InvalidOperationException("Pick track B: this template combines two tracks.");
            if (b.TrackHash == a.TrackHash) throw new InvalidOperationException("Pick two different tracks for A and B.");
        }
        int bars = Math.Clamp(options.Bars, 1, 256);
        double start = Math.Max(0, Math.Round(options.StartBar * 4) / 4);
        int repeats = Math.Clamp(options.Repeats, 1, 16);

        project.Lanes.Clear();
        project.LoopStartBar = project.LoopEndBar = null;

        switch (options.Kind)
        {
            case EditTemplateKind.CustomIntro:
            {
                var drums = Lane(project, $"{Short(a)} drums");
                Add(project, drums, a, EditStem.Drums, 0, bars, InAtBar(project, a, 0));
                var vocals = Lane(project, $"{Short(b!)} vocals");
                var v = Add(project, vocals, b!, EditStem.Vocals, 0, bars, InAtBar(project, b!, 0));
                v.FadeOutBars = Math.Min(0.5, bars / 4.0);
                var rest = Lane(project, Short(a));
                AddToEnd(project, rest, a, EditStem.FullMix, bars, InAtBar(project, a, bars));
                return $"Custom intro: {Short(b!)} vocals over {Short(a)} drums for {bars} bars, then {Short(a)}.";
            }
            case EditTemplateKind.Acapella:
                AddToEnd(project, Lane(project, $"{Short(a)} vocals"), a, EditStem.Vocals, 0, 0);
                return $"Acapella of {Short(a)}.";
            case EditTemplateKind.Instrumental:
                AddToEnd(project, Lane(project, $"{Short(a)} instrumental"), a, EditStem.Instrumental, 0, 0);
                return $"Instrumental of {Short(a)}.";
            case EditTemplateKind.BasslineSwap:
            {
                double end = start + bars;
                var main = Lane(project, Short(a));
                if (start > 0) Add(project, main, a, EditStem.FullMix, 0, start, InAtBar(project, a, 0));
                AddToEnd(project, main, a, EditStem.FullMix, end, InAtBar(project, a, end));
                foreach (var stem in new[] { EditStem.Vocals, EditStem.Drums, EditStem.Other })
                    Add(project, Lane(project, $"{Short(a)} {stem.ToString().ToLowerInvariant()}"), a, stem, start, bars, InAtBar(project, a, start));
                Add(project, Lane(project, $"{Short(b!)} bass"), b!, EditStem.Bass, start, bars, InAtBar(project, b!, start));
                return $"Bassline swap: {Short(b!)} bass under {Short(a)} for bars {start + 1:0.##}–{end + 1:0.##}.";
            }
            case EditTemplateKind.ExtendedIntro:
            {
                var (inPoint, phraseBars) = IntroPhrase(project, a);
                var lane = Lane(project, $"{Short(a)} extended");
                for (int i = 0; i < repeats; i++) Add(project, lane, a, EditStem.FullMix, i * phraseBars, phraseBars, inPoint);
                var probe = new EditClip { SourceHash = a.TrackHash };
                var speed = EditMatching.Resolve(project, probe, a).Speed;
                AddToEnd(project, lane, a, EditStem.FullMix, repeats * phraseBars, inPoint + phraseBars * EditMatching.SecondsPerBar(project) * speed);
                project.LoopStartBar = 0;
                project.LoopEndBar = phraseBars;
                return $"Extended intro: {Short(a)}'s {phraseBars}-bar intro ×{repeats}, then the rest of the track.";
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>The intro phrase (first phrase labelled "intro", else the first phrase, else 16 bars
    /// from the downbeat) as a source in-point and a whole number of timeline bars.</summary>
    public static (double InPoint, int Bars) IntroPhrase(EditProject project, EditSource source)
    {
        var phrase = source.Phrases.FirstOrDefault(p => p.Label.Contains("intro", StringComparison.OrdinalIgnoreCase))
                     ?? source.Phrases.OrderBy(p => p.StartSeconds).FirstOrDefault();
        if (phrase == null || phrase.DurationSeconds <= 0) return (source.DownbeatSeconds, 16);
        var match = EditMatching.Resolve(project, new EditClip { SourceHash = source.TrackHash }, source);
        double bars = phrase.DurationSeconds / match.Speed / EditMatching.SecondsPerBar(project);
        return (phrase.StartSeconds, Math.Max(1, (int)Math.Round(bars)));
    }

    /// <summary>Source seconds at timeline bar <paramref name="timelineBar"/>, counting from the source's downbeat.</summary>
    private static double InAtBar(EditProject project, EditSource source, double timelineBar)
    {
        var match = EditMatching.Resolve(project, new EditClip { SourceHash = source.TrackHash }, source);
        return source.DownbeatSeconds + timelineBar * EditMatching.SecondsPerBar(project) * match.Speed;
    }

    private static EditLane Lane(EditProject project, string name)
    {
        var lane = new EditLane { Name = name };
        project.Lanes.Add(lane);
        return lane;
    }

    private static EditClip Add(EditProject project, EditLane lane, EditSource source, EditStem stem, double startBar, double lengthBars, double inSeconds)
    {
        var clip = new EditClip { SourceHash = source.TrackHash, Stem = stem, StartBar = startBar, LengthBars = Math.Max(EditProjectEditor.MinClipBars, lengthBars), SourceInSeconds = inSeconds };
        lane.Clips.Add(clip);
        return clip;
    }

    /// <summary>A clip from <paramref name="inSeconds"/> to the end of the source (quarter-bar rounded).</summary>
    private static EditClip AddToEnd(EditProject project, EditLane lane, EditSource source, EditStem stem, double startBar, double inSeconds)
    {
        var clip = Add(project, lane, source, stem, startBar, 16, inSeconds);
        double remaining = EditProjectEditor.RemainingBars(project, clip, source);
        clip.LengthBars = Math.Max(1, Math.Floor(remaining * 4) / 4);
        return clip;
    }

    private static string Short(EditSource source) => EditProjectEditor.ShortName(source);
}
