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

public class LibraryServicePlaylistOrderPersistenceTests
{
    [Fact]
    public async Task ManualSortOrder_SurvivesReloadAndLaterMetadataSave()
    {
        var database = CreateDatabaseService();
        var library = CreateLibraryService(database);
        var title = $"Test Playlist Order {Guid.NewGuid():N}";
        var job = await library.CreateEmptyPlaylistAsync(title);

        try
        {
            job.ManualSortOrder = 17;
            await library.SavePlaylistJobAsync(job);

            var reloaded = await library.FindPlaylistJobAsync(job.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(17, reloaded!.ManualSortOrder);

            // Renaming or another header save must not reset the user's arrangement to zero.
            reloaded.SourceTitle = $"{title} Renamed";
            await library.SavePlaylistJobAsync(reloaded);

            var savedAgain = await library.FindPlaylistJobAsync(job.Id);
            Assert.NotNull(savedAgain);
            Assert.Equal(17, savedAgain!.ManualSortOrder);
            Assert.Equal(reloaded.SourceTitle, savedAgain.SourceTitle);
        }
        finally
        {
            await library.DeletePlaylistJobAsync(job.Id);
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

    private static LibraryService CreateLibraryService(DatabaseService databaseService) =>
        new(
            NullLogger<LibraryService>.Instance,
            databaseService,
            new AppConfig(),
            new EventBusService(),
            new LibraryCacheService());
}
