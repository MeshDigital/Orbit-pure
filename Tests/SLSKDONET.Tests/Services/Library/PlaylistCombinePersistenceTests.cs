using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Library;

public sealed class PlaylistCombinePersistenceTests
{
    [Fact]
    public void CollectTracks_IncludesTracksNotReadyForWorkstation()
    {
        var unpreparedTrack = new PlaylistTrack
        {
            TrackUniqueHash = "unprepared",
            Status = TrackStatus.Missing
        };

        var result = PlaylistCombinePersistence.CollectTracks(
            new[]
            {
                new[] { unpreparedTrack },
                new[] { new PlaylistTrack { TrackUniqueHash = "other" } }
            },
            skipDuplicateTracks: true);

        Assert.Equal(2, result.Count);
        Assert.Same(unpreparedTrack, result[0]);
    }

    [Fact]
    public async Task SaveAsync_PersistsTheArrangedTracksAndPreservesRequestedDuplicates()
    {
        var playlistId = Guid.NewGuid();
        var playlist = new PlaylistJob { Id = playlistId, SourceTitle = "Combined" };
        var tracksToSave = new[]
        {
            new PlaylistTrack { TrackUniqueHash = "b", Title = "Second" },
            new PlaylistTrack { TrackUniqueHash = "a", Title = "First" },
            new PlaylistTrack { TrackUniqueHash = "b", Title = "Second (duplicate)" }
        };
        var rowsFromStorage = new[]
        {
            new PlaylistTrack { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "a" },
            new PlaylistTrack { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "b" },
            new PlaylistTrack { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "b" }
        };
        var persistedOrder = new List<PlaylistTrack>();
        var library = new Mock<ILibraryService>();
        library.Setup(service => service.CreateEmptyPlaylistAsync("Combined"))
            .ReturnsAsync(playlist);
        library.Setup(service => service.AddTracksToProjectAsync(
                It.IsAny<IEnumerable<PlaylistTrack>>(), playlistId))
            .Returns(Task.CompletedTask);
        library.Setup(service => service.LoadPlaylistTracksAsync(playlistId))
            .ReturnsAsync(rowsFromStorage.ToList());
        library.Setup(service => service.SaveTrackOrderAsync(
                playlistId, It.IsAny<IEnumerable<PlaylistTrack>>()))
            .Callback<Guid, IEnumerable<PlaylistTrack>>((_, tracks) => persistedOrder = tracks.ToList())
            .Returns(Task.CompletedTask);

        var result = await PlaylistCombinePersistence.SaveAsync(library.Object, " Combined ", tracksToSave);

        Assert.Same(playlist, result);
        library.Verify(service => service.AddTracksToProjectAsync(
            It.Is<IEnumerable<PlaylistTrack>>(tracks =>
                tracks.Count() == 3 && tracks.Count(track => track.TrackUniqueHash == "b") == 2),
            playlistId), Times.Once);
        Assert.Equal(new[] { "b", "b", "a" }, persistedOrder.Select(track => track.TrackUniqueHash));
        Assert.Equal(new[] { 1, 2, 3 }, persistedOrder.Select(track => track.SortOrder));
        Assert.Equal(new[] { 1, 2, 3 }, persistedOrder.Select(track => track.TrackNumber));
        Assert.Equal(3, result.TotalTracks);
        Assert.Equal(persistedOrder.Select(track => track.Id), result.PlaylistTracks.Select(track => track.Id));
        library.Verify(service => service.DeletePlaylistJobAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task SaveAsync_DeletesIncompletePlaylistAndRethrowsPersistenceFailure()
    {
        var playlistId = Guid.NewGuid();
        var playlist = new PlaylistJob { Id = playlistId, SourceTitle = "Combined" };
        var library = new Mock<ILibraryService>();
        library.Setup(service => service.CreateEmptyPlaylistAsync("Combined"))
            .ReturnsAsync(playlist);
        library.Setup(service => service.AddTracksToProjectAsync(
                It.IsAny<IEnumerable<PlaylistTrack>>(), playlistId))
            .Returns(Task.CompletedTask);
        library.Setup(service => service.LoadPlaylistTracksAsync(playlistId))
            .ReturnsAsync(new List<PlaylistTrack>
            {
                new() { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "a" }
            });
        library.Setup(service => service.SaveTrackOrderAsync(
                playlistId, It.IsAny<IEnumerable<PlaylistTrack>>()))
            .ThrowsAsync(new IOException("disk full"));
        library.Setup(service => service.DeletePlaylistJobAsync(playlistId))
            .Returns(Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            PlaylistCombinePersistence.SaveAsync(
                library.Object,
                "Combined",
                new[] { new PlaylistTrack { TrackUniqueHash = "a" } }));

        Assert.Equal("disk full", exception.Message);
        library.Verify(service => service.DeletePlaylistJobAsync(playlistId), Times.Once);
    }

    [Fact]
    public async Task SaveAsync_ReportsWhenTheLibraryRejectsSomeTracks()
    {
        var playlistId = Guid.NewGuid();
        var playlist = new PlaylistJob { Id = playlistId, SourceTitle = "Combined" };
        var library = new Mock<ILibraryService>();
        library.Setup(service => service.CreateEmptyPlaylistAsync("Combined"))
            .ReturnsAsync(playlist);
        library.Setup(service => service.AddTracksToProjectAsync(
                It.IsAny<IEnumerable<PlaylistTrack>>(), playlistId))
            .Returns(Task.CompletedTask);
        library.Setup(service => service.LoadPlaylistTracksAsync(playlistId))
            .ReturnsAsync(new List<PlaylistTrack>
            {
                new() { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = "a" }
            });
        library.Setup(service => service.DeletePlaylistJobAsync(playlistId))
            .Returns(Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PlaylistCombinePersistence.SaveAsync(
                library.Object,
                "Combined",
                new[]
                {
                    new PlaylistTrack { TrackUniqueHash = "a" },
                    new PlaylistTrack { TrackUniqueHash = "b" }
                }));

        Assert.Contains("Only 1 of 2 combined tracks were saved", exception.Message);
        library.Verify(service => service.DeletePlaylistJobAsync(playlistId), Times.Once);
    }
}
