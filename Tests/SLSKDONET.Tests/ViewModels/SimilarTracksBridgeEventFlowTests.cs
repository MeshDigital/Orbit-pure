using System;
using Microsoft.Extensions.Logging;
using Moq;
using ReactiveUI;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Similarity;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class SimilarTracksBridgeEventFlowTests
{
    [Fact]
    public void BridgeInsertOnly_TwoClicks_PublishesInsertEvent_WithoutProjectAdd()
    {
        var eventBus = new EventBusService();
        var logger = new Mock<ILogger>();

        InsertBridgeTrackBetweenEvent? insertEvt = null;
        using var insertSub = MessageBus.Current.Listen<InsertBridgeTrackBetweenEvent>()
            .Subscribe(e => insertEvt = e);

        AddToProjectRequestEvent? addEvt = null;
        using var addSub = eventBus.GetEvent<AddToProjectRequestEvent>()
            .Subscribe(e => addEvt = e);

        var row = new SimilarTrackRowViewModel(
            new SimilarTrack("bridge_hash", 0.91),
            db: null!,
            logger: logger.Object,
            eventBus: eventBus)
        {
            IsBridgeBetweenCandidate = true,
            BridgeFromHash = "from_hash",
            BridgeToHash = "to_hash",
            AddToProjectOnBridgeInsert = false
        };

        // First click arms confirmation only
        row.AddToPlaylistCommand.Execute(null);
        Assert.True(row.IsInsertConfirmArmed);
        Assert.Null(insertEvt);
        Assert.Null(addEvt);

        // Second click confirms insertion
        row.AddToPlaylistCommand.Execute(null);
        Assert.False(row.IsInsertConfirmArmed);
        Assert.NotNull(insertEvt);
        Assert.Equal("from_hash", insertEvt!.FromTrackHash);
        Assert.Equal("to_hash", insertEvt.ToTrackHash);
        Assert.Equal("bridge_hash", insertEvt.BridgeTrack.TrackUniqueHash);
        Assert.Null(addEvt);

        row.Dispose();
        eventBus.Dispose();
    }

    [Fact]
    public void BridgeInsertAndAdd_TwoClicks_PublishesInsertAndProjectAdd()
    {
        var eventBus = new EventBusService();
        var logger = new Mock<ILogger>();

        InsertBridgeTrackBetweenEvent? insertEvt = null;
        using var insertSub = MessageBus.Current.Listen<InsertBridgeTrackBetweenEvent>()
            .Subscribe(e => insertEvt = e);

        AddToProjectRequestEvent? addEvt = null;
        using var addSub = eventBus.GetEvent<AddToProjectRequestEvent>()
            .Subscribe(e => addEvt = e);

        var row = new SimilarTrackRowViewModel(
            new SimilarTrack("bridge_hash_2", 0.88),
            db: null!,
            logger: logger.Object,
            eventBus: eventBus)
        {
            IsBridgeBetweenCandidate = true,
            BridgeFromHash = "from_hash_2",
            BridgeToHash = "to_hash_2",
            AddToProjectOnBridgeInsert = true
        };

        row.AddToPlaylistCommand.Execute(null);
        row.AddToPlaylistCommand.Execute(null);

        Assert.NotNull(insertEvt);
        Assert.NotNull(addEvt);

        row.Dispose();
        eventBus.Dispose();
    }
}
