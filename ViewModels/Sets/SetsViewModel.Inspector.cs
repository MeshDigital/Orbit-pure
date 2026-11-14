using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Sets;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>A suggested bridge track between two slots (0-1 section scores from the bridge engine).</summary>
public sealed class BridgeSuggestionViewModel
{
    public SLSKDONET.Services.Sets.SetPoolTrack Track { get; }
    public string TrackUniqueHash => Track.TrackUniqueHash;
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Bpm => SLSKDONET.Utils.NumFormat.Bpm(Track.Bpm);
    public string Key => Track.CamelotKey ?? "—";
    public double Score { get; }
    public string ScoreText => (Score * 100).ToString("0", CultureInfo.InvariantCulture);
    public string Detail { get; }

    public BridgeSuggestionViewModel(SLSKDONET.Services.Sets.SetPoolTrack track, double score, double aToX, double xToB)
    {
        Track = track;
        Score = score;
        Detail = $"into it {(aToX * 100).ToString("0", CultureInfo.InvariantCulture)} · out of it {(xToB * 100).ToString("0", CultureInfo.InvariantCulture)}";
    }
}

/// <summary>One "Suggest next" entry for the inspector (#205 P4.2).</summary>
public sealed class NextSuggestionViewModel
{
    public NextSuggestion Suggestion { get; }
    public string TrackUniqueHash => Suggestion.Track.TrackUniqueHash;
    public string Title => Suggestion.Track.Title;
    public string Artist => Suggestion.Track.Artist;
    public string Bpm => SLSKDONET.Utils.NumFormat.Bpm(Suggestion.Track.Bpm);
    public string Key => Suggestion.Track.CamelotKey ?? "—";
    public string ScoreText => Suggestion.Score.ToString("0", CultureInfo.InvariantCulture);
    public string Reason => Suggestion.Reason;
    public string Basis => Suggestion.BasisLabel;

    public NextSuggestionViewModel(NextSuggestion suggestion) => Suggestion = suggestion;
}

/// <summary>One role chip in the slot inspector; checking it changes the selected slot's role.</summary>
public sealed class RoleOptionViewModel : ReactiveObject
{
    private readonly Func<SetSlotRole, Task> _apply;

    public SetSlotRole Role { get; }
    public string Label { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            this.RaiseAndSetIfChanged(ref _isChecked, value);
            if (value)
                _ = _apply(Role); // reports its own failures
        }
    }

    public RoleOptionViewModel(SetSlotRole role, Func<SetSlotRole, Task> apply)
    {
        Role = role;
        Label = role.ToString();
        _apply = apply;
    }

    /// <summary>Reflects the stored role without triggering an edit.</summary>
    internal void Sync(bool isChecked)
    {
        if (_isChecked == isChecked)
            return;
        _isChecked = isChecked;
        this.RaisePropertyChanged(nameof(IsChecked));
    }
}

/// <summary>
/// The inspector (rebuild plan P2.6): edits the selected slot's role, energy target and notes,
/// or shows the selected transition's score breakdown. Edits are persisted and undoable.
/// </summary>
public sealed partial class SetsViewModel
{
    private IReadOnlyList<RoleOptionViewModel>? _roleOptions;
    private (Guid From, Guid To)? _selectedTransitionKey;
    private double _energyDraft = 50;
    private string _notesDraft = string.Empty;

    /// <summary>Role chips for the slot inspector, in set-arc order.</summary>
    public IReadOnlyList<RoleOptionViewModel> RoleOptions => _roleOptions ??= Enum.GetValues<SetSlotRole>()
        .Select(role => new RoleOptionViewModel(role, SetRoleAsync))
        .ToList();

    /// <summary>The selected transition chip, if any (mutually exclusive with a selected slot).</summary>
    public TransitionChipViewModel? SelectedTransition => _selectedTransitionKey is { } key
        ? LaneItems.OfType<TransitionChipViewModel>().FirstOrDefault(c => c.From.SlotId == key.From && c.To.SlotId == key.To)
        : null;

    public bool HasSelectedTransition => SelectedTransition is not null;
    public bool ShowInspectorHint => HasSlots && !HasSelectedSlot && !HasSelectedTransition;

    /// <summary>Slider value for the energy target (0-100). Committed on release, not per tick.</summary>
    public double EnergyDraft
    {
        get => _energyDraft;
        set
        {
            // Only the slider calls this (SyncInspector writes the field), so any set is a user change.
            _energyDirty = true;
            this.RaiseAndSetIfChanged(ref _energyDraft, Math.Round(Math.Clamp(value, 0, 100)));
            this.RaisePropertyChanged(nameof(EnergyDraftText));
        }
    }

    /// <summary>"70", or "—" while the slot has no target.</summary>
    public string EnergyDraftText => SelectedSlot?.HasEnergyTarget == true || _energyDirty
        ? EnergyDraft.ToString("0", CultureInfo.InvariantCulture)
        : "—";

    private bool _energyDirty;

    /// <summary>DJ notes for the selected slot. Committed when the text box loses focus.</summary>
    public string NotesDraft
    {
        get => _notesDraft;
        set
        {
            if (_notesDraft == value)
                return;
            this.RaiseAndSetIfChanged(ref _notesDraft, value);
            _ = CommitNotesAsync();
        }
    }

    /// <summary>Selects a transition chip (clicked in the lane).</summary>
    public void SelectTransition(TransitionChipViewModel? chip)
    {
        _selectedTransitionKey = chip is null ? null : (chip.From.SlotId, chip.To.SlotId);
        if (chip is not null && _selectedSlotId is not null)
        {
            _selectedSlotId = null;
            foreach (var slot in Slots)
                slot.IsSelected = false;
        }
        RaiseSlotSelectionChanged();
    }

    /// <summary>Writes the slider's value as the slot's energy target (slider released).</summary>
    public async Task CommitEnergyAsync()
    {
        if (SelectedSlot is not { } card || !_energyDirty)
            return;
        var target = EnergyDraft;
        if (card.Slot.EnergyTarget == target)
            return;
        await UpdateSlotWithUndoAsync(card, "Set energy target", card.Slot.Role, target);
    }

    /// <summary>Removes the slot's energy target.</summary>
    public async Task ClearEnergyTargetAsync()
    {
        if (SelectedSlot is not { HasEnergyTarget: true } card)
            return;
        await UpdateSlotWithUndoAsync(card, "Clear energy target", card.Slot.Role, null);
    }

    private async Task SetRoleAsync(SetSlotRole role)
    {
        if (SelectedSlot is not { } card || card.Slot.Role == role)
            return;
        await UpdateSlotWithUndoAsync(card, $"Set role {role}", role, card.Slot.EnergyTarget);
    }

    private async Task UpdateSlotWithUndoAsync(SlotCardViewModel card, string what, SetSlotRole role, double? energy)
    {
        var slotId = card.SlotId;
        var (oldRole, oldEnergy) = (card.Slot.Role, card.Slot.EnergyTarget);
        await RunEditAsync(what, async () =>
        {
            if (!await _sets.UpdateSlotAsync(slotId, role, energy))
                throw new InvalidOperationException("That track is no longer in the set.");
            _history.Push(what,
                undo: async () => await _sets.UpdateSlotAsync(slotId, oldRole, oldEnergy),
                redo: async () => await _sets.UpdateSlotAsync(slotId, role, energy));
        });
    }

    /// <summary>
    /// Creates a transition editor (the same one Flow Builder and the player use) loaded with the
    /// chip's two slots, saving into this set (rebuild plan P3.1). The caller shows it and disposes
    /// it when closed. Null when it can't be opened; the reason is toasted.
    /// </summary>
    public async Task<MixTransitionViewModel?> CreateTransitionEditorAsync(TransitionChipViewModel? chip = null)
    {
        chip ??= SelectedTransition;
        if (chip is null || SelectedSet is not { } set)
            return null;
        if (_services?.GetService(typeof(MixTransitionViewModel)) is not MixTransitionViewModel editor)
        {
            _logger?.LogError("[Sets] MixTransitionViewModel isn't registered; can't open the transition editor");
            _notifications?.Show("Sets", "The transition editor isn't available right now.", Views.NotificationType.Error);
            return null;
        }

        try
        {
            if (await editor.LoadSetPairAsync(set.Id, chip.From.SlotId, chip.From.Slot.TrackUniqueHash,
                    chip.To.SlotId, chip.To.Slot.TrackUniqueHash))
                return editor;

            _notifications?.Show("Sets", editor.StatusMessage ?? "Couldn't open that transition.", Views.NotificationType.Warning);
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't open the transition editor", ex);
        }
        editor.Dispose();
        return null;
    }

    /// <summary>Called when the transition editor closes: picks up saved transitions and ratings.</summary>
    public Task TransitionEditorClosedAsync() => LoadAsync();

    // ── Find a bridge (rebuild plan P4.3, DJ-initiated only) ─────────────────

    /// <summary>Bridge suggestions for the selected transition, best first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<BridgeSuggestionViewModel> BridgeSuggestions { get; } = new();

    private bool _isFindingBridge;
    public bool IsFindingBridge { get => _isFindingBridge; private set => this.RaiseAndSetIfChanged(ref _isFindingBridge, value); }

    private string? _bridgeStatus;
    /// <summary>What the last search found (or why it found nothing).</summary>
    public string? BridgeStatus { get => _bridgeStatus; private set => this.RaiseAndSetIfChanged(ref _bridgeStatus, value); }

    private (Guid From, Guid To)? _bridgeFor;

    /// <summary>BPM window (each side) around both tracks when looking for a bridge.</summary>
    private const double BridgeBpmBand = 0.06;

    /// <summary>
    /// Looks for library tracks that connect the selected transition's outgoing outro to its
    /// incoming intro (section-vector match, the engine Flow Builder uses; P4.1 found the
    /// knowledge-graph finder has no verified edges to walk yet).
    /// </summary>
    public async Task FindBridgeAsync()
    {
        if (SelectedTransition is not { } chip || SelectedSet is null)
            return;
        BridgeSuggestions.Clear();
        _bridgeFor = (chip.From.SlotId, chip.To.SlotId);
        if (_sectionVectors is null)
        {
            BridgeStatus = "Bridge search isn't available right now.";
            return;
        }

        IsFindingBridge = true;
        BridgeStatus = null;
        try
        {
            var from = chip.From.Slot;
            var to = chip.To.Slot;
            var bpms = new[] { from.Bpm, to.Bpm }.Where(b => b is > 0).Select(b => b!.Value).ToList();
            var inSet = Slots.Select(s => s.Slot.TrackUniqueHash).ToHashSet(StringComparer.Ordinal);

            var pool = await _sets.SearchPoolAsync(new SetPoolQuery(
                AnalysedOnly: true,
                BpmMin: bpms.Count > 0 ? bpms.Min() * (1 - BridgeBpmBand) : null,
                BpmMax: bpms.Count > 0 ? bpms.Max() * (1 + BridgeBpmBand) : null,
                // Generous cap: the pool query orders by title, so a small cap would only ever offer the A–C tracks.
                Limit: 3000));
            var candidates = pool.Where(t => !inSet.Contains(t.TrackUniqueHash)).ToList();
            var byHash = candidates.ToDictionary(t => t.TrackUniqueHash, StringComparer.Ordinal);

            var ranked = await _sectionVectors.FindBridgeCandidatesAsync(
                from.TrackUniqueHash, to.TrackUniqueHash, byHash.Keys);
            if (_bridgeFor != (chip.From.SlotId, chip.To.SlotId))
                return; // the selection moved on

            foreach (var (hash, bridgeScore, aToX, xToB) in ranked.Take(8))
            {
                if (byHash.TryGetValue(hash, out var track))
                    BridgeSuggestions.Add(new BridgeSuggestionViewModel(track, bridgeScore, aToX, xToB));
            }

            BridgeStatus = BridgeSuggestions.Count > 0
                ? $"Best of {CountFormat.Of(candidates.Count, "analysed track")} near this BPM, by how well the outro, bridge and intro sections fit."
                : candidates.Count == 0
                    ? "No analysed tracks near this BPM that aren't already in the set."
                    : $"None of {CountFormat.Of(candidates.Count, "analysed track")} near this BPM has phrase data that connects these two.";
        }
        catch (Exception ex)
        {
            ReportFailure("Bridge search failed", ex);
        }
        finally
        {
            IsFindingBridge = false;
        }
    }

    // ── Suggest next (rebuild plan P4.2, #205) ───────────────────────────────

    /// <summary>What to play after the selected slot, strongest evidence first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NextSuggestionViewModel> NextSuggestions { get; } = new();

    private bool _isSuggestingNext;
    public bool IsSuggestingNext { get => _isSuggestingNext; private set => this.RaiseAndSetIfChanged(ref _isSuggestingNext, value); }

    private string? _nextStatus;
    /// <summary>What the last "Suggest next" found, or why it found nothing.</summary>
    public string? NextStatus { get => _nextStatus; private set => this.RaiseAndSetIfChanged(ref _nextStatus, value); }

    private Guid? _nextFor;

    /// <summary>
    /// Ranks library tracks to play after the selected slot: transitions you played or rated, then
    /// knowledge-scan candidates, then key/BPM/section matches. Each entry carries its reason.
    /// Suggestions only: nothing changes until "Insert after".
    /// </summary>
    public async Task SuggestNextAsync()
    {
        if (SelectedSlot is not { } card || SelectedSet is null)
            return;
        NextSuggestions.Clear();
        _nextFor = card.SlotId;
        IsSuggestingNext = true;
        NextStatus = null;
        try
        {
            var from = card.Slot;
            var fromTrack = new SetPoolTrack(from.TrackUniqueHash, from.Title, from.Artist, from.Bpm, from.CamelotKey);
            var inSet = Slots.Select(s => s.Slot.TrackUniqueHash).ToHashSet(StringComparer.Ordinal);

            var pool = (await _sets.SearchPoolAsync(new SetPoolQuery(
                    AnalysedOnly: from.Bpm is > 0,
                    BpmMin: from.Bpm is double lo && lo > 0 ? lo * (1 - NextTrackRanker.BpmWindow) : null,
                    BpmMax: from.Bpm is double hi && hi > 0 ? hi * (1 + NextTrackRanker.BpmWindow) : null,
                    Limit: 3000)))
                .Where(t => !inSet.Contains(t.TrackUniqueHash))
                .ToList();
            var known = await _sets.GetKnownNextAsync(from.TrackUniqueHash);

            var fit = new Dictionary<string, double>(StringComparer.Ordinal);
            if (_sectionVectors is not null && pool.Count > 0)
                foreach (var (hash, score) in await _sectionVectors.FindNextCandidatesAsync(from.TrackUniqueHash, pool.Select(t => t.TrackUniqueHash)))
                    fit[hash] = score;
            if (_nextFor != card.SlotId)
                return; // the selection moved on

            foreach (var s in NextTrackRanker.Rank(fromTrack, pool, known, fit))
                NextSuggestions.Add(new NextSuggestionViewModel(s));

            NextStatus = NextSuggestions.Count > 0
                ? (known.Any(k => k.FromEvidence)
                    ? "Transitions you've played come first, then matches by key, BPM and how the outro flows into the intro."
                    : "No played transitions out of this track yet: matched by key, BPM and how its outro flows into the intro.")
                : from.Bpm is not > 0
                    ? "Analyse this track first: suggestions need its BPM (or transitions you've played out of it)."
                    : "No library tracks within 6 % of this BPM that aren't already in the set.";
        }
        catch (Exception ex)
        {
            ReportFailure("Suggest next failed", ex);
        }
        finally
        {
            IsSuggestingNext = false;
        }
    }

    /// <summary>Puts a suggestion in a new slot right after the selected one.</summary>
    public async Task InsertSuggestionAfterAsync(NextSuggestionViewModel suggestion)
    {
        if (SelectedSlot is not { } card)
            return;
        NextSuggestions.Clear();
        NextStatus = null;
        await InsertTrackAsync(suggestion.TrackUniqueHash, card.Index + 1);
    }
    /// <summary>Inserts a bridge suggestion between the selected transition's two slots.</summary>
    public async Task InsertBridgeAsync(BridgeSuggestionViewModel bridge)
    {
        if (SelectedTransition is not { } chip)
            return;
        BridgeSuggestions.Clear();
        BridgeStatus = null;
        await InsertTrackAsync(bridge.TrackUniqueHash, chip.To.Index);
    }

    /// <summary>Adds a pool track as a Plan B for the selected slot.</summary>
    public async Task AddAlternateFromPoolAsync(PoolTrackViewModel track)
    {
        if (SelectedSlot is not { } card)
        {
            _notifications?.Show("Sets", "Select a slot first, then add its Plan B.", Views.NotificationType.Information);
            return;
        }
        if (track.TrackUniqueHash == card.Slot.TrackUniqueHash || card.Alternates.Any(a => a.Info.TrackUniqueHash == track.TrackUniqueHash))
        {
            _notifications?.Show("Sets", $"\"{track.Title}\" is already in this slot.", Views.NotificationType.Information);
            return;
        }

        var slotId = card.SlotId;
        await RunEditAsync("Add Plan B", async () =>
        {
            var added = await _sets.AddAlternateAsync(slotId, track.TrackUniqueHash)
                ?? throw new InvalidOperationException("That slot is no longer in the set.");
            SetSlotAlternateEntity? removed = null;
            _history.Push($"Add Plan B \"{track.Title}\"",
                undo: async () => removed = await _sets.RemoveAlternateAsync(added.Id),
                redo: async () =>
                {
                    if (removed is not null)
                        await _sets.RestoreAlternateAsync(removed);
                });
        });
    }

    /// <summary>Removes a Plan B track from its slot.</summary>
    public async Task RemoveAlternateAsync(AlternateViewModel alternate)
    {
        await RunEditAsync("Remove Plan B", async () =>
        {
            var snapshot = await _sets.RemoveAlternateAsync(alternate.Id)
                ?? throw new InvalidOperationException("That Plan B is already gone.");
            _history.Push($"Remove Plan B \"{alternate.Title}\"",
                undo: async () => await _sets.RestoreAlternateAsync(snapshot),
                redo: async () => await _sets.RemoveAlternateAsync(snapshot.Id));
        });
    }

    /// <summary>Plays the Plan B in the slot instead; the planned track becomes the Plan B.</summary>
    public async Task SwapAlternateAsync(AlternateViewModel alternate)
    {
        await RunEditAsync("Swap in Plan B", async () =>
        {
            if (!await _sets.SwapAlternateAsync(alternate.Id))
                throw new InvalidOperationException("That Plan B is no longer in the set.");
            _history.Push($"Swap in \"{alternate.Title}\"",
                undo: async () => await _sets.SwapAlternateAsync(alternate.Id), // a swap undoes itself
                redo: async () => await _sets.SwapAlternateAsync(alternate.Id));
        });
    }

    private async Task CommitNotesAsync()
    {
        if (SelectedSlot is not { } card)
            return;
        var notes = string.IsNullOrWhiteSpace(_notesDraft) ? null : _notesDraft.Trim();
        var old = card.Slot.Notes;
        if (notes == old)
            return;
        var slotId = card.SlotId;
        await RunEditAsync("Edit notes", async () =>
        {
            if (!await _sets.UpdateSlotNotesAsync(slotId, notes))
                throw new InvalidOperationException("That track is no longer in the set.");
            _history.Push("Edit notes",
                undo: async () => await _sets.UpdateSlotNotesAsync(slotId, old),
                redo: async () => await _sets.UpdateSlotNotesAsync(slotId, notes));
        });
    }

    /// <summary>Copies the selected slot's stored values into the inspector without editing anything.</summary>
    private void SyncInspector()
    {
        var card = SelectedSlot;
        foreach (var option in RoleOptions)
            option.Sync(card is not null && option.Role == card.Slot.Role);

        _energyDirty = false;
        _energyDraft = card?.Slot.EnergyTarget ?? card?.Slot.Energy ?? 50;
        _notesDraft = card?.Slot.Notes ?? string.Empty;
        this.RaisePropertyChanged(nameof(EnergyDraft));
        this.RaisePropertyChanged(nameof(EnergyDraftText));
        this.RaisePropertyChanged(nameof(NotesDraft));

        if (_selectedTransitionKey is not null && SelectedTransition is null)
            _selectedTransitionKey = null; // the pair no longer exists after an edit
        foreach (var chip in LaneItems.OfType<TransitionChipViewModel>())
            chip.IsSelected = _selectedTransitionKey is { } key && chip.From.SlotId == key.From && chip.To.SlotId == key.To;
        this.RaisePropertyChanged(nameof(SelectedTransition));
        this.RaisePropertyChanged(nameof(HasSelectedTransition));
        this.RaisePropertyChanged(nameof(ShowInspectorHint));
    }
}
