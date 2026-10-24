using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models.EditLab;

namespace SLSKDONET.Services.EditLab;

/// <summary>
/// Every change to an open <see cref="EditProject"/> goes through here, so undo/redo is uniform:
/// each operation snapshots the project (JSON) first. Drags use <see cref="BeginGesture"/> /
/// <see cref="EndGesture"/> so a whole drag is one undo step, and repeated edits of the same field
/// (a slider being dragged) coalesce into one step. Pure model code — no UI, unit-tested directly.
/// </summary>
public sealed class EditProjectEditor
{
    public const int MaxUndo = 200;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1.5);

    private readonly LinkedList<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string? _gestureSnapshot;
    private string? _lastCoalesceKey;
    private DateTime _lastCoalesceAt;

    public EditProject Project { get; private set; } = new();

    /// <summary>Raised after every change, undo, redo and load.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool InGesture => _gestureSnapshot != null;

    /// <summary>Clock for coalescing; settable for tests.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public void Load(EditProject project)
    {
        Project = project;
        _undo.Clear();
        _redo.Clear();
        _gestureSnapshot = null;
        _lastCoalesceKey = null;
        Changed?.Invoke();
    }

    private void PushUndo(string snapshot)
    {
        _undo.AddLast(snapshot);
        while (_undo.Count > MaxUndo) _undo.RemoveFirst();
        _redo.Clear();
    }

    /// <summary>Applies <paramref name="change"/> as one undo step. With a <paramref name="coalesceKey"/>,
    /// back-to-back changes with the same key within 1.5 s share a step.</summary>
    public void Mutate(Action<EditProject> change, string? coalesceKey = null)
    {
        if (_gestureSnapshot == null)
        {
            var now = Now();
            bool coalesce = coalesceKey != null && coalesceKey == _lastCoalesceKey && now - _lastCoalesceAt < CoalesceWindow && _undo.Count > 0;
            if (!coalesce) PushUndo(EditProjectStore.Serialize(Project));
            _lastCoalesceKey = coalesceKey;
            _lastCoalesceAt = now;
        }
        change(Project);
        Changed?.Invoke();
    }

    public void BeginGesture()
    {
        _gestureSnapshot ??= EditProjectStore.Serialize(Project);
        _lastCoalesceKey = null;
    }

    /// <summary>Ends a drag; it becomes one undo step if anything changed.</summary>
    public void EndGesture()
    {
        if (_gestureSnapshot == null) return;
        var before = _gestureSnapshot;
        _gestureSnapshot = null;
        if (before != EditProjectStore.Serialize(Project)) PushUndo(before);
        Changed?.Invoke();
    }

    /// <summary>Restores the state from before the current gesture (Escape during a drag).</summary>
    public void CancelGesture()
    {
        if (_gestureSnapshot == null) return;
        Project = EditProjectStore.Deserialize(_gestureSnapshot);
        _gestureSnapshot = null;
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_gestureSnapshot != null || _undo.Count == 0) return false;
        _redo.Push(EditProjectStore.Serialize(Project));
        var snapshot = _undo.Last!.Value;
        _undo.RemoveLast();
        Project = EditProjectStore.Deserialize(snapshot);
        _lastCoalesceKey = null;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_gestureSnapshot != null || _redo.Count == 0) return false;
        _undo.AddLast(EditProjectStore.Serialize(Project));
        Project = EditProjectStore.Deserialize(_redo.Pop());
        _lastCoalesceKey = null;
        Changed?.Invoke();
        return true;
    }

    // ── Sources ──────────────────────────────────────────────────────────────

    /// <summary>Adds (or refreshes) a source. The first source becomes the base.</summary>
    public void AddSource(EditSource source) => Mutate(p =>
    {
        int existing = p.Sources.FindIndex(s => s.TrackHash == source.TrackHash);
        if (existing >= 0) p.Sources[existing] = source;
        else p.Sources.Add(source);
        if (p.BaseTrackHash == null) ApplyBase(p, source);
    });

    public void SetBase(string trackHash) => Mutate(p =>
    {
        var source = p.FindSource(trackHash);
        if (source != null) ApplyBase(p, source);
    });

    /// <summary>The base track sets the project tempo/key unless the user typed their own.</summary>
    public static void ApplyBase(EditProject p, EditSource source)
    {
        p.BaseTrackHash = source.TrackHash;
        if (!p.BpmIsManual && source.Bpm > 0) p.Bpm = source.Bpm;
        if (!p.KeyIsManual) p.Key = EditKeyMath.Normalize(source.Key);
    }

    public void RemoveSource(string trackHash) => Mutate(p =>
    {
        p.Sources.RemoveAll(s => s.TrackHash == trackHash);
        foreach (var lane in p.Lanes) lane.Clips.RemoveAll(c => c.SourceHash == trackHash);
        if (p.BaseTrackHash == trackHash)
        {
            p.BaseTrackHash = null;
            if (p.Sources.FirstOrDefault() is { } next) ApplyBase(p, next);
        }
    });

    /// <summary>Records separated stem files on a source (not an undo step: it's not an edit).</summary>
    public void SetStemPaths(string trackHash, IReadOnlyDictionary<EditStem, string> paths)
    {
        var source = Project.FindSource(trackHash);
        if (source == null) return;
        foreach (var (stem, path) in paths) source.StemPaths[stem] = path;
        Changed?.Invoke();
    }

    public void SetTempo(double bpm) => Mutate(p => { p.Bpm = Math.Clamp(bpm, 40, 250); p.BpmIsManual = true; }, "tempo");

    public void SetKey(string? camelot) => Mutate(p => { p.Key = EditKeyMath.Normalize(camelot); p.KeyIsManual = p.Key != null; }, "key");

    // ── Lanes ────────────────────────────────────────────────────────────────

    public EditLane AddLane(string? name = null)
    {
        EditLane lane = null!;
        Mutate(p =>
        {
            lane = new EditLane { Name = name ?? $"Lane {p.Lanes.Count + 1}" };
            p.Lanes.Add(lane);
        });
        return lane;
    }

    public void RemoveLane(Guid laneId) => Mutate(p => p.Lanes.RemoveAll(l => l.Id == laneId));

    public void SetLane(Guid laneId, Action<EditLane> change, string? coalesceKey = null)
    {
        if (Project.Lanes.All(l => l.Id != laneId)) return;
        Mutate(p => change(p.Lanes.First(l => l.Id == laneId)), coalesceKey);
    }

    // ── Clips ────────────────────────────────────────────────────────────────

    /// <summary>Adds a clip of <paramref name="stem"/> on <paramref name="laneId"/> (a new lane when null).
    /// Default in-point: the source's first downbeat; default length: 16 bars or what's left of the source.</summary>
    public EditClip AddClip(string sourceHash, EditStem stem, double startBar, Guid? laneId = null, double? lengthBars = null, double? sourceInSeconds = null)
    {
        EditClip clip = null!;
        Mutate(p =>
        {
            var source = p.FindSource(sourceHash) ?? throw new InvalidOperationException("That track isn't in the project.");
            var lane = laneId is { } id ? p.Lanes.FirstOrDefault(l => l.Id == id) : null;
            if (lane == null)
            {
                lane = new EditLane { Name = $"{ShortName(source)} {StemLabel(stem).ToLowerInvariant()}" };
                p.Lanes.Add(lane);
            }
            clip = new EditClip
            {
                SourceHash = sourceHash,
                Stem = stem,
                StartBar = Math.Max(0, startBar),
                SourceInSeconds = sourceInSeconds ?? source.DownbeatSeconds,
            };
            clip.LengthBars = lengthBars ?? Math.Max(1, Math.Min(16, Math.Floor(RemainingBars(p, clip, source))));
            lane.Clips.Add(clip);
        });
        return clip;
    }

    /// <summary>Timeline bars of source audio left after the clip's in-point.</summary>
    public static double RemainingBars(EditProject p, EditClip clip, EditSource source)
    {
        if (source.DurationSeconds <= 0) return 16;
        var match = EditMatching.Resolve(p, clip, source);
        return Math.Max(0, EditMatching.SourceSecondsToTimelineBars(p, source.DurationSeconds - clip.SourceInSeconds, match));
    }

    public void SetClip(Guid clipId, Action<EditClip> change, string? coalesceKey = null)
    {
        if (Project.FindClip(clipId) == null) return;
        Mutate(p =>
        {
            var clip = p.FindClip(clipId);
            if (clip != null) change(clip);
        }, coalesceKey);
    }

    /// <summary>Moves a clip in time and optionally to another lane (index into <see cref="EditProject.Lanes"/>).</summary>
    public void MoveClip(Guid clipId, double startBar, int? laneIndex = null) => Mutate(p =>
    {
        var clip = p.FindClip(clipId);
        if (clip == null) return;
        clip.StartBar = Math.Max(0, startBar);
        if (laneIndex is { } idx && idx >= 0 && idx < p.Lanes.Count)
        {
            var from = p.LaneOf(clipId);
            var to = p.Lanes[idx];
            if (from != null && from != to)
            {
                from.Clips.Remove(clip);
                to.Clips.Add(clip);
            }
        }
    });

    /// <summary>Drags the left edge: the audio under the rest of the clip stays where it is.</summary>
    public void TrimStart(Guid clipId, double newStartBar) => Mutate(p =>
    {
        var clip = p.FindClip(clipId);
        var source = clip == null ? null : p.FindSource(clip.SourceHash);
        if (clip == null) return;
        double end = clip.EndBar;
        newStartBar = Math.Clamp(newStartBar, 0, end - MinClipBars);
        double delta = newStartBar - clip.StartBar;
        var match = EditMatching.Resolve(p, clip, source);
        clip.SourceInSeconds += delta * EditMatching.SecondsPerBar(p) * match.Speed;
        clip.StartBar = newStartBar;
        clip.LengthBars = end - newStartBar;
        ClampFades(clip);
    });

    public void TrimEnd(Guid clipId, double newEndBar) => Mutate(p =>
    {
        var clip = p.FindClip(clipId);
        if (clip == null) return;
        clip.LengthBars = Math.Max(MinClipBars, newEndBar - clip.StartBar);
        ClampFades(clip);
    });

    public const double MinClipBars = 0.25;

    private static void ClampFades(EditClip clip)
    {
        clip.FadeInBars = Math.Clamp(clip.FadeInBars, 0, clip.LengthBars);
        clip.FadeOutBars = Math.Clamp(clip.FadeOutBars, 0, clip.LengthBars - clip.FadeInBars);
    }

    public void DeleteClip(Guid clipId) => Mutate(p =>
    {
        foreach (var lane in p.Lanes) lane.Clips.RemoveAll(c => c.Id == clipId);
    });

    /// <summary>Copies a clip to directly after itself on the same lane.</summary>
    public EditClip? DuplicateClip(Guid clipId)
    {
        var original = Project.FindClip(clipId);
        if (original == null) return null;
        EditClip copy = null!;
        Mutate(p =>
        {
            var lane = p.LaneOf(clipId)!;
            var src = p.FindClip(clipId)!;
            copy = src.CloneWithNewId();
            copy.StartBar = src.EndBar;
            lane.Clips.Add(copy);
        });
        return copy;
    }

    /// <summary>Splits a clip at <paramref name="bar"/> into two clips that play exactly the same audio.</summary>
    public EditClip? SplitClip(Guid clipId, double bar)
    {
        var clip = Project.FindClip(clipId);
        if (clip == null || bar <= clip.StartBar + MinClipBars || bar >= clip.EndBar - MinClipBars) return null;
        EditClip right = null!;
        Mutate(p =>
        {
            var left = p.FindClip(clipId)!;
            var lane = p.LaneOf(clipId)!;
            var match = EditMatching.Resolve(p, left, p.FindSource(left.SourceHash));
            double cut = bar - left.StartBar;
            right = left.CloneWithNewId();
            right.StartBar = bar;
            right.LengthBars = left.LengthBars - cut;
            right.SourceInSeconds = left.SourceInSeconds + cut * EditMatching.SecondsPerBar(p) * match.Speed;
            right.FadeInBars = 0;
            left.LengthBars = cut;
            left.FadeOutBars = 0;
            ClampFades(left);
            ClampFades(right);
            lane.Clips.Add(right);
        });
        return right;
    }

    public void SetLoop(double? startBar, double? endBar) => Mutate(p =>
    {
        if (startBar is { } s && endBar is { } e && e > s)
        {
            p.LoopStartBar = Math.Max(0, s);
            p.LoopEndBar = e;
        }
        else
        {
            p.LoopStartBar = null;
            p.LoopEndBar = null;
        }
    });

    public void Rename(string name) => Mutate(p => p.Name = string.IsNullOrWhiteSpace(name) ? p.Name : name.Trim(), "name");

    public void SetAutoMatch(bool? tempo, bool? key) => Mutate(p =>
    {
        if (tempo is { } t) p.AutoMatchTempo = t;
        if (key is { } k) p.AutoMatchKey = k;
    });

    // ── Labels ───────────────────────────────────────────────────────────────

    public static string StemLabel(EditStem stem) => stem switch
    {
        EditStem.FullMix => "Full mix",
        EditStem.Instrumental => "Instrumental",
        _ => stem.ToString(),
    };

    public static string ShortName(EditSource source)
    {
        var title = string.IsNullOrWhiteSpace(source.Title) ? "Track" : source.Title;
        return title.Length > 18 ? title[..18].TrimEnd() + "…" : title;
    }

    /// <summary>Snaps a bar position to the grid (<paramref name="beats"/> beats; 0 = free).</summary>
    public static double Snap(double bar, int beats, int beatsPerBar = 4)
    {
        if (beats <= 0) return bar;
        double step = beats / (double)beatsPerBar;
        return Math.Round(bar / step) * step;
    }
}
