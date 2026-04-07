using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Text.Json;
using ReactiveUI;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// Task 7.5 — Command pattern for undo/redo
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Reversible operation on a <see cref="TimelineSession"/>.
/// Implement <see cref="Execute"/> and <see cref="Undo"/> to be
/// automatically tracked by <see cref="TimelineCommandStack"/>.
/// </summary>
public interface ITimelineCommand
{
    string Description { get; }
    void Execute(TimelineSession session);
    void Undo(TimelineSession session);
}

/// <summary>
/// Fixed-capacity undo/redo stack for <see cref="ITimelineCommand"/> operations.
/// </summary>
public sealed class TimelineCommandStack
{
    private const int MaxHistory = 100;

    private readonly Stack<ITimelineCommand> _undoStack = new();
    private readonly Stack<ITimelineCommand> _redoStack = new();
    private readonly TimelineSession _session;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public TimelineCommandStack(TimelineSession session) => _session = session;

    /// <summary>Executes the command and pushes it onto the undo stack.</summary>
    public void Push(ITimelineCommand cmd)
    {
        cmd.Execute(_session);
        _undoStack.Push(cmd);
        _redoStack.Clear();                // new action invalidates redo history
        if (_undoStack.Count > MaxHistory)
            TrimStack(_undoStack);
    }

    /// <summary>Undoes the most recent command.</summary>
    public bool Undo()
    {
        if (!CanUndo) return false;
        var cmd = _undoStack.Pop();
        cmd.Undo(_session);
        _redoStack.Push(cmd);
        return true;
    }

    /// <summary>Re-executes the most recently undone command.</summary>
    public bool Redo()
    {
        if (!CanRedo) return false;
        var cmd = _redoStack.Pop();
        cmd.Execute(_session);
        _undoStack.Push(cmd);
        return true;
    }

    public void Clear() { _undoStack.Clear(); _redoStack.Clear(); }

    private static void TrimStack(Stack<ITimelineCommand> stack)
    {
        // Remove the oldest entry (bottom of stack) by re-building it
        var items = stack.ToArray();
        stack.Clear();
        for (int i = items.Length - 2; i >= 0; i--)
            stack.Push(items[i]);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Concrete commands
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Moves a clip to a new start beat, with beat-grid snapping.</summary>
public sealed class MoveClipCommand : ITimelineCommand
{
    private readonly Guid _trackId;
    private readonly Guid _clipId;
    private readonly double _newStartBeat;
    private double _oldStartBeat;

    public string Description =>
        $"Move clip to beat {_newStartBeat:F2}";

    public MoveClipCommand(Guid trackId, Guid clipId, double newStartBeat)
    {
        _trackId      = trackId;
        _clipId       = clipId;
        _newStartBeat = newStartBeat;
    }

    public void Execute(TimelineSession s)
    {
        var clip = FindClip(s);
        if (clip is null) return;
        _oldStartBeat = clip.StartBeat;
        clip.StartBeat = _newStartBeat;
        s.Touch();
    }

    public void Undo(TimelineSession s)
    {
        var clip = FindClip(s);
        if (clip is null) return;
        clip.StartBeat = _oldStartBeat;
        s.Touch();
    }

    private TimelineClip? FindClip(TimelineSession s)
    {
        foreach (var t in s.Tracks)
            if (t.Id == _trackId)
                foreach (var c in t.Clips)
                    if (c.Id == _clipId)
                        return c;
        return null;
    }
}

/// <summary>Trims a clip's start or end point.</summary>
public sealed class TrimClipCommand : ITimelineCommand
{
    private readonly Guid _trackId;
    private readonly Guid _clipId;
    private readonly double _newStartBeat;
    private readonly double _newLengthBeats;
    private double _oldStartBeat;
    private double _oldLengthBeats;

    public string Description => "Trim clip";

    public TrimClipCommand(Guid trackId, Guid clipId, double newStartBeat, double newLengthBeats)
    {
        _trackId        = trackId;
        _clipId         = clipId;
        _newStartBeat   = newStartBeat;
        _newLengthBeats = newLengthBeats;
    }

    public void Execute(TimelineSession s)
    {
        var clip = FindClip(s);
        if (clip is null) return;
        _oldStartBeat   = clip.StartBeat;
        _oldLengthBeats = clip.LengthBeats;
        clip.StartBeat   = _newStartBeat;
        clip.LengthBeats = _newLengthBeats;
        s.Touch();
    }

    public void Undo(TimelineSession s)
    {
        var clip = FindClip(s);
        if (clip is null) return;
        clip.StartBeat   = _oldStartBeat;
        clip.LengthBeats = _oldLengthBeats;
        s.Touch();
    }

    private TimelineClip? FindClip(TimelineSession s)
    {
        foreach (var t in s.Tracks)
            if (t.Id == _trackId)
                foreach (var c in t.Clips)
                    if (c.Id == _clipId)
                        return c;
        return null;
    }
}

/// <summary>
/// Splits a clip at <paramref name="splitAtBeat"/>, replacing it with two
/// shorter clips. Undo removes the second fragment and restores the original.
/// </summary>
public sealed class SplitClipCommand : ITimelineCommand
{
    private readonly Guid _trackId;
    private readonly Guid _clipId;
    private readonly double _splitAtBeat;
    private double _originalLengthBeats;
    private Guid _newClipId;

    public string Description => $"Split clip at beat {_splitAtBeat:F2}";

    public SplitClipCommand(Guid trackId, Guid clipId, double splitAtBeat)
    {
        _trackId    = trackId;
        _clipId     = clipId;
        _splitAtBeat = splitAtBeat;
    }

    public void Execute(TimelineSession s)
    {
        var track = s.Tracks.Find(t => t.Id == _trackId);
        var clip  = track?.Clips.Find(c => c.Id == _clipId);
        if (clip is null || track is null) return;

        if (_splitAtBeat <= clip.StartBeat || _splitAtBeat >= clip.EndBeat) return;

        _originalLengthBeats = clip.LengthBeats;

        // Shorten original clip
        clip.LengthBeats = _splitAtBeat - clip.StartBeat;

        // Create tail fragment
        var tail = new TimelineClip
        {
            Id                  = Guid.NewGuid(),
            TrackUniqueHash     = clip.TrackUniqueHash,
            StemSource          = clip.StemSource,
            StartBeat           = _splitAtBeat,
            LengthBeats         = _originalLengthBeats - clip.LengthBeats,
            SourceOffsetSeconds = clip.SourceOffsetSeconds + s.BeatsToSeconds(clip.LengthBeats),
            GainDb              = clip.GainDb,
            FadeOutBeats        = clip.FadeOutBeats
        };

        _newClipId = tail.Id;
        track.Clips.Add(tail);
        track.Clips.Sort((a, b) => a.StartBeat.CompareTo(b.StartBeat));
        s.Touch();
    }

    public void Undo(TimelineSession s)
    {
        var track = s.Tracks.Find(t => t.Id == _trackId);
        if (track is null) return;

        // Remove tail fragment
        track.Clips.RemoveAll(c => c.Id == _newClipId);

        // Restore original length
        var clip = track.Clips.Find(c => c.Id == _clipId);
        if (clip is not null)
            clip.LengthBeats = _originalLengthBeats;

        s.Touch();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Task 7.4 — TimelineViewModel
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// ViewModel for the DAW-style timeline editor.
///
/// Exposes:
///   - <see cref="Session"/> — mutable <see cref="TimelineSession"/> root model
///   - <see cref="UndoCommand"/> / <see cref="RedoCommand"/> — undo/redo stack
///   - <see cref="MoveClipCommand(Guid,Guid,double)"/> — snap-aware clip move
///   - <see cref="TrimClipCommand(Guid,Guid,double,double)"/> — clip trimming
///   - <see cref="SplitAtBeatCommand(Guid,Guid,double)"/> — clip splitting
///   - <see cref="SnapResolution"/> — configurable beat-grid resolution
/// </summary>
public sealed class TimelineViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private TimelineCommandStack _commandStack;

    // ── Session ───────────────────────────────────────────────────────────

    private TimelineSession _session = new();

    public TimelineSession Session
    {
        get => _session;
        private set
        {
            this.RaiseAndSetIfChanged(ref _session, value);
            _commandStack = new TimelineCommandStack(_session);
            RefreshUndoRedoState();
        }
    }

    // ── Snap grid ─────────────────────────────────────────────────────────

    private GridResolution _snapResolution = GridResolution.Quarter;

    /// <summary>Beat-grid snap resolution applied to all drag/move operations.</summary>
    public GridResolution SnapResolution
    {
        get => _snapResolution;
        set => this.RaiseAndSetIfChanged(ref _snapResolution, value);
    }

    // ── Playhead ──────────────────────────────────────────────────────────

    private double _playheadBeat;

    /// <summary>Current playhead position in beats.</summary>
    public double PlayheadBeat
    {
        get => _playheadBeat;
        set => this.RaiseAndSetIfChanged(ref _playheadBeat, value);
    }

    // ── Undo/redo observable state ────────────────────────────────────────

    private bool _canUndo;
    public bool CanUndo { get => _canUndo; private set => this.RaiseAndSetIfChanged(ref _canUndo, value); }

    private bool _canRedo;
    public bool CanRedo { get => _canRedo; private set => this.RaiseAndSetIfChanged(ref _canRedo, value); }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> UndoCommand    { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand    { get; }
    public ReactiveCommand<Unit, Unit> NewSessionCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public TimelineViewModel()
    {
        _commandStack = new TimelineCommandStack(_session);

        UndoCommand = ReactiveCommand.Create(
            () => { _commandStack.Undo(); RefreshUndoRedoState(); },
            this.WhenAnyValue(x => x.CanUndo));

        RedoCommand = ReactiveCommand.Create(
            () => { _commandStack.Redo(); RefreshUndoRedoState(); },
            this.WhenAnyValue(x => x.CanRedo));

        NewSessionCommand = ReactiveCommand.Create(() =>
        {
            Session = new TimelineSession();
        });
    }

    // ── Edit operations — Task 7.6: grid snapping wired in ────────────────

    /// <summary>
    /// Moves <paramref name="clipId"/> on <paramref name="trackId"/> to
    /// <paramref name="rawBeat"/>, snapping to the current <see cref="SnapResolution"/>.
    /// </summary>
    public void MoveClip(Guid trackId, Guid clipId, double rawBeat)
    {
        double snapped = BeatGridService.SnapToGrid(rawBeat, SnapResolution);
        snapped = Math.Max(0, snapped);
        Push(new MoveClipCommand(trackId, clipId, snapped));
    }

    /// <summary>
    /// Trims <paramref name="clipId"/>: snaps both start beat and new length to grid.
    /// </summary>
    public void TrimClip(Guid trackId, Guid clipId, double rawStartBeat, double rawLengthBeats)
    {
        double snappedStart  = BeatGridService.SnapToGrid(rawStartBeat,
                                   SnapResolution);
        double snappedEnd    = BeatGridService.SnapToGridCeiling(rawStartBeat + rawLengthBeats,
                                   SnapResolution);
        double snappedLength = Math.Max(BeatGridService.SubdivisionBeats(SnapResolution),
                                        snappedEnd - snappedStart);

        Push(new TrimClipCommand(trackId, clipId, snappedStart, snappedLength));
    }

    /// <summary>
    /// Splits <paramref name="clipId"/> at <paramref name="rawBeat"/> (beats are snapped).
    /// </summary>
    public void SplitAtBeat(Guid trackId, Guid clipId, double rawBeat)
    {
        double snapped = BeatGridService.SnapToGrid(rawBeat, SnapResolution);
        Push(new SplitClipCommand(trackId, clipId, snapped));
    }

    // ── Session I/O ───────────────────────────────────────────────────────

    /// <summary>Serialises the session to JSON.</summary>
    public string ExportJson() => Session.ToJson();

    /// <summary>Loads a session from JSON, replacing the current session.</summary>
    public bool ImportJson(string json)
    {
        var s = TimelineSession.FromJson(json);
        if (s is null) return false;
        Session = s;
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private void Push(ITimelineCommand cmd)
    {
        _commandStack.Push(cmd);
        RefreshUndoRedoState();
        // Notify session-derived properties
        this.RaisePropertyChanged(nameof(Session));
    }

    private void RefreshUndoRedoState()
    {
        CanUndo = _commandStack.CanUndo;
        CanRedo = _commandStack.CanRedo;
    }

    public void Dispose() => _disposables.Dispose();
}
