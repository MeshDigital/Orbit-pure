using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Sets;
using SLSKDONET.Services.StoryGraph;
using SLSKDONET.ViewModels.SetHealth;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>
/// Slice K5: view model for the Set Projects Story Graph canvas. Loads a SetProject's slots +
/// <see cref="SetStoryEdgeEntity"/> branches, computes the <see cref="StoryGraphLayout"/> geometry,
/// exposes slot selection, and lets the inspector mutate slot role/energy with undo/redo.
/// <para>
/// Every role/energy change (including undo/redo) is written through <see cref="ISetProjectService"/>
/// at once, so a reload never drops edits. The undo stack is private to this surface — the global
/// <see cref="IUndoService"/> belongs to the Workstation.
/// </para>
/// </summary>
public sealed class StoryGraphViewModel : ReactiveObject, IDisposable
{
    /// <summary>Slider drags within this window collapse into one undo step.</summary>
    private static readonly TimeSpan EnergyCoalesceWindow = TimeSpan.FromMilliseconds(750);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISetProjectService _sets;
    private readonly ILogger<StoryGraphViewModel>? _logger;
    private readonly INotificationService? _notifications;
    private readonly UndoService _undoService = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private List<SetTrackEntity> _slots = new();
    private List<SetStoryEdgeEntity> _edges = new();
    private IReadOnlyDictionary<string, string> _trackLabels = new Dictionary<string, string>();
    private Guid? _setListId;

    private Task _pendingWrites = Task.CompletedTask;
    private EnergyOperation? _lastEnergyOperation;
    private DateTime _lastEnergyOperationAt;

    private StoryGraphLayoutResult? _layout;
    public StoryGraphLayoutResult? Layout
    {
        get => _layout;
        private set => this.RaiseAndSetIfChanged(ref _layout, value);
    }

    private Guid? _selectedSlotId;
    public Guid? SelectedSlotId
    {
        get => _selectedSlotId;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSlotId, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(SelectedSlotLabel));
            this.RaisePropertyChanged(nameof(SelectedSlotRole));
            this.RaisePropertyChanged(nameof(SelectedSlotEnergyTarget));
        }
    }

    /// <summary>True when a slot is selected; the role/energy inspector is disabled otherwise.</summary>
    public bool HasSelection => SelectedSlot is not null;

    /// <summary>True once a set is loaded but it has no slots yet (drives the empty state).</summary>
    public bool IsEmpty => _setListId is not null && _slots.Count == 0;

    /// <summary>"Artist – Title" of the selected slot, or a hint when nothing is selected.</summary>
    public string SelectedSlotLabel => SelectedSlot is { } slot
        ? GetSlotLabel(slot.Id)
        : "Click a slot to edit its role and energy";

    private SetTrackEntity? SelectedSlot => _slots.FirstOrDefault(s => s.Id == SelectedSlotId);

    /// <summary>The selected slot's role; setting it mutates the slot through the undo stack.</summary>
    public SetSlotRole SelectedSlotRole
    {
        get => SelectedSlot?.SlotRole ?? SLSKDONET.Data.Entities.SetSlotRole.Builder;
        set
        {
            if (SelectedSlotId is Guid id)
                SetSlotRole(id, value);
        }
    }

    /// <summary>The selected slot's energy target (0-100; unset slots show 50 on the slider).</summary>
    public double SelectedSlotEnergyTarget
    {
        get => SelectedSlot?.EnergyTarget ?? 50.0;
        set
        {
            if (SelectedSlotId is Guid id)
                SetSlotEnergyTarget(id, Math.Round(value));
        }
    }

    public bool CanUndo => _undoService.CanUndo;
    public bool CanRedo => _undoService.CanRedo;

    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

    /// <summary>All slot roles, for the inspector's role selector.</summary>
    public IReadOnlyList<SetSlotRole> RoleOptions { get; } = Enum.GetValues<SetSlotRole>();

    /// <summary>Set Health dashboard child — computed from the same SetProject.</summary>
    public SetHealthDashboardViewModel Health { get; }

    public StoryGraphViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        ISetProjectService sets,
        ILogger<StoryGraphViewModel>? logger = null,
        INotificationService? notifications = null)
    {
        _dbFactory = dbFactory;
        _sets = sets;
        _logger = logger;
        _notifications = notifications;
        Health = new SetHealthDashboardViewModel(dbFactory);
        UndoCommand = ReactiveCommand.Create(Undo);
        RedoCommand = ReactiveCommand.Create(Redo);
    }

    /// <summary>Display label for a slot's track (falls back to a short hash for unknown tracks).</summary>
    public string GetSlotLabel(Guid slotId)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == slotId);
        if (slot is null)
            return string.Empty;
        return _trackLabels.TryGetValue(slot.TrackUniqueHash, out var label)
            ? label
            : "Unknown track";
    }

    public async Task LoadSetAsync(Guid setProjectId)
    {
        try
        {
            await _writeGate.WaitAsync();
            try
            {
                await LoadSetCoreAsync(setProjectId);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't open the set", ex);
        }
    }

    private async Task LoadSetCoreAsync(Guid setProjectId)
    {
        _setListId = setProjectId;

        await using var db = await _dbFactory.CreateDbContextAsync();

        var set = await db.SetLists
            .AsNoTracking()
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setProjectId);

        if (set is null)
        {
            Load(Array.Empty<SetTrackEntity>(), Array.Empty<SetStoryEdgeEntity>());
        }
        else
        {
            var edges = await db.SetStoryEdges
                .AsNoTracking()
                .Where(e => e.SetProjectId == setProjectId)
                .ToListAsync();

            _trackLabels = await _sets.GetTrackLabelsAsync(set.Tracks.Select(t => t.TrackUniqueHash));
            Load(set.Tracks.ToList(), edges);
        }

        await Health.LoadAsync(setProjectId);
    }

    /// <summary>Seeds the in-memory slots/edges and recomputes layout (also used by tests).</summary>
    internal void Load(IReadOnlyList<SetTrackEntity> slots, IReadOnlyList<SetStoryEdgeEntity> edges)
    {
        _slots = slots.ToList();
        _edges = edges.ToList();
        _undoService.Clear();
        _lastEnergyOperation = null;
        if (SelectedSlotId is Guid id && _slots.All(s => s.Id != id))
            SelectedSlotId = null;
        Recompute();
        RaiseUndoStateChanged();
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(SelectedSlotLabel));
    }

    /// <summary>Appends a library track as a new slot and reloads the graph.</summary>
    public async Task AddTrackAsync(string trackUniqueHash)
    {
        if (_setListId is not Guid setId)
            return;

        try
        {
            // Same gate as slot writes: a pending role/energy write lands before the reload reads.
            await _writeGate.WaitAsync();
            try
            {
                var slot = await _sets.AppendTrackAsync(setId, trackUniqueHash);
                if (slot is null)
                {
                    _notifications?.Show("Add track", "This set no longer exists.", NotificationType.Warning);
                    return;
                }

                await LoadSetCoreAsync(setId);
                SelectedSlotId = slot.Id;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't add the track", ex);
        }
    }

    public void SetSlotRole(Guid slotId, SetSlotRole role)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == slotId);
        if (slot is null || slot.SlotRole == role)
            return;

        var previous = slot.SlotRole;
        Push(new DelegateOperation(
            $"Set role {role}",
            apply: () => { slot.SlotRole = role; Persist(slot); },
            revert: () => { slot.SlotRole = previous; Persist(slot); }));
        this.RaisePropertyChanged(nameof(SelectedSlotRole));
    }

    public void SetSlotEnergyTarget(Guid slotId, double? energy)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == slotId);
        if (slot is null || slot.EnergyTarget == energy)
            return;

        // A slider drag fires many changes: fold them into the previous step for the same slot.
        var now = DateTime.UtcNow;
        if (_lastEnergyOperation is { } last
            && last.Slot == slot
            && now - _lastEnergyOperationAt < EnergyCoalesceWindow)
        {
            last.To = energy;
            last.Execute();
            _lastEnergyOperationAt = now;
            Recompute();
        }
        else
        {
            var operation = new EnergyOperation(slot, slot.EnergyTarget, energy, Persist);
            Push(operation);
            _lastEnergyOperation = operation;
            _lastEnergyOperationAt = now;
        }

        this.RaisePropertyChanged(nameof(SelectedSlotEnergyTarget));
    }

    public void Undo()
    {
        if (!_undoService.CanUndo)
            return;
        _lastEnergyOperation = null;
        _undoService.Undo();
        Recompute();
        RaiseUndoStateChanged();
        RaiseSelectionValuesChanged();
    }

    /// <summary>Builds the picker used by the "Add Track" flow.</summary>
    public TrackPickerViewModel CreateTrackPicker() => new(_sets);

    public void Redo()
    {
        if (!_undoService.CanRedo)
            return;
        _lastEnergyOperation = null;
        _undoService.Redo();
        Recompute();
        RaiseUndoStateChanged();
        RaiseSelectionValuesChanged();
    }

    /// <summary>Completes once every queued slot write has been persisted (used by tests).</summary>
    internal Task FlushWritesAsync() => _pendingWrites;

    public void Dispose()
    {
        UndoCommand.Dispose();
        RedoCommand.Dispose();
        _writeGate.Dispose();
    }

    private void Push(IUndoableOperation operation)
    {
        _lastEnergyOperation = null;
        operation.Execute();
        _undoService.Push(operation);
        Recompute();
        RaiseUndoStateChanged();
    }

    /// <summary>
    /// Writes the slot's current role/energy. Writes are serialized and always send the latest
    /// in-memory values, so a burst of slider changes can't land out of order.
    /// </summary>
    private void Persist(SetTrackEntity slot) => _pendingWrites = Task.WhenAll(_pendingWrites, PersistAsync(slot));

    private async Task PersistAsync(SetTrackEntity slot)
    {
        try
        {
            await _writeGate.WaitAsync();
            try
            {
                if (!await _sets.UpdateSlotAsync(slot.Id, slot.SlotRole, slot.EnergyTarget))
                    _logger?.LogWarning("[StoryGraph] slot {Slot} no longer exists; edit not saved", slot.Id);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Surface torn down mid-write (app shutdown); nothing left to report to.
            _logger?.LogWarning("[StoryGraph] slot {Slot} write skipped: view model disposed", slot.Id);
        }
        catch (Exception ex)
        {
            ReportFailure("Couldn't save the slot change", ex);
        }
    }

    private void ReportFailure(string message, Exception ex)
    {
        _logger?.LogError(ex, "[StoryGraph] {Message}", message);
        _notifications?.Show("Set Projects", $"{message}: {ex.Message}", NotificationType.Error);
    }

    private void Recompute() => Layout = StoryGraphLayout.Compute(_slots, _edges);

    private void RaiseUndoStateChanged()
    {
        this.RaisePropertyChanged(nameof(CanUndo));
        this.RaisePropertyChanged(nameof(CanRedo));
    }

    private void RaiseSelectionValuesChanged()
    {
        this.RaisePropertyChanged(nameof(SelectedSlotRole));
        this.RaisePropertyChanged(nameof(SelectedSlotEnergyTarget));
    }

    private sealed class DelegateOperation : IUndoableOperation
    {
        private readonly Action _apply;
        private readonly Action _revert;

        public string Description { get; }

        public DelegateOperation(string description, Action apply, Action revert)
        {
            Description = description;
            _apply = apply;
            _revert = revert;
        }

        public void Execute() => _apply();
        public void Undo() => _revert();
    }

    /// <summary>Energy change whose target can be extended while a slider drag is in progress.</summary>
    private sealed class EnergyOperation : IUndoableOperation
    {
        private readonly double? _from;
        private readonly Action<SetTrackEntity> _persist;

        public SetTrackEntity Slot { get; }
        public double? To { get; set; }
        public string Description => $"Set energy {To}";

        public EnergyOperation(SetTrackEntity slot, double? from, double? to, Action<SetTrackEntity> persist)
        {
            Slot = slot;
            _from = from;
            To = to;
            _persist = persist;
        }

        public void Execute() { Slot.EnergyTarget = To; _persist(Slot); }
        public void Undo() { Slot.EnergyTarget = _from; _persist(Slot); }
    }
}
