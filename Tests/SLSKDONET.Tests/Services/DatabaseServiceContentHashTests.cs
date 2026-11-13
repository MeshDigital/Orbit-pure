using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class DatabaseServiceContentHashTests
{
    [Fact]
    public async Task FindLibraryEntryByContentHash_ReturnsMatchingEntry()
    {
        var hash = Guid.NewGuid().ToString("N");
        var contentHash = "payload:deadbeef";
        await using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = hash,
                Artist = "A",
                Title = "T",
                FilePath = "a.flac",
                AudioContentHash = contentHash
            });
            await db.SaveChangesAsync();
        }

        var service = CreateDatabaseService();
        var found = await service.FindLibraryEntryByContentHashAsync(contentHash);
        Assert.NotNull(found);
        Assert.Equal(hash, found!.UniqueHash);

        Assert.Null(await service.FindLibraryEntryByContentHashAsync("payload:nope"));
        Assert.Null(await service.FindLibraryEntryByContentHashAsync(""));
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
}