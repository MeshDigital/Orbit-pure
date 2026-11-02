using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Sets;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>A set in the Sets rail.</summary>
public sealed class SetListItemViewModel : ReactiveObject
{
    public Guid Id { get; }

    private string _name;
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

    public SetProjectStatus Status { get; }
    public int SlotCount { get; }

    /// <summary>"12 tracks · Draft".</summary>
    public string Summary => $"{CountFormat.Of(SlotCount, "track")} · {Status}";

    public SetListItemViewModel(SetSummary summary)
    {
        Id = summary.Id;
        _name = summary.Name;
        Status = summary.Status;
        SlotCount = summary.SlotCount;
    }
}

/// <summary>
/// The Sets page (rebuild plan Phase 2): a rail of every SetProject with create / rename /
/// duplicate / delete, and the selected set as a lane of slot cards joined by transition chips.
/// Every edit is persisted at once through <see cref="ISetProjectService"/> and recorded in a
/// Sets-local undo history; failures are logged, toasted and shown inline, never swallowed.
/// </summary>
public sealed class SetsViewModel : ReactiveObject, IDisposable
{
    private readonly ISetProjectService _sets;
    private readonly IDialogService _dialogs;
    private readonly INotificationService? _notifications;
    private readonly ILogger<SetsViewModel>? _logger;
    private readonly SetEditHistory _history = new();
    private int _slotLoadGeneration;
    private Guid? _pendingOpen;
    private bool _rebuildingRail;

    public ObservableCollection<SetListItemViewModel> Sets { get; } = new();

    /// <summary>The selected set's slots in spine order.</summary>
    public ObservableCollection<SlotCardViewModel> Slots { get; } = new();

    /// <summary>Slot cards interleaved with the transition chips between them, for the lane.</summary>
    public ObservableCollection<object> LaneItems { get; } = new();

    private SetListItemViewModel? _selectedSet;
    public SetListItemViewModel? SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (ReferenceEquals(_selectedSet, value))
                return;
            // Rebuilding the rail clears the list, and the ListBox's TwoWay binding then pushes
            // null; that isn't the user leaving the set (it would wipe undo history on every edit).
            if (_rebuildingRail && value is null)
                return;
            var switchedSet = _selectedSet?.Id != value?.Id;
            this.RaiseAndSetIfChanged(ref _selectedSet, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(ShowNoSelection));
            if (switchedSet)
            {
                _history.Clear(); // edits are per set
                _selectedSlotId = null;
                RaiseUndoStateChanged();
            }
            PendingSlotLoad = LoadSlotsAsync();
        }
    }

    /// <summary>The slot load started by the last selection change (awaited by tests).</summary>
    internal Task PendingSlotLoad { get; private set; } = Task.CompletedTask;

    public bool HasSelection => SelectedSet is not null;

    private Guid? _selectedSlotId;
    /// <summary>The selected slot card, if any.</summary>
    public SlotCardViewModel? SelectedSlot => Slots.FirstOrDefault(s => s.SlotId == _selectedSlotId);
    public bool HasSelectedSlot => SelectedSlot is not null;

    private bool _isLoadingSets;
    public bool IsLoadingSets { get => _isLoadingSets; private set => this.RaiseAndSetIfChanged(ref _isLoadingSets, value); }

    private bool _setsLoaded;
    private bool _isLoadingSlots;
    public bool IsLoadingSlots
    {
        get => _isLoadingSlots;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoadingSlots, value);
            this.RaisePropertyChanged(nameof(IsSetEmpty));
            this.RaisePropertyChanged(nameof(HasSlots));
        }
    }

    private string? _errorMessage;
    /// <summary>Set when loading or an edit failed; shown inline instead of an empty page.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>No sets exist at all (rail + page empty state).</summary>
    public bool HasNoSets => _setsLoaded && Sets.Count == 0;

    /// <summary>Sets exist but none is selected.</summary>
    public bool ShowNoSelection => _setsLoaded && Sets.Count > 0 && SelectedSet is null;

    /// <summary>The selected set has no slots yet.</summary>
    public bool IsSetEmpty => HasSelection && !IsLoadingSlots && !HasError && Slots.Count == 0;

    public bool HasSlots => HasSelection && !IsLoadingSlots && Slots.Count > 0;

    /// <summary>"12 tracks · Draft" for the selected set's header.</summary>
    public string SelectedSummary => SelectedSet is { } set ? $"{CountFormat.Of(Slots.Count, "track")} · {set.Status}" : string.Empty;

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    public ReactiveCommand<Unit, Unit> NewSetCommand { get; }
    public ReactiveCommand<Unit, Unit> RenameSetCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateSetCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSetCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    public ReactiveCommand<Unit, Unit> SelectPreviousSlotCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectNextSlotCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveSlotLeftCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveSlotRightCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveSlotCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

    public SetsViewModel(
        ISetProjectService sets,
        IDialogService dialogs,
        INotificationService? notifications = null,
        ILogger<SetsViewModel>? logger = null)
    {
        _sets = sets;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;

        var hasSelection = this.WhenAnyValue(x => x.SelectedSet).Select(s => s is not null);
        var hasSlot = this.WhenAnyValue(x => x.HasSelectedSlot);
        NewSetCommand = ReactiveCommand.CreateFromTask(NewSetAsync);
        RenameSetCommand = ReactiveCommand.CreateFromTask(RenameSetAsync, hasSelection);
        DuplicateSetCommand = ReactiveCommand.CreateFromTask(DuplicateSetAsync, hasSelection);
        DeleteSetCommand = ReactiveCommand.CreateFromTask(DeleteSetAsync, hasSelection);
        RetryCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());

        SelectPreviousSlotCommand = ReactiveCommand.Create(() => SelectRelative(-1));
        SelectNextSlotCommand = ReactiveCommand.Create(() => SelectRelative(+1));
        MoveSlotLeftCommand = ReactiveCommand.CreateFromTask(() => MoveSelectedAsync(-1), hasSlot);
        MoveSlotRightCommand = ReactiveCommand.CreateFromTask(() => MoveSelectedAsync(+1), hasSlot);
        RemoveSlotCommand = ReactiveCommand.CreateFromTask(RemoveSelectedAsync, hasSlot);
        UndoCommand = ReactiveCommand.CreateFromTask(UndoAsync, this.WhenAnyValue(x => x.CanUndo));
        RedoCommand = ReactiveCommand.CreateFromTask(RedoAsync, this.WhenAnyValue(x => x.CanRedo));
    }

    /// <summary>
    /// Asks the next <see cref="LoadAsync"/> to select <paramref name="setId"/>. Used when another
    /// surface routes here: the page's own load-on-attach then opens the right set.
    /// </summary>
    public void RequestOpen(Guid setId) => _pendingOpen = setId;

    /// <summary>Reloads the rail, keeping (or switching to) a selection when it still exists.</summary>
    public async Task LoadAsync(Guid? select = null)
    {
        var keep = select ?? _pendingOpen ?? SelectedSet?.Id;
        _pendingOpen = null;
        IsLoadingSets = true;
        ErrorMessage = null;
        try
        {
            var sets = await _sets.GetSetsAsync();

            _rebuildingRail = true;
            try
            {
                Sets.Clear();
                foreach (var summary in sets)
                    Sets.Add(new SetListItemViewModel(summary));
            }
            finally
            {
                _rebuildingRail = false;
            }

            _setsLoaded = true;
            var target = Sets.FirstOrDefault(s => s.Id == keep);
            if (target is not null && !ReferenceEquals(target, SelectedSet))
                SelectedSet = target;
            else if (target is null)
                SelectedSet = null;
            else
                PendingSlotLoad = LoadSlotsAsync(); // same set re-selected: refresh its slots
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't load your sets", ex);
        }
        finally
        {
            IsLoadingSets = false;
            RaiseStateChanged();
        }
    }

    /// <summary>Opens the page on a specific set.</summary>
    public Task OpenSetAsync(Guid setId) => LoadAsync(setId);

    /// <summary>Selects a slot card (clicked or keyboard-navigated).</summary>
    public void SelectSlot(SlotCardViewModel? card)
    {
        _selectedSlotId = card?.SlotId;
        foreach (var slot in Slots)
            slot.IsSelected = slot.SlotId == _selectedSlotId;
        this.RaisePropertyChanged(nameof(SelectedSlot));
        this.RaisePropertyChanged(nameof(HasSelectedSlot));
    }

    /// <summary>Moves a slot to a new index (drag-drop in the lane), with undo.</summary>
    public async Task MoveSlotAsync(Guid slotId, int newIndex)
    {
        var from = Slots.FirstOrDefault(s => s.SlotId == slotId)?.Index;
        if (from is not int oldIndex)
            return;
        newIndex = Math.Clamp(newIndex, 0, Math.Max(0, Slots.Count - 1));
        if (newIndex == oldIndex)
            return;

        await RunEditAsync("Move track", async () =>
        {
            if (!await _sets.MoveSlotAsync(slotId, newIndex))
                throw new InvalidOperationException("That track is no longer in the set.");
            _history.Push("Move track",
                undo: async () => await _sets.MoveSlotAsync(slotId, oldIndex),
                redo: async () => await _sets.MoveSlotAsync(slotId, newIndex));
            _selectedSlotId = slotId;
        });
    }

    /// <summary>Inserts a library track at <paramref name="index"/> (dropped or added from the pool), with undo.</summary>
    public async Task InsertTrackAsync(string trackUniqueHash, int index)
    {
        if (SelectedSet is not { } set)
            return;

        await RunEditAsync("Add track", async () =>
        {
            var slot = await _sets.InsertTrackAsync(set.Id, trackUniqueHash, index)
                ?? throw new InvalidOperationException($"\"{set.Name}\" no longer exists.");
            SetTrackEntity? removed = null;
            _history.Push("Add track",
                undo: async () => removed = await _sets.RemoveSlotAsync(slot.Id),
                redo: async () =>
                {
                    if (removed is not null)
                        await _sets.RestoreSlotAsync(removed);
                });
            _selectedSlotId = slot.Id;
        });
    }

    private async Task LoadSlotsAsync()
    {
        var generation = ++_slotLoadGeneration;
        ErrorMessage = null;

        if (SelectedSet is not { } set)
        {
            Slots.Clear();
            LaneItems.Clear();
            RaiseStateChanged();
            return;
        }

        IsLoadingSlots = true;
        try
        {
            var slots = await _sets.GetSlotsAsync(set.Id);
            var pairs = slots.Zip(slots.Skip(1), (a, b) => (a.TrackUniqueHash, b.TrackUniqueHash)).ToList();
            var knowledge = pairs.Count == 0
                ? Array.Empty<TransitionKnowledgeInfo>()
                : await _sets.GetKnowledgeAsync(pairs);
            if (generation != _slotLoadGeneration)
                return; // the user picked another set meanwhile

            BuildLane(slots, knowledge);
        }
        catch (Exception ex)
        {
            if (generation == _slotLoadGeneration)
                ReportFailure($"Couldn't open \"{set.Name}\"", ex);
        }
        finally
        {
            if (generation == _slotLoadGeneration)
            {
                IsLoadingSlots = false;
                RaiseStateChanged();
            }
        }
    }

    private void BuildLane(IReadOnlyList<SetSlotInfo> slots, IReadOnlyList<TransitionKnowledgeInfo> knowledge)
    {
        var byPair = knowledge.ToDictionary(k => (k.SourceTrackHash, k.TargetTrackHash));

        Slots.Clear();
        LaneItems.Clear();
        SlotCardViewModel? previous = null;
        foreach (var slot in slots)
        {
            var card = new SlotCardViewModel(slot) { IsSelected = slot.SlotId == _selectedSlotId };
            if (previous is not null)
            {
                byPair.TryGetValue((previous.Slot.TrackUniqueHash, slot.TrackUniqueHash), out var known);
                LaneItems.Add(new TransitionChipViewModel(previous, card, known));
            }
            Slots.Add(card);
            LaneItems.Add(card);
            previous = card;
        }

        if (_selectedSlotId is Guid id && Slots.All(s => s.SlotId != id))
            _selectedSlotId = null;
        this.RaisePropertyChanged(nameof(SelectedSlot));
        this.RaisePropertyChanged(nameof(HasSelectedSlot));
    }

    private void SelectRelative(int delta)
    {
        if (Slots.Count == 0)
            return;
        var index = SelectedSlot is { } current ? Slots.IndexOf(current) + delta : (delta > 0 ? 0 : Slots.Count - 1);
        SelectSlot(Slots[Math.Clamp(index, 0, Slots.Count - 1)]);
    }

    private Task MoveSelectedAsync(int delta)
        => SelectedSlot is { } card ? MoveSlotAsync(card.SlotId, card.Index + delta) : Task.CompletedTask;

    private async Task RemoveSelectedAsync()
    {
        if (SelectedSlot is not { } card)
            return;
        var neighbour = Slots.ElementAtOrDefault(card.Index + 1) ?? Slots.ElementAtOrDefault(card.Index - 1);

        await RunEditAsync("Remove track", async () =>
        {
            var snapshot = await _sets.RemoveSlotAsync(card.SlotId)
                ?? throw new InvalidOperationException("That track is no longer in the set.");
            _history.Push($"Remove \"{card.Title}\"",
                undo: async () => await _sets.RestoreSlotAsync(snapshot),
                redo: async () => await _sets.RemoveSlotAsync(snapshot.Id));
            _selectedSlotId = neighbour?.SlotId;
        });
    }

    private async Task UndoAsync()
    {
        await RunEditAsync("Undo", async () =>
        {
            var what = await _history.UndoAsync();
            if (what is not null)
                _notifications?.Show("Sets", $"Undid: {what}", NotificationType.Information, TimeSpan.FromSeconds(2));
        });
    }

    private async Task RedoAsync()
    {
        await RunEditAsync("Redo", async () =>
        {
            var what = await _history.RedoAsync();
            if (what is not null)
                _notifications?.Show("Sets", $"Redid: {what}", NotificationType.Information, TimeSpan.FromSeconds(2));
        });
    }

    /// <summary>Runs a persisted edit, then reloads the lane and rail counts; failures are reported.</summary>
    private async Task RunEditAsync(string what, Func<Task> edit)
    {
        string? failure = null;
        try
        {
            await edit();
        }
        catch (Exception ex)
        {
            ReportFailure($"{what} failed", ex);
            failure = ErrorMessage;
        }

        RaiseUndoStateChanged();
        await LoadAsync(); // refresh slots (positions, chips) and the rail's slot counts
        await PendingSlotLoad;
        if (failure is not null && ErrorMessage is null)
            ErrorMessage = failure; // the reload clears it; keep the failure visible
    }

    private async Task NewSetAsync()
    {
        try
        {
            var name = await _dialogs.ShowPromptAsync("New set", "Name the set", $"Set {DateTime.Now:MMM d}");
            if (name is null)
                return; // cancelled
            var created = await _sets.CreateSetAsync(name);
            await LoadAsync(created.Id);
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't create the set", ex);
        }
    }

    private async Task RenameSetAsync()
    {
        if (SelectedSet is not { } set)
            return;
        try
        {
            var name = await _dialogs.ShowPromptAsync("Rename set", "Set name", set.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == set.Name)
                return;
            if (!await _sets.RenameSetAsync(set.Id, name))
            {
                _notifications?.Show("Sets", $"\"{set.Name}\" no longer exists.", NotificationType.Warning);
                await LoadAsync();
                return;
            }
            set.Name = name.Trim();
            await LoadAsync(set.Id); // re-sort: renaming bumps LastModifiedAt
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't rename the set", ex);
        }
    }

    private async Task DuplicateSetAsync()
    {
        if (SelectedSet is not { } set)
            return;
        try
        {
            var copy = await _sets.DuplicateSetAsync(set.Id);
            if (copy is null)
            {
                _notifications?.Show("Sets", $"\"{set.Name}\" no longer exists.", NotificationType.Warning);
                await LoadAsync();
                return;
            }
            await LoadAsync(copy.Id);
            _notifications?.Show("Sets", $"Duplicated as \"{copy.Name}\".", NotificationType.Success);
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't duplicate the set", ex);
        }
    }

    private async Task DeleteSetAsync()
    {
        if (SelectedSet is not { } set)
            return;
        try
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Delete set",
                $"Delete \"{set.Name}\" and its {CountFormat.Of(set.SlotCount, "slot")}? Tracks stay in your library. This can't be undone.",
                "Delete",
                "Cancel");
            if (!confirmed)
                return;

            if (!await _sets.DeleteSetAsync(set.Id))
                _notifications?.Show("Sets", $"\"{set.Name}\" was already deleted.", NotificationType.Warning);

            SelectedSet = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't delete the set", ex);
        }
    }

    private void ReportFailure(string message, Exception ex)
    {
        _logger?.LogError(ex, "[Sets] {Message}", message);
        ErrorMessage = $"{message}: {ex.Message}";
        _notifications?.Show("Sets", ErrorMessage, NotificationType.Error);
    }

    private void RaiseUndoStateChanged()
    {
        this.RaisePropertyChanged(nameof(CanUndo));
        this.RaisePropertyChanged(nameof(CanRedo));
    }

    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasNoSets));
        this.RaisePropertyChanged(nameof(ShowNoSelection));
        this.RaisePropertyChanged(nameof(IsSetEmpty));
        this.RaisePropertyChanged(nameof(HasSlots));
        this.RaisePropertyChanged(nameof(SelectedSummary));
    }

    public void Dispose()
    {
        NewSetCommand.Dispose();
        RenameSetCommand.Dispose();
        DuplicateSetCommand.Dispose();
        DeleteSetCommand.Dispose();
        RetryCommand.Dispose();
        SelectPreviousSlotCommand.Dispose();
        SelectNextSlotCommand.Dispose();
        MoveSlotLeftCommand.Dispose();
        MoveSlotRightCommand.Dispose();
        RemoveSlotCommand.Dispose();
        UndoCommand.Dispose();
        RedoCommand.Dispose();
    }
}
