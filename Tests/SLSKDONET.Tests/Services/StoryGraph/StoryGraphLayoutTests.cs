using System;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.StoryGraph;
using Xunit;

namespace SLSKDONET.Tests.Services.StoryGraph;

public class StoryGraphLayoutTests
{
    private static SetTrackEntity Slot(int position, SetSlotRole role = SetSlotRole.Builder, double? energy = null)
        => new() { Id = Guid.NewGuid(), Position = position, SlotRole = role, EnergyTarget = energy };

    [Fact]
    public void Compute_PrimarySpine_LaysNodesLeftToRight()
    {
        var a = Slot(0, SetSlotRole.Opening, 30);
        var b = Slot(1, SetSlotRole.Peak, 90);
        var c = Slot(2, SetSlotRole.Closer, 40);

        var result = StoryGraphLayout.Compute(new[] { a, b, c }, Array.Empty<SetStoryEdgeEntity>());

        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(0.0, result.Nodes[0].X, 6);
        Assert.Equal(StoryGraphLayout.ColumnWidth, result.Nodes[1].X, 6);
        Assert.Equal(2 * StoryGraphLayout.ColumnWidth, result.Nodes[2].X, 6);
        Assert.Equal(SetSlotRole.Peak, result.Nodes[1].Role);
        Assert.Equal(90.0, result.Nodes[1].EnergyTarget);

        Assert.Equal(2, result.Edges.Count);
        Assert.All(result.Edges, e => Assert.True(e.IsPrimary));
    }

    [Fact]
    public void Compute_ForwardSkip_ArcsUp()
    {
        var a = Slot(0);
        var b = Slot(1);
        var c = Slot(2);
        var branch = new SetStoryEdgeEntity { FromSlotId = a.Id, ToSlotId = c.Id, Probability = 0.7 };

        var result = StoryGraphLayout.Compute(new[] { a, b, c }, new[] { branch });

        var branchEdge = Assert.Single(result.Edges.Where(e => !e.IsPrimary));
        Assert.True(branchEdge.ControlY < 0, "a forward skip should arc above the spine");
        Assert.Equal(0.7, branchEdge.Probability, 6);
    }

    [Fact]
    public void Compute_BackEdge_ArcsDown()
    {
        var a = Slot(0);
        var b = Slot(1);
        var c = Slot(2);
        var branch = new SetStoryEdgeEntity { FromSlotId = c.Id, ToSlotId = a.Id };

        var result = StoryGraphLayout.Compute(new[] { a, b, c }, new[] { branch });

        var branchEdge = Assert.Single(result.Edges.Where(e => !e.IsPrimary));
        Assert.True(branchEdge.ControlY > 0, "a back-edge should arc below the spine");
    }

    [Fact]
    public void Compute_AdjacentEdge_IsNotDuplicatedAsBranch()
    {
        var a = Slot(0);
        var b = Slot(1);
        var redundant = new SetStoryEdgeEntity { FromSlotId = a.Id, ToSlotId = b.Id };

        var result = StoryGraphLayout.Compute(new[] { a, b }, new[] { redundant });

        Assert.Single(result.Edges); // only the primary spine connector
    }

    [Fact]
    public void Compute_MultipleBranchesFromSameSlot_StackOutward()
    {
        var a = Slot(0);
        var b = Slot(1);
        var c = Slot(2);
        var d = Slot(3);
        var first = new SetStoryEdgeEntity { FromSlotId = a.Id, ToSlotId = c.Id };
        var second = new SetStoryEdgeEntity { FromSlotId = a.Id, ToSlotId = d.Id };

        var result = StoryGraphLayout.Compute(new[] { a, b, c, d }, new[] { first, second });

        var branches = result.Edges.Where(e => !e.IsPrimary).ToList();
        Assert.Equal(2, branches.Count);
        Assert.NotEqual(branches[0].ControlY, branches[1].ControlY);
    }
}
