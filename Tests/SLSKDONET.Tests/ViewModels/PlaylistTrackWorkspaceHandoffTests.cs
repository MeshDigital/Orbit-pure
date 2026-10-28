using System.Collections.Generic;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>Library-to-Workstation handoffs prepare a deck without changing playback.</summary>
public class PlaylistTrackWorkspaceHandoffTests
{
    [Fact]
    public void OpenWorkstation_PublishesDeckRequestWithoutPlayingTrack()
    {
        using var bus = new EventBusService();
        var requests = new List<OpenStemWorkspaceRequestEvent>();
        var playbackRequests = new List<PlayTrackRequestEvent>();
        var notifications = new List<NotificationEvent>();
        using var requestSubscription = bus.GetEvent<OpenStemWorkspaceRequestEvent>().Subscribe(requests.Add);
        using var playbackSubscription = bus.GetEvent<PlayTrackRequestEvent>().Subscribe(playbackRequests.Add);
        using var notificationSubscription = bus.GetEvent<NotificationEvent>().Subscribe(notifications.Add);
        using var track = CreateTrack(bus);

        Assert.True(track.OpenWorkstationCommand.CanExecute(null));
        track.OpenWorkstationCommand.Execute(null);

        var request = Assert.Single(requests);
        Assert.Same(track.Model, request.Track);
        Assert.Empty(playbackRequests);
        var notification = Assert.Single(notifications);
        Assert.Equal("Workstation", notification.Title);
        Assert.Equal(NotificationType.Information, notification.Type);
        Assert.Contains("without starting playback", notification.Message);
    }

    [Fact]
    public void OpenWorkstation_RemainsAvailableWhenCompletedTrackNeedsRelink()
    {
        using var bus = new EventBusService();
        OpenStemWorkspaceRequestEvent? request = null;
        using var subscription = bus.GetEvent<OpenStemWorkspaceRequestEvent>().Subscribe(evt => request = evt);
        using var track = CreateTrack(bus);
        track.Model.ResolvedFilePath = string.Empty;

        Assert.True(track.NeedsFileRepair);
        Assert.True(track.OpenWorkstationCommand.CanExecute(null));
        track.OpenWorkstationCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Same(track.Model, request!.Track);
    }

    private static PlaylistTrackViewModel CreateTrack(IEventBus bus) => new(new PlaylistTrack
    {
        PlaylistId = System.Guid.NewGuid(),
        TrackUniqueHash = "workspace-handoff-hash",
        Artist = "Artist",
        Title = "Track",
        Status = TrackStatus.Downloaded,
        AvailabilityState = TrackAvailabilityState.LocalUnanalyzed,
        ResolvedFilePath = @"C:\Music\track.flac",
    }, bus);
}
