using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Files;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Files;

/// <summary>LF4 (#346): the path lock is set, read and cleared on a library entry.</summary>
[Collection(NonParallelCollection.Name)] // FreshDatabaseScope swaps the process-wide ORBIT_DB_PATH
public class PathProtectionServiceTests : IDisposable
{
    private readonly FreshDatabaseScope _scope = new();

    public PathProtectionServiceTests()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _scope.Dispose();

    private sealed class TestDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppDbContext());
    }

    private static PathProtectionService MakeService() => new(new TestDbFactory());

    private static void Seed(string hash)
    {
        using var db = new AppDbContext();
        db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = $"/tmp/{hash}.flac" });
        db.SaveChanges();
    }

    [Fact]
    public async Task SetPathLocked_LocksThenUnlocks()
    {
        const string hash = "h-lock";
        Seed(hash);
        var svc = MakeService();

        Assert.False(await svc.IsPathLockedAsync(hash));

        await svc.SetPathLockedAsync(hash, locked: true);
        Assert.True(await svc.IsPathLockedAsync(hash));

        await svc.SetPathLockedAsync(hash, locked: false);
        Assert.False(await svc.IsPathLockedAsync(hash));
    }

    [Fact]
    public async Task SetPathLocked_UnknownHash_IsNoOp()
    {
        var svc = MakeService();
        await svc.SetPathLockedAsync("missing", locked: true); // must not throw
        Assert.False(await svc.IsPathLockedAsync("missing"));
    }

    [Fact]
    public async Task RefreshFileOrigin_DerivesLinkedFolderFromTheRoot()
    {
        const string hash = "h-origin";
        var linkedRoot = Path.Combine(Path.GetTempPath(), "orbit-origin-linked");
        var downloadDir = Path.Combine(Path.GetTempPath(), "orbit-origin-downloads");
        var file = Path.Combine(linkedRoot, "Artist", "track.flac");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = file });
            db.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = linkedRoot, IsEnabled = true });
            db.SaveChanges();
        }

        var origin = await MakeService().RefreshFileOriginAsync(hash, file, downloadDir);

        Assert.Equal(FileOriginDeriver.LinkedFolder, origin);
    }
}
