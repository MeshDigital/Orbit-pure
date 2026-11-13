using SLSKDONET.ViewModels;

namespace SLSKDONET.Events;

/// <summary>
/// Published by MainViewModel when navigation lands on Cue Forge from another page. Carries a way
/// back to that page ("Library · Neuro", "Sets", …), which Cue Forge shows as "← Back to …" unless
/// the visit was opened with a more specific return point (Flow Builder's transition, the Mix panel).
/// </summary>
public sealed record CueForgeVisitStartedEvent(CueForgeReturnPoint? PreviousPage);
