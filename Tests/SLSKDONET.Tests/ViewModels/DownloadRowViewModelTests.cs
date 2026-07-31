using System;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.Downloads;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Regression coverage for the Searching/Queued badge distinction: before this, a track
/// actively being searched showed the same grey "QUEUED" badge as one that hadn't started
/// at all, giving no visual signal that the app was doing anything.
/// </summary>
public class DownloadRowViewModelTests
{
    [Fact]
    public void SearchingState_MapsToDistinctSearchingStatus_NotQueued()
    {
        var track = CreateTrackViewModel(CreateTrack("hash-1", Guid.NewGuid()));
        track.State = PlaylistTrackState.Searching;

        using var row = new DownloadRowViewModel(track);

        Assert.Equal(DownloadRowStatus.Searching, row.Status);
        Assert.Equal("SEARCHING", row.StatusBadgeText);
        Assert.Equal(DownloadRowPriority.Active, row.Priority);
    }

    [Fact]
    public void SearchingState_PrimaryActionIsPause()
    {
        var track = CreateTrackViewModel(CreateTrack("hash-2", Guid.NewGuid()));
        track.State = PlaylistTrackState.Searching;

        using var row = new DownloadRowViewModel(track);

        Assert.Equal("Pause", row.PrimaryActionLabel);
        Assert.Same(track.PauseCommand, row.PrimaryAction);
    }

    [Fact]
    public void QueuedState_StillMapsToQueued()
    {
        var track = CreateTrackViewModel(CreateTrack("hash-3", Guid.NewGuid()));
        track.State = PlaylistTrackState.Queued;

        using var row = new DownloadRowViewModel(track);

        Assert.Equal(DownloadRowStatus.Queued, row.Status);
        Assert.Equal("QUEUED", row.StatusBadgeText);
        Assert.Equal(DownloadRowPriority.Active, row.Priority);
    }

    [Fact]
    public void TransitioningFromSearchingToDownloading_UpdatesStatusLive()
    {
        var track = CreateTrackViewModel(CreateTrack("hash-4", Guid.NewGuid()));
        track.State = PlaylistTrackState.Searching;

        using var row = new DownloadRowViewModel(track);
        Assert.Equal(DownloadRowStatus.Searching, row.Status);

        track.State = PlaylistTrackState.Downloading;

        Assert.Equal(DownloadRowStatus.Downloading, row.Status);
        Assert.Equal("DOWNLOADING", row.StatusBadgeText);
    }

    [Theory]
    [InlineData(PlaylistTrackState.Searching, "Searching", true)]
    [InlineData(PlaylistTrackState.Downloading, "Searching", false)]
    [InlineData(PlaylistTrackState.Downloading, "Downloading", true)]
    [InlineData(PlaylistTrackState.Failed, "Failed", true)]
    [InlineData(PlaylistTrackState.Stalled, "Failed", true)]
    [InlineData(PlaylistTrackState.Cancelled, "Failed", true)]
    [InlineData(PlaylistTrackState.Completed, "Completed", true)]
    [InlineData(PlaylistTrackState.Completed, "Downloading", false)]
    [InlineData(PlaylistTrackState.Searching, "All", true)]
    [InlineData(PlaylistTrackState.Completed, "All", true)]
    public void MatchesRowStatusFilter_MatchesExpectedStates(PlaylistTrackState state, string filter, bool expected)
    {
        var track = CreateTrackViewModel(CreateTrack("hash-filter", Guid.NewGuid()));
        track.State = state;

        Assert.Equal(expected, DownloadCenterViewModel.MatchesRowStatusFilter(track, filter));
    }

    private static UnifiedTrackViewModel CreateTrackViewModel(PlaylistTrack model)
    {
        var downloadManager = (DownloadManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadManager));
        var eventBus = new EventBusService();
        var artworkCache = (ArtworkCacheService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ArtworkCacheService));
        var libraryService = Mock.Of<ILibraryService>();
        var databaseService = (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService));
        var config = new AppConfig();

        return new UnifiedTrackViewModel(
            model,
            downloadManager,
            eventBus,
            artworkCache,
            libraryService,
            databaseService,
            config);
    }

    private static PlaylistTrack CreateTrack(string globalId, Guid playlistId)
    {
        return new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            PlaylistId = playlistId,
            TrackUniqueHash = globalId,
            Artist = "Test Artist",
            Title = "Test Title",
            Status = TrackStatus.Pending
        };
    }
}
