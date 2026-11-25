using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Library.Files;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Files;

/// <summary>LF6 (#348): the quality upgrade swaps files and repoints all three path columns.</summary>
[Collection(NonParallelCollection.Name)] // FreshDatabaseScope swaps the process-wide ORBIT_DB_PATH
public class QualityUpgradeServiceTests : IDisposable
{
    private readonly FreshDatabaseScope _scope = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-upgrade-{Guid.NewGuid():N}");

    public QualityUpgradeServiceTests()
    {
        Directory.CreateDirectory(_dir);
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private sealed class TestDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppDbContext());
    }

    [Fact]
    public async Task ReplaceAsync_MovesNewTrashesOldAndRepointsPaths()
    {
        const string hash = "h-upgrade";
        var oldFile = Path.Combine(_dir, "Artist - Title.mp3");
        var newFile = Path.Combine(_dir, "staged", "Artist - Title.flac");
        File.WriteAllText(oldFile, "mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.WriteAllText(newFile, "flac");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = oldFile });
            db.PlaylistTracks.Add(new PlaylistTrackEntity { TrackUniqueHash = hash, ResolvedFilePath = oldFile });
            db.Tracks.Add(new TrackEntity { GlobalId = hash, LocalFilePath = oldFile });
            db.SaveChanges();
        }

        var svc = new QualityUpgradeService(new TestDbFactory(), NullLogger<QualityUpgradeService>.Instance);
        var target = await svc.ReplaceAsync(hash, newFile, oldFile, _dir);

        var expected = Path.Combine(_dir, "Artist - Title.flac");
        Assert.Equal(expected, target);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(newFile)); // moved into place
        Assert.False(File.Exists(oldFile)); // trashed

        using var verify = new AppDbContext();
        Assert.Equal(target, verify.LibraryEntries.Single(e => e.UniqueHash == hash).FilePath);
        Assert.Equal(target, verify.PlaylistTracks.Single(t => t.TrackUniqueHash == hash).ResolvedFilePath);
        Assert.Equal(target, verify.Tracks.Single(t => t.GlobalId == hash).LocalFilePath);
    }

    [Fact]
    public async Task ReplaceAsync_SameExtensionUpgrade_SucceedsAndTrashesOld()
    {
        const string hash = "h-same-ext";
        var oldFile = Path.Combine(_dir, "Artist - Title.mp3");
        var newFile = Path.Combine(_dir, "staged", "Artist - Title.mp3");
        File.WriteAllText(oldFile, "128kbps");
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.WriteAllText(newFile, "320kbps");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = oldFile });
            db.SaveChanges();
        }

        var svc = new QualityUpgradeService(new TestDbFactory(), NullLogger<QualityUpgradeService>.Instance);
        var target = await svc.ReplaceAsync(hash, newFile, oldFile, _dir);

        Assert.Equal(oldFile, target);
        Assert.Equal("320kbps", File.ReadAllText(target));
        Assert.False(File.Exists(newFile));
        Assert.Single(Directory.GetFiles(OrbitTrash.GetTrashDirectory(_dir), "*.mp3"));
    }

    [Fact]
    public async Task ReplaceAsync_LockedTrack_ThrowsWithoutTouchingDisk()
    {
        const string hash = "h-locked";
        var oldFile = Path.Combine(_dir, "Locked.mp3");
        var newFile = Path.Combine(_dir, "staged", "Locked.flac");
        File.WriteAllText(oldFile, "old");
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.WriteAllText(newFile, "new");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = oldFile, PathLockedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        var svc = new QualityUpgradeService(new TestDbFactory(), NullLogger<QualityUpgradeService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReplaceAsync(hash, newFile, oldFile, _dir));

        Assert.True(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public async Task ReplaceAsync_ShiftsCueTimestampsByTheOffset()
    {
        const string hash = "h-offset";
        var oldFile = Path.Combine(_dir, "Artist - Title.mp3");
        var newFile = Path.Combine(_dir, "staged", "Artist - Title.flac");
        File.WriteAllText(oldFile, "mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.WriteAllText(newFile, "flac");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = oldFile });
            db.CuePoints.Add(new CuePointEntity { TrackUniqueHash = hash, TimestampInSeconds = 10.0 });
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = hash, BeatGridJson = "[10.0, 11.0]" });
            db.SaveChanges();
        }

        var svc = new QualityUpgradeService(new TestDbFactory(), NullLogger<QualityUpgradeService>.Instance);
        await svc.ReplaceAsync(hash, newFile, oldFile, _dir, offsetSeconds: 0.026);

        using var verify = new AppDbContext();
        Assert.Equal(10.026, verify.CuePoints.Single(c => c.TrackUniqueHash == hash).TimestampInSeconds, 6);

        var beats = System.Text.Json.JsonSerializer.Deserialize<List<double>>(
            verify.AudioFeatures.Single(f => f.TrackUniqueHash == hash).BeatGridJson);
        Assert.Equal(10.026, beats![0], 6);
        Assert.Equal(11.026, beats[1], 6);
    }

    [Fact]
    public async Task ReplaceAsync_NegativeOffset_ClampsEarlyCuesAndBeatsAtZero()
    {
        const string hash = "h-offset-clamp";
        var oldFile = Path.Combine(_dir, "Artist - Early.mp3");
        var newFile = Path.Combine(_dir, "staged", "Artist - Early.flac");
        File.WriteAllText(oldFile, "mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.WriteAllText(newFile, "flac");

        using (var db = new AppDbContext())
        {
            db.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = hash, FilePath = oldFile });
            db.CuePoints.Add(new CuePointEntity { TrackUniqueHash = hash, TimestampInSeconds = 0.010 });
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = hash, BeatGridJson = "[0.005, 1.0]" });
            db.SaveChanges();
        }

        var svc = new QualityUpgradeService(new TestDbFactory(), NullLogger<QualityUpgradeService>.Instance);
        await svc.ReplaceAsync(hash, newFile, oldFile, _dir, offsetSeconds: -0.026);

        using var verify = new AppDbContext();
        Assert.Equal(0.0, verify.CuePoints.Single(c => c.TrackUniqueHash == hash).TimestampInSeconds, 6);
        var beats = System.Text.Json.JsonSerializer.Deserialize<List<double>>(
            verify.AudioFeatures.Single(f => f.TrackUniqueHash == hash).BeatGridJson);
        Assert.Equal(0.0, beats![0], 6);
        Assert.Equal(0.974, beats[1], 6);
    }
}
