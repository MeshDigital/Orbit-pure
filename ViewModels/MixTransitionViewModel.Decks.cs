using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// One cue on a deck's cue rail in the transition editor. Every cue of the track is listed (hot
/// cues, memory cues, loops, section markers), not just the mix cues the waveform filter shows,
/// so any cue is one click away: mix here, play from here, or edit it in Cue Forge.
/// </summary>
public sealed class DeckCueItem : ReactiveObject
{
    public DeckCueItem(OrbitCue cue, bool isOutgoing, MixTransitionViewModel owner)
    {
        Cue = cue;
        IsOutgoing = isOutgoing;
        ClickCommand = ReactiveCommand.Create(() => owner.ClickDeckCue(isOutgoing, cue));
        MixHereCommand = ReactiveCommand.Create(() => owner.SetMixPointOnCue(isOutgoing, cue));
        PlayCommand = ReactiveCommand.Create(() => owner.PlayDeckCue(isOutgoing, cue));
        EditInCueForgeCommand = ReactiveCommand.CreateFromTask(() => owner.EditDeckCueInCueForgeAsync(isOutgoing, cue));
    }

    public OrbitCue Cue { get; }
    public bool IsOutgoing { get; }

    public string Name => string.IsNullOrWhiteSpace(Cue.Name) ? Cue.KindDisplay : Cue.Name;
    public string TimeDisplay => Cue.TimestampDisplay;
    public string Color => Cue.Color;

    /// <summary>"Hot A", "Memory", "Loop", "Section" or "Mix" — what kind of cue this is.</summary>
    public string KindDisplay =>
        Cue.IsLoop ? "Loop"
        : Cue.IsSectionMarker ? "Section"
        : Cue.HasSlot && Cue.SlotIndex <= 7 ? $"Hot {Cue.SlotLabel}"
        : Cue.IsMixPoint ? "Mix"
        : "Memory";

    public string MixHereLabel => IsOutgoing ? "Mix out here" : "Mix in here";

    public string Tip => IsOutgoing
        ? $"{Name} · {KindDisplay} · {TimeDisplay}\nClick: mix out here and play from it. Right-click for more."
        : $"{Name} · {KindDisplay} · {TimeDisplay}\nClick: mix in here and play from it. Right-click for more.";

    private bool _isCurrentMixPoint;
    /// <summary>The deck's mix point sits on this cue.</summary>
    public bool IsCurrentMixPoint
    {
        get => _isCurrentMixPoint;
        set => this.RaiseAndSetIfChanged(ref _isCurrentMixPoint, value);
    }

    /// <summary>Plain click: same as clicking the cue's flag (mix here and play from it; select it in edit mode).</summary>
    public ReactiveCommand<Unit, Unit> ClickCommand { get; }
    public ReactiveCommand<Unit, Unit> MixHereCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> EditInCueForgeCommand { get; }
}

/// <summary>Deck controls of the transition editor: per-deck zoom, the draggable mix-out / mix-in
/// markers (snapped to a cue or the beat), the all-cues rails and "Edit in Cue Forge" at a cue.</summary>
public partial class MixTransitionViewModel
{
    private const double ZoomStep = 1.5;

    /// <summary>
    /// Where Cue Forge's "← Back to …" link returns to when a track of this pair is opened there.
    /// Flow Builder sets this (back to the same playlist and transition); when null the Library
    /// Mix sidepanel's round trip is used.
    /// </summary>
    public Func<CueForgeReturnPoint?>? CueForgeReturnFactory { get; set; }

    private IReadOnlyList<DeckCueItem> _outgoingCueRail = Array.Empty<DeckCueItem>();
    /// <summary>Every cue of the outgoing track, in time order (regardless of the waveform's cue filter).</summary>
    public IReadOnlyList<DeckCueItem> OutgoingCueRail { get => _outgoingCueRail; private set => this.RaiseAndSetIfChanged(ref _outgoingCueRail, value); }

    private IReadOnlyList<DeckCueItem> _incomingCueRail = Array.Empty<DeckCueItem>();
    public IReadOnlyList<DeckCueItem> IncomingCueRail { get => _incomingCueRail; private set => this.RaiseAndSetIfChanged(ref _incomingCueRail, value); }

    private IReadOnlyList<OrbitCue> _outgoingPickableCues = Array.Empty<OrbitCue>();
    /// <summary>The mix-out picker's choices: every non-loop cue of the outgoing track.</summary>
    public IReadOnlyList<OrbitCue> OutgoingPickableCues { get => _outgoingPickableCues; private set => this.RaiseAndSetIfChanged(ref _outgoingPickableCues, value); }

    private IReadOnlyList<OrbitCue> _incomingPickableCues = Array.Empty<OrbitCue>();
    public IReadOnlyList<OrbitCue> IncomingPickableCues { get => _incomingPickableCues; private set => this.RaiseAndSetIfChanged(ref _incomingPickableCues, value); }

    public bool OutgoingHasCues => _outgoingCueRail.Count > 0;
    public bool IncomingHasCues => _incomingCueRail.Count > 0;

    public ReactiveCommand<Unit, Unit> ZoomOutgoingInCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomOutgoingOutCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomIncomingInCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ZoomIncomingOutCommand { get; private set; } = null!;
    /// <summary>Re-centers the outgoing deck on its mix-out (drops a manual pan).</summary>
    public ReactiveCommand<Unit, Unit> CenterOutgoingOnMixPointCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CenterIncomingOnMixPointCommand { get; private set; } = null!;
    /// <summary>Waveform marker drag / right-click "Mix out here": seconds in A, snapped (cue, else beat).</summary>
    public ReactiveCommand<double, Unit> SetMixOutAtCommand { get; private set; } = null!;
    public ReactiveCommand<double, Unit> SetMixInAtCommand { get; private set; } = null!;
    /// <summary>Waveform right-click on a cue → "Edit in Cue Forge" (the cue's seconds).</summary>
    public ReactiveCommand<double, Unit> EditOutgoingInCueForgeAtCommand { get; private set; } = null!;
    public ReactiveCommand<double, Unit> EditIncomingInCueForgeAtCommand { get; private set; } = null!;

    private void InitDeckControls()
    {
        ZoomOutgoingInCommand = ReactiveCommand.Create(() => { OutgoingZoomLevel *= ZoomStep; });
        ZoomOutgoingOutCommand = ReactiveCommand.Create(() => { OutgoingZoomLevel /= ZoomStep; });
        ZoomIncomingInCommand = ReactiveCommand.Create(() => { IncomingZoomLevel *= ZoomStep; });
        ZoomIncomingOutCommand = ReactiveCommand.Create(() => { IncomingZoomLevel /= ZoomStep; });
        CenterOutgoingOnMixPointCommand = ReactiveCommand.Create(() =>
        {
            _outgoingManualOffset = null;
            this.RaisePropertyChanged(nameof(OutgoingViewOffset));
        });
        CenterIncomingOnMixPointCommand = ReactiveCommand.Create(() =>
        {
            _incomingManualOffset = null;
            this.RaisePropertyChanged(nameof(IncomingViewOffset));
        });
        SetMixOutAtCommand = ReactiveCommand.Create<double>(s => SetMixPointAt(isOutgoing: true, s));
        SetMixInAtCommand = ReactiveCommand.Create<double>(s => SetMixPointAt(isOutgoing: false, s));
        EditOutgoingInCueForgeAtCommand = ReactiveCommand.CreateFromTask<double>(s => OpenTrackInCueForgeAsync(OutgoingTrack, focusSeconds: s));
        EditIncomingInCueForgeAtCommand = ReactiveCommand.CreateFromTask<double>(s => OpenTrackInCueForgeAsync(IncomingTrack, focusSeconds: s));

        // The rails' "this is the mix point" highlight follows every mix-point change.
        this.WhenAnyValue(x => x.SourceTriggerSeconds, x => x.NudgeSeconds).Subscribe(_ => MarkRailMixPoints(_outgoingCueRail, EffectiveSourceTriggerSeconds));
        this.WhenAnyValue(x => x.TargetTriggerSeconds).Subscribe(_ => MarkRailMixPoints(_incomingCueRail, TargetTriggerSeconds));
    }

    /// <summary>Rebuilds both rails and pickers from the decks' cue sessions (every cue edit).</summary>
    private void RefreshCueRails()
    {
        OutgoingCueRail = BuildRail(OutgoingEditor.Session.Cues, isOutgoing: true);
        IncomingCueRail = BuildRail(IncomingEditor.Session.Cues, isOutgoing: false);
        OutgoingPickableCues = OutgoingEditor.Session.Cues.Where(c => !c.IsLoop).OrderBy(c => c.Timestamp).ToList();
        IncomingPickableCues = IncomingEditor.Session.Cues.Where(c => !c.IsLoop).OrderBy(c => c.Timestamp).ToList();
        MarkRailMixPoints(_outgoingCueRail, EffectiveSourceTriggerSeconds);
        MarkRailMixPoints(_incomingCueRail, TargetTriggerSeconds);
        this.RaisePropertyChanged(nameof(OutgoingHasCues));
        this.RaisePropertyChanged(nameof(IncomingHasCues));
    }

    private IReadOnlyList<DeckCueItem> BuildRail(IEnumerable<OrbitCue> cues, bool isOutgoing) =>
        cues.OrderBy(c => c.Timestamp).Select(c => new DeckCueItem(c, isOutgoing, this)).ToList();

    private static void MarkRailMixPoints(IReadOnlyList<DeckCueItem>? rail, double mixPoint)
    {
        if (rail == null) return;
        foreach (var item in rail)
            item.IsCurrentMixPoint = !item.Cue.IsLoop && Math.Abs(item.Cue.Timestamp - mixPoint) < 0.05;
    }

    /// <summary>
    /// Snaps a hand-placed mix point: onto a cue when it is (already) exactly on one — the waveform
    /// snaps a marker dropped near a cue flag onto it — otherwise onto the nearest beat of the
    /// deck's grid, so a dragged mix point stays phrase-countable. Unknown BPM: left as is.
    /// </summary>
    internal static double SnapMixPoint(double seconds, IEnumerable<OrbitCue> cues, double bpm, double gridOffset, double duration)
    {
        var onCue = cues.FirstOrDefault(c => !c.IsLoop && Math.Abs(c.Timestamp - seconds) < 0.001);
        double snapped = onCue != null ? onCue.Timestamp
            : bpm > 0 ? CueForgeGrid.Snap(seconds, bpm, gridOffset, 1)
            : seconds;
        return duration > 0 ? Math.Clamp(snapped, 0, duration) : Math.Max(0, snapped);
    }

    /// <summary>Drag of a deck's mix marker or its right-click "Mix out/in here". Undoable.</summary>
    internal void SetMixPointAt(bool isOutgoing, double seconds)
    {
        if (!HasLoadedPair) return;
        var deck = isOutgoing ? OutgoingEditor : IncomingEditor;
        double bpm = deck.Session.Bpm > 0 ? deck.Session.Bpm : (isOutgoing ? OutgoingBpm : IncomingBpm);
        double duration = isOutgoing ? OutgoingDurationSeconds : IncomingDurationSeconds;
        double target = SnapMixPoint(seconds, deck.Session.Cues, bpm, deck.Session.GridOffset, duration);
        double current = isOutgoing ? EffectiveSourceTriggerSeconds : TargetTriggerSeconds;
        if (Math.Abs(target - current) < 1e-6) return;

        RecordMixPointUndo();
        if (isOutgoing)
        {
            _nudgeSeconds = 0;
            this.RaisePropertyChanged(nameof(NudgeSeconds));
            SourceTriggerSeconds = target;
        }
        else TargetTriggerSeconds = target;

        var cue = deck.Session.Cues.FirstOrDefault(c => !c.IsLoop && Math.Abs(c.Timestamp - target) < 0.001);
        string where = cue != null ? $"on {cue.Name}" : bpm > 0 ? "on the beat" : string.Empty;
        StatusMessage = $"{(isOutgoing ? "Mix-out" : "Mix-in")} at {FormatTimestamp(target)} {where} · Ctrl+Z to undo".Replace("  ", " ");
    }

    /// <summary>Rail menu "Mix out/in here": the mix point goes exactly on the cue (no playback).</summary>
    internal void SetMixPointOnCue(bool isOutgoing, OrbitCue cue)
    {
        if (!HasLoadedPair) return;
        double current = isOutgoing ? EffectiveSourceTriggerSeconds : TargetTriggerSeconds;
        if (Math.Abs(cue.Timestamp - current) < 1e-6) return;
        RecordMixPointUndo();
        if (isOutgoing)
        {
            _nudgeSeconds = 0;
            this.RaisePropertyChanged(nameof(NudgeSeconds));
            SourceTriggerSeconds = cue.Timestamp;
        }
        else TargetTriggerSeconds = cue.Timestamp;
        StatusMessage = $"{(isOutgoing ? "Mix-out" : "Mix-in")} on {cue.Name} ({FormatTimestamp(cue.Timestamp)}) · Ctrl+Z to undo";
    }

    internal void ClickDeckCue(bool isOutgoing, OrbitCue cue) =>
        OnCueClicked(isOutgoing ? OutgoingEditor : IncomingEditor, cue, cue.Timestamp, isOutgoing);

    /// <summary>Rail menu "Play from here": prelisten the cue without moving the mix point.</summary>
    internal void PlayDeckCue(bool isOutgoing, OrbitCue cue)
    {
        var deck = isOutgoing ? OutgoingEditor : IncomingEditor;
        if (IsPlaying) _previewPlayer.StopPreview();
        (isOutgoing ? IncomingEditor : OutgoingEditor).Session.StopPrelisten();
        deck.Session.PrelistenCue(cue);
        IsPreviewSeekPlaying = deck.Session.PrelisteningCue != null;
        PreviewSeekStatusText = IsPreviewSeekPlaying ? $"Playing {deck.TrackTitle} from {cue.Name} ({FormatTimestamp(cue.Timestamp)})" : string.Empty;
    }

    internal System.Threading.Tasks.Task EditDeckCueInCueForgeAsync(bool isOutgoing, OrbitCue cue) =>
        OpenTrackInCueForgeAsync(isOutgoing ? OutgoingTrack : IncomingTrack, focusSeconds: cue.Timestamp);
}
