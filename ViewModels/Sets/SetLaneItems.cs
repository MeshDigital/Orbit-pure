using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Sets;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>A slot card in the set lane (P2.4).</summary>
public sealed class SlotCardViewModel : ReactiveObject
{
    public SetSlotInfo Slot { get; }
    public Guid SlotId => Slot.SlotId;
    public int Index => Slot.Position;

    public string Number => (Slot.Position + 1).ToString("00", CultureInfo.InvariantCulture);
    public SetSlotRole RoleValue => Slot.Role;
    public string Role => Slot.Role.ToString().ToUpperInvariant();
    public string Title => Slot.Title;
    public string Artist => Slot.Artist;
    public string Bpm => NumFormat.Bpm(Slot.Bpm);
    public string Key => Slot.CamelotKey ?? "—";
    public bool IsMissing => !Slot.IsInLibrary;
    public string? Notes => Slot.Notes;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Slot.Notes);

    /// <summary>The DJ's energy target for this slot (0-100), if set.</summary>
    public double EnergyTarget => Slot.EnergyTarget ?? 0;
    public bool HasEnergyTarget => Slot.EnergyTarget is not null;

    /// <summary>The track's analysed energy (0-100), if analysed.</summary>
    public double TrackEnergy => Slot.Energy ?? 0;
    public bool HasTrackEnergy => Slot.Energy is not null;

    /// <summary>"Target 70 · Track 64" with "—" for whatever is unknown.</summary>
    public string EnergyText =>
        $"Target {(Slot.EnergyTarget is { } t ? t.ToString("0", CultureInfo.InvariantCulture) : "—")} · " +
        $"Track {(Slot.Energy is { } e ? e.ToString("0", CultureInfo.InvariantCulture) : "—")}";

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

    /// <summary>Plan B tracks for this slot.</summary>
    public IReadOnlyList<AlternateViewModel> Alternates { get; }
    public bool HasAlternates => Alternates.Count > 0;

    public SlotCardViewModel(SetSlotInfo slot)
    {
        Slot = slot;
        Alternates = slot.Alternates.Select(a => new AlternateViewModel(a)).ToList();
    }
}

/// <summary>A Plan B track shown under a slot card and in the inspector.</summary>
public sealed class AlternateViewModel
{
    public SetAlternateInfo Info { get; }
    public Guid Id => Info.Id;
    public string Title => Info.Title;
    public string Artist => Info.Artist;
    public string Bpm => NumFormat.Bpm(Info.Bpm);
    public string Key => Info.CamelotKey ?? "—";

    public AlternateViewModel(SetAlternateInfo info) => Info = info;
}

/// <summary>Where a transition's assessment comes from, strongest first.</summary>
public enum TransitionEvidenceState
{
    /// <summary>Only the algorithmic pair score; nobody has rated it.</summary>
    Algorithmic,
    /// <summary>The DJ rated or played it at least once.</summary>
    Rated,
    /// <summary>Marked verified in the knowledge graph.</summary>
    Verified,
}

/// <summary>The transition chip between two adjacent slot cards (P2.4).</summary>
public sealed class TransitionChipViewModel : ReactiveObject
{
    public SlotCardViewModel From { get; }
    public SlotCardViewModel To { get; }

    /// <summary>Combined compatibility 0-100 (same scorer as Flow Builder and the player).</summary>
    public double Score { get; }
    public string ScoreText => Score.ToString("0", CultureInfo.InvariantCulture);
    public IReadOnlyList<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;
    public TransitionEvidenceState Evidence { get; }
    public bool IsVerified => Evidence == TransitionEvidenceState.Verified;
    public bool IsRated => Evidence == TransitionEvidenceState.Rated;

    public string ToolTip { get; }

    /// <summary>"Verified (3 ratings)", "Rated (1 rating)" or "Not rated yet: …".</summary>
    public string EvidenceText { get; }

    // Breakdown for the inspector (0-100 each), with the scorer's labels.
    public double HarmonicScore { get; }
    public string HarmonicLabel { get; }
    public double BpmScore { get; }
    public string BpmLabel { get; }
    public double EnergyScore { get; }
    public string EnergyLabel { get; }
    public double StyleScore { get; }
    public string StyleLabel { get; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

    /// <summary>The transition saved for this pair in this set (transition editor), if any.</summary>
    public SLSKDONET.Models.Timeline.PlaylistTrackTransition? SavedTransition { get; }
    public bool HasSavedTransition => SavedTransition is not null;

    /// <summary>The saved transition's intent tag (D5), when one is saved.</summary>
    public SLSKDONET.Services.Transitions.TransitionIntent? SavedIntent => SavedTransition is { } saved
        ? SLSKDONET.Services.Transitions.TransitionIntents.Of(saved.Type, saved.PresetName)
        : null;

    /// <summary>"Saved: Drop swap · Drop Sync · 32 bars" (the preset only when it adds something), or a prompt to design one.</summary>
    public string SavedTransitionText
    {
        get
        {
            if (SavedTransition is not { } saved || SavedIntent is not { } intent)
                return "No saved transition yet: playback would use the Auto plan";
            var label = SLSKDONET.Services.Transitions.TransitionIntents.Label(intent);
            var preset = string.IsNullOrWhiteSpace(saved.PresetName) || string.Equals(saved.PresetName, label, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" · {saved.PresetName}";
            return $"Saved: {label}{preset} · {saved.DurationBars.ToString(CultureInfo.InvariantCulture)} bars";
        }
    }

    public TransitionChipViewModel(SlotCardViewModel from, SlotCardViewModel to, TransitionKnowledgeInfo? knowledge,
        SLSKDONET.Models.Timeline.PlaylistTrackTransition? savedTransition = null)
    {
        SavedTransition = savedTransition;
        From = from;
        To = to;

        var pair = TrackPairCompatibilityScorer.Score(
            from.Slot.CamelotKey, to.Slot.CamelotKey,
            from.Slot.Energy / 100.0, to.Slot.Energy / 100.0,
            outgoingBpm: from.Slot.Bpm, incomingBpm: to.Slot.Bpm);
        Score = pair.CombinedScore;
        Warnings = TrackPairCompatibilityScorer.BuildWarnings(pair, from.Slot.Bpm, to.Slot.Bpm);
        (HarmonicScore, HarmonicLabel) = (pair.HarmonicScore, pair.HarmonicLabel);
        (BpmScore, BpmLabel) = (pair.BpmScore, pair.BpmLabel);
        (EnergyScore, EnergyLabel) = (pair.EnergyScore, pair.EnergyLabel);
        (StyleScore, StyleLabel) = (pair.GenreScore, pair.GenreLabel);

        Evidence = knowledge switch
        {
            { Verified: true } => TransitionEvidenceState.Verified,
            { EvidenceCount: > 0 } => TransitionEvidenceState.Rated,
            _ => TransitionEvidenceState.Algorithmic,
        };

        var evidenceLine = Evidence switch
        {
            TransitionEvidenceState.Verified => $"Verified ({CountFormat.Of(knowledge!.EvidenceCount, "rating")})",
            TransitionEvidenceState.Rated => $"Rated ({CountFormat.Of(knowledge!.EvidenceCount, "rating")})",
            _ => "Not rated yet: score is from key, BPM and energy only",
        };
        EvidenceText = evidenceLine;
        var lines = new List<string>
        {
            $"{from.Title} → {to.Title}",
            $"Compatibility {ScoreText}/100 · harmonic {pair.HarmonicLabel}, BPM {pair.BpmLabel}, energy {pair.EnergyLabel}",
            evidenceLine,
            SavedTransitionText,
        };
        lines.AddRange(Warnings);
        ToolTip = string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Undo/redo for set edits. Each edit is already persisted; undo and redo run the inverse
/// service calls. Local to the Sets page (the Workstation keeps its own stack).
/// </summary>
internal sealed class SetEditHistory
{
    private const int MaxEntries = 200;

    private sealed record Edit(string Description, Func<Task> Undo, Func<Task> Redo);

    private readonly LinkedList<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(string description, Func<Task> undo, Func<Task> redo)
    {
        _undo.AddLast(new Edit(description, undo, redo));
        if (_undo.Count > MaxEntries)
            _undo.RemoveFirst();
        _redo.Clear();
    }

    /// <summary>Undoes the latest edit; returns its description, or null when there was nothing.</summary>
    public async Task<string?> UndoAsync()
    {
        if (_undo.Last is not { } node)
            return null;
        _undo.RemoveLast();
        await node.Value.Undo();
        _redo.Push(node.Value);
        return node.Value.Description;
    }

    /// <summary>Redoes the latest undone edit; returns its description, or null when there was nothing.</summary>
    public async Task<string?> RedoAsync()
    {
        if (!_redo.TryPop(out var edit))
            return null;
        await edit.Redo();
        _undo.AddLast(edit);
        return edit.Description;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
