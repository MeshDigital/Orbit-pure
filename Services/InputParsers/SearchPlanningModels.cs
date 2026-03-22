using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services.InputParsers;

public enum SearchQueryLane
{
    Strict,
    Standard,
    Desperate
}

public sealed record PlannedSearchLane(SearchQueryLane Lane, string Query);

public sealed record TargetMetadata(
    string? Artist,
    string? Title,
    string? Album = null,
    int? DurationSeconds = null)
{
    public string NormalizedArtist => (Artist ?? string.Empty).Trim();
    public string NormalizedTitle => (Title ?? string.Empty).Trim();
    public bool HasArtist => !string.IsNullOrWhiteSpace(NormalizedArtist);
    public bool HasTitle => !string.IsNullOrWhiteSpace(NormalizedTitle);
}

public sealed record SearchPlan(
    TargetMetadata Target,
    string? StrictQuery,
    string? StandardQuery,
    string? DesperateQuery)
{
    public IReadOnlyList<PlannedSearchLane> EnumerateLanes()
        => new[]
            {
                new PlannedSearchLane(SearchQueryLane.Strict, StrictQuery ?? string.Empty),
                new PlannedSearchLane(SearchQueryLane.Standard, StandardQuery ?? string.Empty),
                new PlannedSearchLane(SearchQueryLane.Desperate, DesperateQuery ?? string.Empty)
            }
            .Where(lane => !string.IsNullOrWhiteSpace(lane.Query))
            .Select(lane => lane with { Query = lane.Query.Trim() })
            .GroupBy(lane => lane.Query, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(lane => lane.Lane).First())
            .ToList();

    public IReadOnlyList<string> EnumerateQueries()
        => EnumerateLanes()
            .Select(lane => lane.Query)
            .ToList();
}