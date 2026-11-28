using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Vault;
using Xunit;

namespace SLSKDONET.Tests.Vault;

public sealed class VaultServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orbit-vault-{Guid.NewGuid():N}");
    private readonly string _music;
    private readonly string _drive;
    private readonly string _dbPath;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly VaultService _service;

    public VaultServiceTests()
    {
        _music = Path.Combine(_root, "Music");
        _drive = Path.Combine(_root, "BackupDrive");
        Directory.CreateDirectory(_music);
        Directory.CreateDirectory(_drive);
        _dbPath = Path.Combine(_root, "AppData", "library.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        CreateSqliteLibrary(_dbPath, tracks: 3);

        _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"vault-{Guid.NewGuid():N}").Options;
        using (var db = new AppDbContext(_options))
        {
            db.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = _music });
            db.SaveChanges();
        }
        var local = new VaultLocalState(Path.Combine(_root, "AppData", "vault-device.json"), NullLogger<VaultLocalState>.Instance);
        _service = new VaultService(new Factory(_options), local, NullLogger<VaultService>.Instance, () => _dbPath);
    }

    private static void CreateSqliteLibrary(string path, int tracks)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE LibraryEntries (UniqueHash TEXT PRIMARY KEY); CREATE TABLE Projects (Id TEXT); CREATE TABLE CuePoints (Id TEXT);";
        cmd.ExecuteNonQuery();
        for (int i = 0; i < tracks; i++)
        {
            cmd.CommandText = $"INSERT INTO LibraryEntries VALUES ('t{i}');";
            cmd.ExecuteNonQuery();
        }
    }

    private string Track(string relative, int size = 64, string? underRoot = null)
    {
        var path = Path.GetFullPath(Path.Combine(underRoot ?? _music, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)(i * 7 + relative.Length)).ToArray());
        using var db = new AppDbContext(_options);
        db.LibraryEntries.Add(new LibraryEntryEntity
        {
            UniqueHash = "h-" + relative, FilePath = path, AvailabilityState = TrackAvailabilityState.Ready,
            AudioContentHash = "pcm:" + relative + size,
        });
        db.SaveChanges();
        return path;
    }

    private string Vault => VaultService.ResolveVaultRoot(_drive);

    [Fact]
    public async Task Backup_CopiesMusicUnderItsRoot_WithManifestIndexAndCheckedSnapshot()
    {
        Track("Artist/One.flac");
        Track("Artist/Two.mp3");

        var result = await _service.BackupAsync(Vault);

        Assert.True(result.IsHealthy, result.Summary);
        Assert.Equal(2, result.Copied);
        Assert.Equal(3, result.SnapshotTracks);
        Assert.True(File.Exists(Path.Combine(Vault, "Music", "music", "Artist", "One.flac")));
        Assert.True(File.Exists(Path.Combine(Vault, VaultService.ManifestFile)));
        Assert.True(File.Exists(Path.Combine(Vault, VaultService.IndexFile)));
        Assert.Empty(Directory.GetFiles(Vault, "*.orbit-vault-part", SearchOption.AllDirectories));

        var status = await _service.GetStatusAsync(Vault);
        Assert.NotNull(status!.LastBackupUtc);
        Assert.Equal(2, status.FilesInVault);
    }

    [Fact]
    public async Task SecondBackup_CopiesOnlyWhatChanged_AndKeepsThePreviousSnapshot()
    {
        var one = Track("One.flac");
        Track("Two.flac");
        await _service.BackupAsync(Vault);

        var again = await _service.BackupAsync(Vault);
        Assert.Equal((0, 2), (again.Copied, again.Unchanged));

        File.WriteAllBytes(one, new byte[200]); // re-encoded / retagged
        await using (var db = new AppDbContext(_options))
        {
            var e = await db.LibraryEntries.SingleAsync(x => x.FilePath == one);
            e.AudioContentHash = "pcm:new";
            await db.SaveChangesAsync();
        }
        var third = await _service.BackupAsync(Vault);
        Assert.Equal((1, 1), (third.Copied, third.Unchanged));
        Assert.Equal(200, new FileInfo(Path.Combine(Vault, "Music", "music", "One.flac")).Length);
        Assert.True(File.Exists(Path.Combine(Vault, VaultService.PreviousSnapshotFile)));
    }

    [Fact]
    public async Task Backup_CountsTracksWhoseFileIsGone_AndFilesOutsideSourcesGoToAnOtherRoot()
    {
        var gone = Track("Gone.flac");
        File.Delete(gone);
        Track("Loose.flac", underRoot: Path.Combine(_root, "Elsewhere"));

        var result = await _service.BackupAsync(Vault);

        Assert.Equal((1, 1), (result.Copied, result.MissingOnDisk));
        var manifest = await VaultService.LoadManifestAsync(Vault, default);
        var other = Assert.Single(manifest!.Roots);
        Assert.StartsWith("Other", other.Name);
    }

    [Fact]
    public async Task RestoreMissingFiles_PutsBackDeletedFiles_AndNeverOverwrites()
    {
        var lost = Track("Lost.flac");
        var kept = Track("Kept.flac");
        await _service.BackupAsync(Vault);
        var lostBytes = File.ReadAllBytes(lost);
        File.Delete(lost);
        File.WriteAllBytes(kept, new byte[] { 9 }); // changed since the backup: must not be overwritten

        var plan = await _service.PlanRestoreAsync(Vault);
        Assert.Equal(1, plan.FilesMissingLocally);

        var restored = await _service.RestoreMissingFilesAsync(Vault);

        Assert.Equal(1, restored);
        Assert.Equal(lostBytes, File.ReadAllBytes(lost));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(kept));
    }

    [Fact]
    public async Task PlanRestore_ComparesTheSnapshotWithTheCurrentLibrary()
    {
        Track("A.flac");
        await _service.BackupAsync(Vault);
        var plan = await _service.PlanRestoreAsync(Vault);
        Assert.Equal(3, plan.SnapshotTracks);
        Assert.Equal(1, plan.CurrentTracks);
    }

    [Fact]
    public async Task ScheduledRestore_SwapsTheDatabaseAtStartup_AfterSavingTheCurrentOne()
    {
        Track("A.flac");
        await _service.BackupAsync(Vault);

        // The live library changes after the backup.
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO LibraryEntries VALUES ('later');";
            cmd.ExecuteNonQuery();
        }
        File.WriteAllText(_dbPath + "-wal", "stale");

        _service.ScheduleDatabaseRestore(Vault);
        Assert.True(VaultPendingRestore.IsScheduled(_dbPath));

        var outcome = VaultPendingRestore.ApplyIfScheduled(_dbPath);

        Assert.True(outcome!.Applied, outcome.Message);
        Assert.False(VaultPendingRestore.IsScheduled(_dbPath));
        Assert.False(File.Exists(_dbPath + "-wal"));
        Assert.Equal(3, VaultService.CheckSnapshot(_dbPath).Tracks);
        Assert.Equal(4, VaultService.CheckSnapshot(outcome.SafetyBackupPath!).Tracks);
        Assert.True(File.Exists(outcome.SafetyBackupPath + "-wal"));
    }

    [Fact]
    public void ScheduledRestore_SnapshotUnreachable_IsSkippedOnce()
    {
        VaultPendingRestore.Schedule(_dbPath, Path.Combine(_root, "unplugged", "library.db"));
        var outcome = VaultPendingRestore.ApplyIfScheduled(_dbPath);
        Assert.False(outcome!.Applied);
        Assert.Contains("isn't reachable", outcome.Message);
        Assert.Null(VaultPendingRestore.ApplyIfScheduled(_dbPath)); // no loop on the next start
        Assert.Equal(3, VaultService.CheckSnapshot(_dbPath).Tracks);
    }

    [Fact]
    public async Task VaultFromANewerOrbit_IsRefused()
    {
        Directory.CreateDirectory(Vault);
        File.WriteAllText(Path.Combine(Vault, VaultService.ManifestFile), "{\"FormatVersion\": 99}");
        await Assert.ThrowsAsync<NotSupportedException>(() => _service.BackupAsync(Vault));
    }

    [Fact]
    public void ResolveVaultRoot_UsesAnExistingVault_OrMakesOneInside()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(_drive), "ORBIT Vault"), VaultService.ResolveVaultRoot(_drive));
        Directory.CreateDirectory(Path.Combine(_drive, "Mine"));
        File.WriteAllText(Path.Combine(_drive, "Mine", VaultService.ManifestFile), "{}");
        Assert.Equal(Path.GetFullPath(Path.Combine(_drive, "Mine")), VaultService.ResolveVaultRoot(Path.Combine(_drive, "Mine")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public Factory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
