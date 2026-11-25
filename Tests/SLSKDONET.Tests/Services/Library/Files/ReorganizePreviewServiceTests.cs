using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Files;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Files;

/// <summary>LF5 (#347): the reorganise preview classifies move / unchanged / blocked.</summary>
[Collection(NonParallelCollection.Name)] // FreshDatabaseScope swaps the process-wide ORBIT_DB_PATH
public class ReorganizePreviewServiceTests : IDisposable
{
    private readonly FreshDatabaseScope _scope = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-reorg-{Guid.NewGuid():N}");

    public ReorganizePreviewServiceTests()
    {
        Directory.CreateDirectory(_dir);
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private sealed class TestDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppDbContext());
    }

    private sealed class FakeEventBus : IEventBus
    {
        public IObservable<T> GetEvent<T>() => throw new NotImplementedException();
        public void Publish<T>(T eventData) { }
    }

    private ReorganizePreviewService MakeService() =>
        new(
            new PathProviderService(
                new AppConfig { NameFormat = "{artist} - {title}", DownloadDirectory = _dir },
                new FileNameFormatter(),
                NullLogger<PathProviderService>.Instance),
            new FileRelocationService(new TestDbFactory(), new FakeEventBus(), NullLogger<FileRelocationService>.Instance));

    private static Track MakeTrack(string artist, string title, string filename) =>
        new() { Artist = artist, Title = title, Filename = filename };

    [Fact]
    public async Task Preview_ClassifiesMoveUnchangedAndLocked()
    {
        const string moveHash = "h-move";
        const string unchangedHash = "h-same";
        const string lockedHash = "h-locked";

        var moveSource = Path.Combine(_dir, "old - name.flac");
        var sameSource = Path.Combine(_dir, "Artist - Title.flac");
        var lockedSource = Path.Combine(_dir, "Locked - Wrong.flac");
        File.WriteAllText(moveSource, "a");
        File.WriteAllText(sameSource, "b");
        File.WriteAllText(lockedSource, "c");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = moveHash, FilePath = moveSource });
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = unchangedHash, FilePath = sameSource });
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = lockedHash, FilePath = lockedSource, PathLockedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        var preview = await MakeService().PreviewAsync(new[]
        {
            new ReorganizeTrack(moveHash, moveSource, MakeTrack("Artist", "Title", "old - name.flac")),
            new ReorganizeTrack(unchangedHash, sameSource, MakeTrack("Artist", "Title", "Artist - Title.flac")),
            new ReorganizeTrack(lockedHash, lockedSource, MakeTrack("Locked", "Name", "Locked - Wrong.flac")),
        });

        Assert.Equal(1, preview.Movable);
        Assert.Equal(1, preview.Unchanged);
        Assert.Equal(1, preview.Blocked);
        Assert.Contains(preview.Items, i => i.Hash == moveHash && i.Status == "Move");
        Assert.Contains(preview.Items, i => i.Hash == unchangedHash && i.Status == "Unchanged");
        Assert.Contains(preview.Items, i => i.Hash == lockedHash && i.Status.Contains("locked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAndUndo_RestoresOriginalLayoutAndCleansEmptyFolders()
    {
        const string hash = "h-undo";
        var subDir = Path.Combine(_dir, "OldSub");
        Directory.CreateDirectory(subDir);
        var source = Path.Combine(subDir, "messy.flac");
        var target = Path.Combine(_dir, "Artist - Title.flac");
        File.WriteAllText(source, "audio");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = source, Artist = "Artist", Title = "Title" });
            db.SaveChanges();
        }

        var relocation = new FileRelocationService(new TestDbFactory(), new FakeEventBus(), NullLogger<FileRelocationService>.Instance);
        var plan = await relocation.PlanAsync(new[] { new RelocationRequest(hash, source, target) });
        var outcome = await relocation.ExecuteAsync(plan);

        Assert.Equal(1, outcome.Moved);
        Assert.NotEqual(Guid.Empty, outcome.BatchId);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(source));

        var cleaned = EmptyFolderCleanup.RemoveEmptyParents(source, _dir);
        Assert.Single(cleaned);
        Assert.False(Directory.Exists(subDir));

        var reverted = await relocation.RevertBatchAsync(outcome.BatchId);
        Assert.Equal(1, reverted);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }
}
