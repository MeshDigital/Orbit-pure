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
    public async Task Preview_DoesNotCreateTargetDirectoriesOnDisk()
    {
        const string hash = "h-nested";
        var source = Path.Combine(_dir, "loose.flac");
        File.WriteAllText(source, "a");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = source });
            db.SaveChanges();
        }

        var nestedSvc = new ReorganizePreviewService(
            new PathProviderService(
                new AppConfig { NameFormat = "{artist}/{album}/{title}", DownloadDirectory = _dir },
                new FileNameFormatter(),
                NullLogger<PathProviderService>.Instance),
            new FileRelocationService(new TestDbFactory(), new FakeEventBus(), NullLogger<FileRelocationService>.Instance));

        var preview = await nestedSvc.PreviewAsync(new[]
        {
            new ReorganizeTrack(hash, source, new Track { Artist = "NewArtist", Album = "NewAlbum", Title = "Track", Filename = "loose.flac" }),
        });

        Assert.Equal(1, preview.Movable);
        Assert.False(Directory.Exists(Path.Combine(_dir, "NewArtist")), "Preview must not create target folders on disk.");
    }
}
