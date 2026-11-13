using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Library.Maintenance;
using Xunit;

namespace SLSKDONET.Tests.Maintenance;

public class LibraryContentHashTests
{
    [Fact]
    public async Task DuplicateGroups_GroupOnlyRepeatedHashes()
    {
        var a = "dup-a-" + Guid.NewGuid().ToString("N");
        var b = "dup-b-" + Guid.NewGuid().ToString("N");
        var c = "dup-c-" + Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext())
        {
            db.LibraryEntries.AddRange(
                new LibraryEntryEntity { UniqueHash = a, Artist = "A", Title = "One", FilePath = "a.flac", AudioContentHash = "pcm-md5:abc" },
                new LibraryEntryEntity { UniqueHash = b, Artist = "B", Title = "One", FilePath = "b.flac", AudioContentHash = "pcm-md5:abc" },
                new LibraryEntryEntity { UniqueHash = c, Artist = "C", Title = "Two", FilePath = "c.flac", AudioContentHash = "pcm-md5:def" });
            await db.SaveChangesAsync();
        }

        var factory = new TestDbFactory();
        var hasher = new AudioContentHasher(NullLogger<AudioContentHasher>.Instance);
        var service = new LibraryContentHashBackfillService(factory, hasher, NullLogger<LibraryContentHashBackfillService>.Instance);

        var groups = await service.FindDuplicateGroupsAsync();
        var group = Assert.Single(groups, g => g.AudioContentHash == "pcm-md5:abc");
        Assert.Equal(2, group.TrackHashes.Count);
        Assert.Contains(a, group.TrackHashes);
        Assert.Contains(b, group.TrackHashes);
    }

    private sealed class TestDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AppDbContext());
    }
}