using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.StoryGraph;
using SLSKDONET.ViewModels.SetHealth;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>
/// Slice K5: view model for the Set Projects Story Graph canvas. Loads a SetProject's slots +
/// <see cref="SetStoryEdgeEntity"/> branches, computes the <see cref="StoryGraphLayout"/> geometry,
/// exposes slot selection, and lets the inspector mutate slot role/energy with undo/redo.
/// </summary>
public sealed class StoryGraphViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IUndoService _undoService;

    private List<SetTrackEntity> _slots = new();
    private List<SetStoryEdgeEntity> _edges = new();

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
            this.RaisePropertyChanged(nameof(SelectedSlotRole));
            this.RaisePropertyChanged(nameof(SelectedSlotEnergyTarget));
        }
    }

    /// <summary>The selected slot's role; setting it mutates the slot through the undo stack.</summary>
    public SetSlotRole SelectedSlotRole
    {
        get => _slots.FirstOrDefault(s => s.Id == SelectedSlotId)?.SlotRole ?? SLSKDONET.Data.Entities.SetSlotRole.Builder;
        set
        {
            if (SelectedSlotId is Guid id)
                SetSlotRole(id, value);
        }
    }

    /// <summary>The selected slot's energy target; setting it mutates the slot through the undo stack.</summary>
    public double? SelectedSlotEnergyTarget
    {
        get => _slots.FirstOrDefault(s => s.Id == SelectedSlotId)?.EnergyTarget;
        set
        {
            if (SelectedSlotId is Guid id)
                SetSlotEnergyTarget(id, value);
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

    public StoryGraphViewModel(IDbContextFactory<AppDbContext> dbFactory, IUndoService undoService)
    {
        _dbFactory = dbFactory;
        _undoService = undoService;
        Health = new SetHealthDashboardViewModel(dbFactory);
        UndoCommand = ReactiveCommand.Create(Undo);
        RedoCommand = ReactiveCommand.Create(Redo);
    }

    public async Task LoadSetAsync(Guid setProjectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setProjectId)
            .ConfigureAwait(false);

        if (set is null)
        {
            Load(Array.Empty<SetTrackEntity>(), Array.Empty<SetStoryEdgeEntity>());
        }
        else
        {
            var edges = await db.SetStoryEdges
                .Where(e => e.SetProjectId == setProjectId)
                .ToListAsync()
                .ConfigureAwait(false);

            Load(set.Tracks.ToList(), edges);
        }

        await Health.LoadAsync(setProjectId).ConfigureAwait(false);
    }

    /// <summary>Seeds the in-memory slots/edges and recomputes layout (also used by tests).</summary>
    internal void Load(IReadOnlyList<SetTrackEntity> slots, IReadOnlyList<SetStoryEdgeEntity> edges)
    {
        _slots = slots.ToList();
        _edges = edges.ToList();
        _undoService.Clear();
        Recompute();
        RaiseUndoStateChanged();
    }

    public void SetSlotRole(Guid slotId, SetSlotRole role)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == slotId);
        if (slot is null || slot.SlotRole == role)
            return;

        var previous = slot.SlotRole;
        var operation = new DelegateOperation(
            $"Set role {role}",
            apply: () => slot.SlotRole = role,
            revert: () => slot.SlotRole = previous);

        operation.Execute();
        _undoService.Push(operation);
        Recompute();
        RaiseUndoStateChanged();
        this.RaisePropertyChanged(nameof(SelectedSlotRole));
    }

    public void SetSlotEnergyTarget(Guid slotId, double? energy)
    {
        var slot = _slots.FirstOrDefault(s => s.Id == slotId);
        if (slot is null || slot.EnergyTarget == energy)
            return;

        var previous = slot.EnergyTarget;
        var operation = new DelegateOperation(
            $"Set energy {energy}",
            apply: () => slot.EnergyTarget = energy,
            revert: () => slot.EnergyTarget = previous);

        operation.Execute();
        _undoService.Push(operation);
        Recompute();
        RaiseUndoStateChanged();
        this.RaisePropertyChanged(nameof(SelectedSlotEnergyTarget));
    }

    public void Undo()
    {
        if (!_undoService.CanUndo)
            return;
        _undoService.Undo();
        Recompute();
        RaiseUndoStateChanged();
    }

    public void Redo()
    {
        if (!_undoService.CanRedo)
            return;
        _undoService.Redo();
        Recompute();
        RaiseUndoStateChanged();
    }

    private void Recompute() => Layout = StoryGraphLayout.Compute(_slots, _edges);

    private void RaiseUndoStateChanged()
    {
        this.RaisePropertyChanged(nameof(CanUndo));
        this.RaisePropertyChanged(nameof(CanRedo));
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
}
