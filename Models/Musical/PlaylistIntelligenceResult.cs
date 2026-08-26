using System;
using System.Collections.Generic;

namespace SLSKDONET.Models.Musical;

public sealed class PlaylistRecommendation
{
    public string TrackHash { get; init; } = string.Empty;
    public double Score { get; init; }
    public double SimilarityScore { get; init; }
    public double HarmonicScore { get; init; }
    public double TransitionScore { get; init; }
    public double EnergyFitScore { get; init; }
    public IReadOnlyList<string> ReasonTags { get; init; } = Array.Empty<string>();
}

public sealed class PlaylistReorderResult
{
    public IReadOnlyList<string> OrderedTrackHashes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PlaylistRecommendation> TransitionRecommendations { get; init; } = Array.Empty<PlaylistRecommendation>();
    public double AverageTransitionScore { get; init; }
}

/// <summary>
/// Per-track metadata that TrackFingerprint doesn't carry (artist, subgenre) but that
/// ReorderAsync's repetition-avoidance scoring needs — same reasoning as the separate
/// bpmByHash lookup passed for double-drop scoring.
/// </summary>
public sealed record ReorderTrackMetadata(string? Artist, string? SubGenre);