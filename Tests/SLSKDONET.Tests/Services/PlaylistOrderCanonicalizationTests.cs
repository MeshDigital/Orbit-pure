using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// <see cref="LibraryService.SaveTrackOrderAsync"/> is the one place a playlist's order is
/// persisted, and it must always leave the playlist with ONE total order (SortOrder 1..N, no
/// ties) — otherwise the grid, Cue Forge's browser and the player each read a different sequence
/// from <c>ORDER BY SortOrder</c>, and paged reads can repeat or skip rows. It is also the place
/// that broadcasts the change so those surfaces re-read it.
/// </summary>
public class PlaylistOrderCanonicalizationTests
{
    private static PlaylistTrack Row(string hash, int sortOrder) => new()
    {
        TrackUniqueHash = hash,
        Artist = "Artist",
        Title = hash,
        Status = TrackStatus.Downloaded,
        ResolvedFilePath = $"C:/music/{hash}.flac",
        TrackNumber = sortOrder,
        SortOrder = sortOrder,
        AddedAt = DateTime.UtcNow,
    };

    [Fact]
    public void CompleteList_IsTakenAsTheNewOrder()
    {
        var a = Row("a", 1);
        var b = Row("b", 2);
        var c = Row("c", 3);
        var current = new List<PlaylistTrack> { a, b, c };
        var requested = new List<PlaylistTrack> { c, a, b };

        var result = LibraryService.BuildTotalPlaylistOrder(current, requested);

        Assert.Equal(new[] { "c", "a", "b" }, result.Select(t => t.TrackUniqueHash));
    }

    [Fact]
    public void PartialList_PermutesOnlyItsOwnPositions()
    {
        // Downloaded/arranged rows sit at positions 1, 3 and 5; the two "wanted but missing"
        // rows in between must not move.
        var a = Row("a", 1);
        var missing1 = Row("missing-1", 2);
        var b = Row("b", 3);
        var missing2 = Row("missing-2", 4);
        var c = Row("c", 5);
        var current = new List<PlaylistTrack> { a, missing1, b, missing2, c };

        var result = LibraryService.BuildTotalPlaylistOrder(current, new List<PlaylistTrack> { c, a, b })
            .Select(t => t.TrackUniqueHash)
            .ToList();

        Assert.Equal(new[] { "c", "missing-1", "a", "missing-2", "b" }, result);
    }

    [Fact]
    public void PartialList_IgnoresRowsThatAreNotMembersOfThePlaylist()
    {
        var a = Row("a", 1);
        var b = Row("b", 2);
        var current = new List<PlaylistTrack> { a, b };
        var foreign = Row("foreign", 9);

        var result = LibraryService.BuildTotalPlaylistOrder(current, new List<PlaylistTrack> { foreign, b, a });

        Assert.Equal(new[] { "b", "a" }, result.Select(t => t.TrackUniqueHash));
    }

    [Fact]
    public async Task SavingAPartialOrder_LeavesTheWholePlaylistOnUniqueSequentialPositions_AndBroadcasts()
    {
        var database = new DatabaseService(
            NullLogger<DatabaseService>.Instance,
            new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance),
            new TrackRepository(NullLogger<TrackRepository>.Instance),
            new Mock<IFileWriteService>().Object);
        var eventBus = new EventBusService();
        var library = new LibraryService(
            NullLogger<LibraryService>.Instance, database, new AppConfig(), eventBus, new LibraryCacheService());

        var job = await library.CreateEmptyPlaylistAsync($"Order test {Guid.NewGuid():N}");
        try
        {
            var rows = new List<PlaylistTrack>();
            for (var i = 0; i < 5; i++)
            {
                var row = Row($"order-{job.Id:N}-{i}", i);
                row.PlaylistId = job.Id;
                row.Status = i % 2 == 0 ? TrackStatus.Downloaded : TrackStatus.Failed;
                rows.Add(row);
            }

            await library.AddTracksToProjectAsync(rows, job.Id);
            var persisted = await library.LoadPlaylistTracksAsync(job.Id);
            Assert.Equal(5, persisted.Count);

            PlaylistTrackOrderChangedEvent? published = null;
            using var subscription = eventBus.GetEvent<PlaylistTrackOrderChangedEvent>()
                .Subscribe(evt => published = evt);

            // Only the downloaded rows are "arranged", exactly like Reorder for better flow /
            // Auto-Arrange / Automix: they pass a partial list in their new order.
            var arranged = persisted
                .Where(t => t.Status == TrackStatus.Downloaded)
                .OrderByDescending(t => t.TrackUniqueHash, StringComparer.Ordinal)
                .ToList();
            var arrangedHashes = arranged.Select(t => t.TrackUniqueHash).ToList();

            await library.SaveTrackOrderAsync(job.Id, arranged);

            var reloaded = await library.LoadPlaylistTracksAsync(job.Id);

            // One total order: 1..N, every value used exactly once.
            Assert.Equal(
                Enumerable.Range(1, reloaded.Count).ToArray(),
                reloaded.Select(t => t.SortOrder).OrderBy(v => v).ToArray());

            // The arranged rows keep the requested relative order…
            var reloadedArranged = reloaded
                .Where(t => arrangedHashes.Contains(t.TrackUniqueHash))
                .Select(t => t.TrackUniqueHash)
                .ToList();
            Assert.Equal(arrangedHashes, reloadedArranged);

            // …and the rows that weren't part of the arrange call are still there, in their slots.
            Assert.Equal(5, reloaded.Count);

            Assert.NotNull(published);
            Assert.Equal(job.Id, published!.PlaylistId);
            Assert.Equal(reloaded.Select(t => t.Id).ToList(), published.OrderedPlaylistTrackIds.ToList());
        }
        finally
        {
            await library.DeletePlaylistJobAsync(job.Id);
        }
    }
}
