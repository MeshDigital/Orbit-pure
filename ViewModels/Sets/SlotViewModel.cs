using SLSKDONET.Data.Entities;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>A slot rendered as a card in the set lane.</summary>
public sealed record SlotViewModel(Guid SlotId, string Title, SetSlotRole Role, double? EnergyTarget);
