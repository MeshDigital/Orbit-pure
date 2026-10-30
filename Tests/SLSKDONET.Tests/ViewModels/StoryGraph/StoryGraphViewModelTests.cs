using System;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.StoryGraph;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.StoryGraph;

public class StoryGraphViewModelTests
{
    private static SetTrackEntity Slot(int position, SetSlotRole role = SetSlotRole.Builder, double? energy = null)
        => new() { Id = Guid.NewGuid(), Position = position, SlotRole = role, EnergyTarget = energy };

    private static StoryGraphViewModel Create()
        => new(null!, new UndoService());

    [Fact]
    public void SetSlotRole_RecomputesLayout()
    {
        var vm = Create();
        var a = Slot(0, SetSlotRole.Opening);
        var b = Slot(1, SetSlotRole.Builder);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotRole(b.Id, SetSlotRole.Peak);

        Assert.Equal(SetSlotRole.Peak, vm.Layout!.Nodes[1].Role);
    }

    [Fact]
    public void SetSlotRole_Undo_RestoresOriginalRole()
    {
        var vm = Create();
        var a = Slot(0, SetSlotRole.Opening);
        var b = Slot(1, SetSlotRole.Builder);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotRole(b.Id, SetSlotRole.Peak);
        vm.Undo();

        Assert.Equal(SetSlotRole.Builder, vm.Layout!.Nodes[1].Role);
    }

    [Fact]
    public void SetSlotRole_Redo_ReappliesRole()
    {
        var vm = Create();
        var a = Slot(0);
        var b = Slot(1);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotRole(b.Id, SetSlotRole.Closer);
        vm.Undo();
        vm.Redo();

        Assert.Equal(SetSlotRole.Closer, vm.Layout!.Nodes[1].Role);
    }

    [Fact]
    public void SetSlotEnergyTarget_Undo_RestoresOriginal()
    {
        var vm = Create();
        var a = Slot(0);
        var b = Slot(1, energy: 40.0);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotEnergyTarget(b.Id, 85.0);
        Assert.Equal(85.0, vm.Layout!.Nodes[1].EnergyTarget);

        vm.Undo();
        Assert.Equal(40.0, vm.Layout!.Nodes[1].EnergyTarget);
    }

    [Fact]
    public void CanUndo_TracksMutations()
    {
        var vm = Create();
        var a = Slot(0);
        var b = Slot(1);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        Assert.False(vm.CanUndo);
        vm.SetSlotRole(b.Id, SetSlotRole.Peak);
        Assert.True(vm.CanUndo);
        vm.Undo();
        Assert.False(vm.CanUndo);
    }
}
