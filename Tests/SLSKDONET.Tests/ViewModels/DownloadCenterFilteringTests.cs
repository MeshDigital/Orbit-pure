using System;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.Downloads;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Regression coverage for two Download Center filtering gaps found during a log/UX deep-dive:
/// (1) the Hub's status chips only matched a track's exact PlaylistTrackState, so a track
/// waiting for a download slot or a peer connection vanished from every chip except "All";
/// (2) the free-text search compared strings verbatim, so typing "beyonce" wouldn't find a
/// track titled "Beyoncé" — a routine case given how often track/artist names carry diacritics.
/// </summary>
public class DownloadCenterFilteringTests
{
    private static UnifiedTrackViewModel CreateTrackViewModel(PlaylistTrackState state)
    {
        var downloadManager = (DownloadManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadManager));
        var eventBus = new EventBusService();
        var artworkCache = (ArtworkCacheService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ArtworkCacheService));
        var libraryService = Mock.Of<ILibraryService>();
        var databaseService = (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService));
        var config = new AppConfig();

        var model = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            PlaylistId = Guid.NewGuid(),
            TrackUniqueHash = "hash-" + Guid.NewGuid(),
            Artist = "Test Artist",
            Title = "Test Title",
            Status = TrackStatus.Pending,
        };

        var vm = new UnifiedTrackViewModel(model, downloadManager, eventBus, artworkCache, libraryService, databaseService, config);
        vm.State = state;
        return vm;
    }

    [Theory]
    [InlineData(PlaylistTrackState.Downloading)]
    [InlineData(PlaylistTrackState.Queued)]
    [InlineData(PlaylistTrackState.WaitingForConnection)]
    public void MatchesRowStatusFilter_Downloading_IncludesQueuedAndWaitingForConnection(PlaylistTrackState state)
    {
        var track = CreateTrackViewModel(state);

        Assert.True(DownloadCenterViewModel.MatchesRowStatusFilter(track, "Downloading"));
    }

    [Fact]
    public void MatchesRowStatusFilter_Downloading_ExcludesSearching()
    {
        var track = CreateTrackViewModel(PlaylistTrackState.Searching);

        Assert.False(DownloadCenterViewModel.MatchesRowStatusFilter(track, "Downloading"));
    }

    [Theory]
    [InlineData("Beyoncé", "beyonce", true)]
    [InlineData("Beyoncé", "Beyoncé", true)]
    [InlineData("Café del Mar", "cafe del mar", true)]
    [InlineData("Beyoncé", "rihanna", false)]
    public void StripDiacritics_MakesAccentInsensitiveComparisonPossible(string original, string typedSearch, bool shouldMatch)
    {
        var normalizedOriginal = DownloadCenterViewModel.StripDiacritics(original);
        var normalizedSearch = DownloadCenterViewModel.StripDiacritics(typedSearch);

        var matches = normalizedOriginal.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(shouldMatch, matches);
    }
}
