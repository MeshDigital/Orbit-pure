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
        Assert.Equal("Searching", row.StatusLabel);
        Assert.True(row.IsToneInfo);
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
        Assert.Equal("In peer queue", row.StatusLabel);
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
        Assert.Equal("Downloading", row.StatusLabel);
        Assert.True(row.IsToneAccent);
    }

    [Theory]
    [InlineData(PlaylistTrackState.Searching, DownloadStatusFilter.Active, true)]
    [InlineData(PlaylistTrackState.Downloading, DownloadStatusFilter.Queued, false)]
    [InlineData(PlaylistTrackState.Downloading, DownloadStatusFilter.Active, true)]
    [InlineData(PlaylistTrackState.Failed, DownloadStatusFilter.Failed, true)]
    [InlineData(PlaylistTrackState.Stalled, DownloadStatusFilter.Failed, true)]
    [InlineData(PlaylistTrackState.Cancelled, DownloadStatusFilter.Failed, true)]
    [InlineData(PlaylistTrackState.Completed, DownloadStatusFilter.Completed, true)]
    [InlineData(PlaylistTrackState.Completed, DownloadStatusFilter.Active, false)]
    [InlineData(PlaylistTrackState.Searching, DownloadStatusFilter.All, true)]
    [InlineData(PlaylistTrackState.Completed, DownloadStatusFilter.All, true)]
    public void MatchesStatusFilter_MatchesExpectedStates(PlaylistTrackState state, DownloadStatusFilter filter, bool expected)
    {
        var track = CreateTrackViewModel(CreateTrack("hash-filter", Guid.NewGuid()));
        track.State = state;

        Assert.Equal(expected, DownloadCenterViewModel.MatchesStatusFilter(track, filter));
    }

    [Fact]
    public void FailedRow_ShowsShortLabel_WithFullReasonInTooltip()
    {
        var track = CreateTrackViewModel(CreateTrack("hash-fail", Guid.NewGuid()));
        track.FailureEnum = DownloadFailureReason.AllResultsRejectedQuality;
        track.State = PlaylistTrackState.Failed;

        using var row = new DownloadRowViewModel(track);

        Assert.Equal("Quality too low", row.StatusLabel);
        Assert.True(row.IsToneError);
        Assert.Contains("Quality too low", row.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bitrate threshold", row.StatusDetail);
        Assert.True(row.CanRetry);
        Assert.False(row.CanCancel);
    }

    [Fact]
    public void FailureReasonArrivingAfterStateFlip_UpdatesLabelLive()
    {
        var track = CreateTrackViewModel(CreateTrack("hash-fail-late", Guid.NewGuid()));
        track.State = PlaylistTrackState.Failed;
        using var row = new DownloadRowViewModel(track);
        Assert.Equal("Failed", row.StatusLabel);

        track.FailureEnum = DownloadFailureReason.DiskFull;

        Assert.Equal("Disk full", row.StatusLabel);
    }

    [Fact]
    public void StalledRow_IsWarningNotError()
    {
        var track = CreateTrackViewModel(CreateTrack("hash-stall", Guid.NewGuid()));
        track.State = PlaylistTrackState.Stalled;

        using var row = new DownloadRowViewModel(track);

        Assert.Equal("Stalled", row.StatusLabel);
        Assert.True(row.IsToneWarning);
        Assert.Equal(DownloadRowPriority.Attention, row.Priority);
    }

    [Fact]
    public void PausedRow_OffersResumeAndStartNow_NotPause()
    {
        var track = CreateTrackViewModel(CreateTrack("hash-paused", Guid.NewGuid()));
        track.State = PlaylistTrackState.Paused;

        using var row = new DownloadRowViewModel(track);

        Assert.Equal("Paused", row.StatusLabel);
        Assert.True(row.CanResume);
        Assert.True(row.CanStartNow);
        Assert.False(row.CanPause);
        Assert.False(row.CanRemove); // no remove callback supplied
    }

    [Fact]
    public void ShortLabels_AreCellSized_ForEveryFailureReason()
    {
        foreach (DownloadFailureReason reason in Enum.GetValues(typeof(DownloadFailureReason)))
        {
            var label = reason.ToShortLabel();
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.True(label.Length <= 22, $"{reason} label '{label}' is too long for the status cell");
        }
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
