using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.StoryGraph;

/// <summary>A slot rendered as a node on the Story Graph canvas.</summary>
public sealed record StoryGraphNode(Guid SlotId, double X, double Y, SetSlotRole Role, double? EnergyTarget);

/// <summary>
/// A rendered edge: a primary spine connector (straight) or a branch arc (quadratic, control point
/// above/below the spine). <see cref="Probability"/> is meaningful only for branch edges.
/// </summary>
public sealed record StoryGraphEdge(
    Guid FromSlotId,
    Guid ToSlotId,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    double ControlY,
    double Probability,
    bool IsPrimary);

public sealed record StoryGraphLayoutResult(IReadOnlyList<StoryGraphNode> Nodes, IReadOnlyList<StoryGraphEdge> Edges);

/// <summary>
/// Slice K5: pure Story Graph geometry. Lays out a SetProject's slots along a horizontal primary
/// spine (by <see cref="SetTrackEntity.Position"/>) and routes <see cref="SetStoryEdgeEntity"/>
/// branches as arcs above/below it. Deterministic and unit-testable; the Avalonia view only renders
/// this geometry, so no layout logic lives in XAML/code-behind.
/// </summary>
public static class StoryGraphLayout
{
    public const double ColumnWidth = 160.0;
    public const double BranchOffset = 72.0;

    public static StoryGraphLayoutResult Compute(
        IReadOnlyList<SetTrackEntity> slots,
        IReadOnlyList<SetStoryEdgeEntity> edges)
    {
        var ordered = slots.OrderBy(s => s.Position).ToList();
        var indexById = ordered
            .Select((s, i) => (s.Id, i))
            .ToDictionary(x => x.Id, x => x.i);

        var nodes = ordered
            .Select((s, i) => new StoryGraphNode(s.Id, i * ColumnWidth, 0.0, s.SlotRole, s.EnergyTarget))
            .ToList();

        var resultEdges = new List<StoryGraphEdge>();

        // Primary spine: consecutive slots, straight connectors.
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            resultEdges.Add(new StoryGraphEdge(
                ordered[i].Id,
                ordered[i + 1].Id,
                i * ColumnWidth, 0.0,
                (i + 1) * ColumnWidth, 0.0,
                ControlY: 0.0,
                Probability: 1.0,
                IsPrimary: true));
        }

        // Branch edges: non-adjacent slots. Forward skips arc up (negative Y), back-edges arc down,
        // and multiple branches from the same slot stack outward to avoid overlap.
        var branchLane = new Dictionary<int, int>();
        foreach (var edge in edges)
        {
            if (!indexById.TryGetValue(edge.FromSlotId, out var fromIndex) ||
                !indexById.TryGetValue(edge.ToSlotId, out var toIndex))
                continue;

            if (Math.Abs(toIndex - fromIndex) <= 1)
                continue; // already the primary spine

            var lane = branchLane.TryGetValue(fromIndex, out var existing) ? existing : 0;
            branchLane[fromIndex] = lane + 1;

            var sign = toIndex > fromIndex ? -1.0 : 1.0;   // forward skip = up, back-edge = down
            var controlY = sign * (BranchOffset + lane * BranchOffset);

            resultEdges.Add(new StoryGraphEdge(
                edge.FromSlotId,
                edge.ToSlotId,
                fromIndex * ColumnWidth, 0.0,
                toIndex * ColumnWidth, 0.0,
                controlY,
                edge.Probability,
                IsPrimary: false));
        }

        return new StoryGraphLayoutResult(nodes, resultEdges);
    }
}
