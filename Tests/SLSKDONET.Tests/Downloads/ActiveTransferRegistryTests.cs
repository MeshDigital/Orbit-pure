using SLSKDONET.Models;
using SLSKDONET.Services.Downloads;
using SLSKDONET.Services.Models;
using Xunit;

namespace SLSKDONET.Tests.Downloads;

public class ActiveTransferRegistryTests
{
    private static DownloadContext Ctx(string title) => new(new PlaylistTrack { Artist = "A", Title = title });

    [Fact]
    public void SameUsernameDifferentPaths_AreIndependent()
    {
        var reg = new ActiveTransferRegistry();
        var a = new ActiveTransferRegistry.TransferKey("alice", "a.mp3");
        var b = new ActiveTransferRegistry.TransferKey("alice", "b.mp3");
        var ctxA = Ctx("A");
        var ctxB = Ctx("B");

        reg.Register(a, ctxA);
        reg.Register(b, ctxB);

        Assert.True(reg.TryGet(a, out var gotA));
        Assert.Same(ctxA, gotA);
        Assert.True(reg.TryGet(b, out var gotB));
        Assert.Same(ctxB, gotB);
        Assert.True(reg.HasAnyForUser("alice"));
    }

    [Fact]
    public void DeltaForOne_DoesNotChangeOther()
    {
        var reg = new ActiveTransferRegistry();
        var a = new ActiveTransferRegistry.TransferKey("alice", "a.mp3");
        var b = new ActiveTransferRegistry.TransferKey("alice", "b.mp3");
        reg.Register(a, Ctx("A"));
        reg.Register(b, Ctx("B"));

        Assert.Equal(1000, reg.RecordBytes(a, 1000));
        Assert.Equal(500, reg.RecordBytes(a, 1500));
        Assert.Equal(2000, reg.RecordBytes(b, 2000)); // b's first sample is its own baseline
        Assert.Equal(0, reg.RecordBytes(a, 1500)); // no advance
    }

    [Fact]
    public void RemovingOne_LeavesTheOther()
    {
        var reg = new ActiveTransferRegistry();
        var a = new ActiveTransferRegistry.TransferKey("alice", "a.mp3");
        var b = new ActiveTransferRegistry.TransferKey("alice", "b.mp3");
        reg.Register(a, Ctx("A"));
        reg.Register(b, Ctx("B"));

        reg.Remove(a);
        Assert.False(reg.TryGet(a, out _));
        Assert.True(reg.TryGet(b, out _));
        Assert.True(reg.HasAnyForUser("alice"));
    }

    [Fact]
    public void UsernameComparison_IsCaseInsensitive()
    {
        var reg = new ActiveTransferRegistry();
        var key = new ActiveTransferRegistry.TransferKey("Alice", "a.mp3");
        reg.Register(key, Ctx("A"));
        Assert.True(reg.TryGet(new ActiveTransferRegistry.TransferKey("alice", "a.mp3"), out _));
        Assert.True(reg.HasAnyForUser("ALICE"));
    }

    [Fact]
    public void PathComparison_IsCaseSensitive()
    {
        var reg = new ActiveTransferRegistry();
        var key = new ActiveTransferRegistry.TransferKey("alice", "A.mp3");
        reg.Register(key, Ctx("A"));
        Assert.False(reg.TryGet(new ActiveTransferRegistry.TransferKey("alice", "a.mp3"), out _));
    }
}