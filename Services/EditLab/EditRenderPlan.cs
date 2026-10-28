using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SLSKDONET.Models.EditLab;

namespace SLSKDONET.Services.EditLab;

/// <summary>One audio file played by one clip, with everything resolved to frames at the plan rate.</summary>
public sealed record ClipPlan(
    Guid ClipId,
    string AudioPath,
    long StartFrame,
    long LengthFrames,
    double SourceInSeconds,
    double Speed,
    double PitchFactor,
    float Gain,
    long FadeInFrames,
    long FadeOutFrames)
{
    public long EndFrame => StartFrame + LengthFrames;
}

/// <summary>
/// An immutable, render-ready snapshot of an <see cref="EditProject"/>: every audible clip as a
/// <see cref="ClipPlan"/> on an absolute frame grid. The live preview and the offline render both
/// play the same plan through <see cref="EditTimelineRenderer"/>, so what you hear is what you render.
/// </summary>
public sealed class EditRenderPlan
{
    public int SampleRate { get; }
    public IReadOnlyList<ClipPlan> Clips { get; }
    /// <summary>End of the last clip, in frames.</summary>
    public long TotalFrames { get; }
    public double SecondsPerBar { get; }
    /// <summary>Clips that can't play (stems not separated, file missing). Rendering is refused while non-empty.</summary>
    public IReadOnlyList<string> Problems { get; }

    public EditRenderPlan(int sampleRate, IReadOnlyList<ClipPlan> clips, double secondsPerBar, IReadOnlyList<string> problems, long? totalFrames = null)
    {
        SampleRate = sampleRate;
        Clips = clips.OrderBy(c => c.StartFrame).ToList();
        SecondsPerBar = secondsPerBar;
        Problems = problems;
        TotalFrames = totalFrames ?? (Clips.Count == 0 ? 0 : Clips.Max(c => c.EndFrame));
    }

    public long BarToFrame(double bar) => (long)Math.Round(bar * SecondsPerBar * SampleRate);

    public double FrameToBar(long frame) => frame / (double)SampleRate / SecondsPerBar;

    /// <summary>The files a clip of <paramref name="stem"/> reads (Instrumental = drums + bass + other).</summary>
    public static IEnumerable<EditStem> FilesFor(EditStem stem) => stem == EditStem.Instrumental
        ? new[] { EditStem.Drums, EditStem.Bass, EditStem.Other }
        : new[] { stem };

    /// <summary>Default resolver: the track file for the full mix, the cached stem file otherwise.</summary>
    public static string? DefaultAudioPath(EditSource source, EditStem stem)
    {
        if (stem == EditStem.FullMix) return File.Exists(source.FilePath) ? source.FilePath : null;
        return source.StemPaths.TryGetValue(stem, out var p) && File.Exists(p) ? p : null;
    }

    /// <summary>True when the clip (or its lane) is heard given the project's mute/solo state.</summary>
    public static bool IsAudible(EditProject project, EditLane lane, EditClip clip)
    {
        if (lane.Muted || clip.Muted) return false;
        bool anySolo = project.Lanes.Any(l => l.Solo || l.Clips.Any(c => c.Solo));
        return !anySolo || lane.Solo || clip.Solo;
    }

    /// <summary>Compiles <paramref name="project"/> at <paramref name="sampleRate"/>.
    /// <paramref name="resolveAudio"/> maps a source + single stem to a playable file (null = unavailable).</summary>
    public static EditRenderPlan Compile(EditProject project, int sampleRate, Func<EditSource, EditStem, string?>? resolveAudio = null)
    {
        resolveAudio ??= DefaultAudioPath;
        double spb = EditMatching.SecondsPerBar(project);
        long ToFrame(double bar) => (long)Math.Round(bar * spb * sampleRate);

        var clips = new List<ClipPlan>();
        var problems = new List<string>();
        foreach (var lane in project.Lanes)
        foreach (var clip in lane.Clips)
        {
            if (clip.LengthBars <= 0 || !IsAudible(project, lane, clip)) continue;
            var source = project.FindSource(clip.SourceHash);
            if (source == null)
            {
                problems.Add($"A clip on \"{lane.Name}\" uses a track that is no longer in the project.");
                continue;
            }

            var match = EditMatching.Resolve(project, clip, source);
            long start = ToFrame(Math.Max(0, clip.StartBar));
            long end = ToFrame(Math.Max(0, clip.EndBar));
            long length = end - start;
            if (length <= 0) continue;
            long fadeIn = Math.Min(length, ToFrame(Math.Max(0, clip.FadeInBars)));
            long fadeOut = Math.Min(length, ToFrame(Math.Max(0, clip.FadeOutBars)));
            float gain = (float)Math.Pow(10, Math.Clamp(clip.GainDb, -60, 12) / 20.0);

            foreach (var stem in FilesFor(clip.Stem))
            {
                var path = resolveAudio(source, stem);
                if (path == null)
                {
                    problems.Add(stem == EditStem.FullMix
                        ? $"\"{source.Title}\": the audio file is missing."
                        : $"\"{source.Title}\": {stem.ToString().ToLowerInvariant()} stem isn't separated yet.");
                    continue;
                }
                clips.Add(new ClipPlan(clip.Id, path, start, length, clip.SourceInSeconds, match.Speed,
                    match.PitchFactor, gain, fadeIn, fadeOut));
            }
        }

        return new EditRenderPlan(sampleRate, clips, spb, problems.Distinct().ToList());
    }
}
