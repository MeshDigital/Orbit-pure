using System;

namespace SLSKDONET.Models.Musical;

public sealed record SavedDouble(
    string TrackAId,
    string TrackBId,
    DateTime CreatedAt,
    double? CachedScore = null,
    string? Label = null);
