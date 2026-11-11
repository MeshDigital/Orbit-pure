using System;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Sets;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>
/// A slot rendered as a card in the set lane. Role / energy / notes are mutable and persist
/// immediately through the service, so the inspector's edits round-trip without a reload.
/// </summary>
public sealed class SlotViewModel : ReactiveObject
{
    private readonly ISetProjectService _sets;

    public Guid SlotId { get; }
    public string Title { get; }

    private SetSlotRole _role;
    public SetSlotRole Role
    {
        get => _role;
        set
        {
            this.RaiseAndSetIfChanged(ref _role, value);
            _ = _sets.UpdateSlotAsync(SlotId, value, EnergyTarget);
        }
    }

    private double? _energyTarget;
    public double? EnergyTarget
    {
        get => _energyTarget;
        set
        {
            this.RaiseAndSetIfChanged(ref _energyTarget, value);
            _ = _sets.UpdateSlotAsync(SlotId, Role, value);
        }
    }

    private string _notes = string.Empty;
    public string Notes
    {
        get => _notes;
        set
        {
            this.RaiseAndSetIfChanged(ref _notes, value);
            _ = _sets.UpdateSlotNotesAsync(SlotId, value);
        }
    }

    public SlotViewModel(ISetProjectService sets, Guid slotId, string title, SetSlotRole role, double? energy, string notes)
    {
        _sets = sets;
        SlotId = slotId;
        Title = title;
        _role = role;
        _energyTarget = energy;
        _notes = notes ?? string.Empty;
    }
}
