using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>#344 (LF2): the single move/rename path updates every path column and moves the file.</summary>
[Collection(NonParallelCollection.Name)] // FreshDatabaseScope swaps the process-wide ORBIT_DB_PATH
public class FileRelocationServiceTests : IDisposable
{
    private readonly FreshDatabaseScope _scope = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-reloc-{Guid.NewGuid():N}");

    public FileRelocationServiceTests()
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

    private sealed class ThrowingDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("db down");
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("db down");
    }

    private sealed class FakeEventBus : IEventBus
    {
        public List<object> Published { get; } = new();
        public IObservable<T> GetEvent<T>() => throw new NotImplementedException();
        public void Publish<T>(T eventData) => Published.Add(eventData!);
    }

    private static FileRelocationService MakeService(FakeEventBus bus) =>
        new(new TestDbFactory(), bus, NullLogger<FileRelocationService>.Instance);

    private static void Seed(string hash, string path)
    {
        using var db = new AppDbContext();
        db.Tracks.Add(new TrackEntity { GlobalId = hash, LocalFilePath = path });
        db.PlaylistTracks.Add(new PlaylistTrackEntity { TrackUniqueHash = hash, ResolvedFilePath = path });
        db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = path });
        db.SaveChanges();
    }

    [Fact]
    public async Task Execute_Rename_UpdatesAllThreeTablesAndMovesFile()
    {
        var hash = "h1";
        var source = Path.Combine(_dir, "old", "a.flac");
        var target = Path.Combine(_dir, "new", "b.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "audio");

        Seed(hash, source);
        var bus = new FakeEventBus();
        var svc = MakeService(bus);

        var plan = svc.Plan(new[] { new RelocationRequest(hash, source, target) });
        Assert.Empty(plan.Problems);
        var outcome = await svc.ExecuteAsync(plan);

        Assert.Equal(1, outcome.Moved);
        Assert.Empty(outcome.Failures);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(source));

        using var db = new AppDbContext();
        Assert.Equal(target, db.LibraryEntries.Single(e => e.UniqueHash == hash).FilePath);
        Assert.Equal(target, db.PlaylistTracks.Single(t => t.TrackUniqueHash == hash).ResolvedFilePath);
        Assert.Equal(target, db.Tracks.Single(t => t.GlobalId == hash).LocalFilePath);
        Assert.Single(bus.Published);
    }

    [Fact]
    public void Plan_MissingSource_IsReported()
    {
        var svc = MakeService(new FakeEventBus());
        var plan = svc.Plan(new[] { new RelocationRequest("h", Path.Combine(_dir, "nope.flac"), Path.Combine(_dir, "x.flac")) });

        Assert.Empty(plan.Moves);
        Assert.Single(plan.Problems);
        Assert.Contains("not found", plan.Problems[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_Collision_IsReported()
    {
        var a = Path.Combine(_dir, "a.flac");
        var b = Path.Combine(_dir, "b.flac");
        var dest = Path.Combine(_dir, "dest.flac");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        var svc = MakeService(new FakeEventBus());
        var plan = svc.Plan(new[]
        {
            new RelocationRequest("h1", a, dest),
            new RelocationRequest("h2", b, dest), // same target → collision
        });

        Assert.Single(plan.Moves);
        Assert.Single(plan.Problems);
        Assert.Contains("collision", plan.Problems[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_LockedTrack_IsReportedAsProblem()
    {
        var hash = "h-locked";
        var source = Path.Combine(_dir, "locked.flac");
        var target = Path.Combine(_dir, "moved.flac");
        File.WriteAllText(source, "audio");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = source, PathLockedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        var svc = MakeService(new FakeEventBus());
        var plan = await svc.PlanAsync(new[] { new RelocationRequest(hash, source, target) });

        Assert.Empty(plan.Moves);
        Assert.Single(plan.Problems);
        Assert.Contains("locked", plan.Problems[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_LinkedFolderTrack_IsReportedAsProblem()
    {
        var hash = "h-linked";
        var source = Path.Combine(_dir, "linked.flac");
        var target = Path.Combine(_dir, "moved2.flac");
        File.WriteAllText(source, "audio");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = source, FileOrigin = FileOriginDeriver.LinkedFolder });
            db.SaveChanges();
        }

        var svc = MakeService(new FakeEventBus());
        var plan = await svc.PlanAsync(new[] { new RelocationRequest(hash, source, target) });

        Assert.Empty(plan.Moves);
        Assert.Single(plan.Problems);
        Assert.Contains("linked folder", plan.Problems[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_UnlockedDownloadedTrack_IsMoved()
    {
        var hash = "h-downloaded";
        var source = Path.Combine(_dir, "downloaded.flac");
        var target = Path.Combine(_dir, "moved3.flac");
        File.WriteAllText(source, "audio");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = source, FileOrigin = FileOriginDeriver.Downloaded });
            db.SaveChanges();
        }

        var svc = MakeService(new FakeEventBus());
        var plan = await svc.PlanAsync(new[] { new RelocationRequest(hash, source, target) });

        Assert.Single(plan.Moves);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public async Task Execute_DbWriteFailure_RollsBackTheFileMove()
    {
        var source = Path.Combine(_dir, "src.flac");
        var target = Path.Combine(_dir, "tgt.flac");
        File.WriteAllText(source, "audio");

        var svc = new FileRelocationService(new ThrowingDbFactory(), new FakeEventBus(), NullLogger<FileRelocationService>.Instance);
        var plan = svc.Plan(new[] { new RelocationRequest("h-rollback", source, target) });

        var outcome = await svc.ExecuteAsync(plan);

        Assert.Equal(0, outcome.Moved);
        Assert.Single(outcome.Failures);
        Assert.True(File.Exists(source), "the move must be rolled back to the source");
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Execute_JournalsTheMove()
    {
        var hash = "h-journal";
        var source = Path.Combine(_dir, "journal-src.flac");
        var target = Path.Combine(_dir, "journal-tgt.flac");
        File.WriteAllText(source, "audio");
        Seed(hash, source);

        var svc = MakeService(new FakeEventBus());
        var plan = svc.Plan(new[] { new RelocationRequest(hash, source, target) });
        await svc.ExecuteAsync(plan);

        using var db = new AppDbContext();
        var log = Assert.Single(db.LibraryActionLogs.ToList());
        Assert.Equal(source, log.SourcePath);
        Assert.Equal(target, log.DestinationPath);
        Assert.Equal(LibraryActionType.Rename, log.ActionType); // same directory
    }

    [Fact]
    public async Task RevertBatch_UndoesTheMove()
    {
        var hash = "h-undo";
        var source = Path.Combine(_dir, "undo-src.flac");
        var target = Path.Combine(_dir, "undo-tgt.flac");
        File.WriteAllText(source, "audio");
        Seed(hash, source);

        var svc = MakeService(new FakeEventBus());
        var plan = svc.Plan(new[] { new RelocationRequest(hash, source, target) });
        await svc.ExecuteAsync(plan);
        Assert.True(File.Exists(target));

        Guid batchId;
        using (var db = new AppDbContext())
            batchId = db.LibraryActionLogs.Single().BatchId;

        var reverted = await svc.RevertBatchAsync(batchId);
        Assert.Equal(1, reverted);

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));

        using var verify = new AppDbContext();
        Assert.Equal(source, verify.LibraryEntries.Single(e => e.UniqueHash == hash).FilePath);
        Assert.Equal(source, verify.PlaylistTracks.Single(t => t.TrackUniqueHash == hash).ResolvedFilePath);
        Assert.Equal(source, verify.Tracks.Single(t => t.GlobalId == hash).LocalFilePath);
    }

    [Fact]
    public async Task Execute_MovesTheStemFolderAlongside()
    {
        var hash = "h-stems";
        var source = Path.Combine(_dir, "Artist", "track.flac");
        var target = Path.Combine(_dir, "Artist2", "track2.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "audio");

        var stemFolder = Path.Combine(Path.GetDirectoryName(source)!, "Stems", "track");
        Directory.CreateDirectory(stemFolder);
        File.WriteAllText(Path.Combine(stemFolder, "vocals.wav"), "stem");
        Seed(hash, source);

        var svc = MakeService(new FakeEventBus());
        var plan = svc.Plan(new[] { new RelocationRequest(hash, source, target) });
        await svc.ExecuteAsync(plan);

        var targetStem = Path.Combine(Path.GetDirectoryName(target)!, "Stems", "track2");
        Assert.True(Directory.Exists(targetStem), "the stem folder must move alongside the track");
        Assert.True(File.Exists(Path.Combine(targetStem, "vocals.wav")));
    }
}
