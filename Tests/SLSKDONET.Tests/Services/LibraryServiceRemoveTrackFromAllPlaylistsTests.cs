using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression coverage for a real, live-reported bug: after the file-deletion fix, "Delete
/// Permanently" stopped no-op'ing but still left the track visibly sitting in the playlist, and
/// the app immediately started lagging/hanging as it launched Soulseek search cascades trying to
/// re-download the file it had just deleted. Root cause was DeleteTrackFromDiskAndHistoryAsync
/// marking the playlist row TrackStatus.Missing instead of removing it — Missing literally means
/// "not yet downloaded, queue for search", which both keeps the row alive in the playlist list AND
/// feeds GhostAcquisitionOrchestrator's Missing/Failed/OnHold sweep. RemoveTrackFromAllPlaylistsAsync
/// replaces that with an actual delete of the PlaylistTrack row(s).
/// </summary>
public class LibraryServiceRemoveTrackFromAllPlaylistsTests
{
    [Fact]
    public async Task RemoveTrackFromAllPlaylistsAsync_DeletesRowsAcrossEveryPlaylist_NotJustMarksMissing()
    {
        var databaseService = CreateDatabaseService();
        var library = CreateLibraryService(databaseService);
        var trackHash = Guid.NewGuid().ToString("N");

        var playlistA = await library.CreateEmptyPlaylistAsync($"Test Delete A {Guid.NewGuid():N}");
        var playlistB = await library.CreateEmptyPlaylistAsync($"Test Delete B {Guid.NewGuid():N}");

        try
        {
            var trackIdA = Guid.NewGuid();
            var trackIdB = Guid.NewGuid();

            await using (var db = new AppDbContext())
            {
                db.PlaylistTracks.Add(new PlaylistTrackEntity
                {
                    Id = trackIdA,
                    PlaylistId = playlistA.Id,
                    TrackUniqueHash = trackHash,
                    Artist = "Test Artist",
                    Title = "Test Title",
                    Status = TrackStatus.Downloaded,
                    ResolvedFilePath = string.Empty,
                });
                db.PlaylistTracks.Add(new PlaylistTrackEntity
                {
                    Id = trackIdB,
                    PlaylistId = playlistB.Id,
                    TrackUniqueHash = trackHash,
                    Artist = "Test Artist",
                    Title = "Test Title",
                    Status = TrackStatus.Downloaded,
                    ResolvedFilePath = string.Empty,
                });
                await db.SaveChangesAsync();
            }

            await library.RemoveTrackFromAllPlaylistsAsync(trackHash);

            await using (var db = new AppDbContext())
            {
                var remaining = await db.PlaylistTracks
                    .Where(t => t.TrackUniqueHash == trackHash)
                    .ToListAsync();

                Assert.Empty(remaining); // rows deleted outright — none left over "marked Missing"
            }
        }
        finally
        {
            await library.DeletePlaylistJobAsync(playlistA.Id);
            await library.DeletePlaylistJobAsync(playlistB.Id);
        }
    }

    private static DatabaseService CreateDatabaseService()
    {
        var schemaMigrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        var trackRepository = new TrackRepository(NullLogger<TrackRepository>.Instance);
        var fileWrite = new Mock<IFileWriteService>();

        return new DatabaseService(
            NullLogger<DatabaseService>.Instance,
            schemaMigrator,
            trackRepository,
            fileWrite.Object);
    }

    private static LibraryService CreateLibraryService(DatabaseService databaseService)
    {
        var cache = new LibraryCacheService();
        var config = new AppConfig();
        var eventBus = new EventBusService();

        return new LibraryService(
            NullLogger<LibraryService>.Instance,
            databaseService,
            config,
            eventBus,
            cache);
    }
}
