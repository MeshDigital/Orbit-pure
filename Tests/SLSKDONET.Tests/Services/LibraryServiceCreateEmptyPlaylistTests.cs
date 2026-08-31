using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Events;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// CreateEmptyPlaylistAsync is the shared root for "New Playlist" and Combine Playlists — it had
/// no uniqueness check at all, so combining twice with the same auto-suggested name (or naming
/// two playlists identically) silently created two indistinguishable playlists.
/// </summary>
public class LibraryServiceCreateEmptyPlaylistTests
{
    [Fact]
    public async Task CreateEmptyPlaylistAsync_DuplicateTitle_AppendsNumericSuffix()
    {
        var databaseService = CreateDatabaseService();
        var library = CreateLibraryService(databaseService);
        var title = $"Test Combine {Guid.NewGuid():N}";
        Guid firstId = Guid.Empty, secondId = Guid.Empty, thirdId = Guid.Empty;

        try
        {
            var first = await library.CreateEmptyPlaylistAsync(title);
            firstId = first.Id;
            Assert.Equal(title, first.SourceTitle);

            var second = await library.CreateEmptyPlaylistAsync(title);
            secondId = second.Id;
            Assert.Equal($"{title} (2)", second.SourceTitle);

            var third = await library.CreateEmptyPlaylistAsync(title);
            thirdId = third.Id;
            Assert.Equal($"{title} (3)", third.SourceTitle);
        }
        finally
        {
            if (firstId != Guid.Empty) await library.DeletePlaylistJobAsync(firstId);
            if (secondId != Guid.Empty) await library.DeletePlaylistJobAsync(secondId);
            if (thirdId != Guid.Empty) await library.DeletePlaylistJobAsync(thirdId);
        }
    }

    [Fact]
    public async Task CreateEmptyPlaylistAsync_UniqueTitle_UnaffectedByCollisionCheck()
    {
        var databaseService = CreateDatabaseService();
        var library = CreateLibraryService(databaseService);
        var title = $"Test Unique {Guid.NewGuid():N}";
        Guid id = Guid.Empty;

        try
        {
            var job = await library.CreateEmptyPlaylistAsync(title);
            id = job.Id;
            Assert.Equal(title, job.SourceTitle);
        }
        finally
        {
            if (id != Guid.Empty) await library.DeletePlaylistJobAsync(id);
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
