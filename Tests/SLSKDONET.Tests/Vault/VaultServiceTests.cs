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

        Assert.Equal(1, restored.Restored);
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

    // ── checksums, tag edits, verify (feedback 2026-11-28) ─────────────────────

    private async Task<System.Collections.Generic.List<VaultFileEntry>> IndexAsync()
    {
        await using var s = File.OpenRead(Path.Combine(Vault, VaultService.IndexFile));
        return (await System.Text.Json.JsonSerializer.DeserializeAsync<System.Collections.Generic.List<VaultFileEntry>>(s))!;
    }

    private string CopyOf(string relative) => Path.Combine(Vault, "Music", "music", relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Backup_StoresTheChecksumOfEachCopy()
    {
        var one = Track("One.flac");
        await _service.BackupAsync(Vault);

        var entry = Assert.Single(await IndexAsync());
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(one)));
        Assert.Equal(expected, entry.FileSha256);
        Assert.Equal(File.GetLastWriteTimeUtc(one), entry.SourceModifiedUtc);
    }

    [Fact]
    public async Task TagOnlyEdit_SameSizeSameAudio_IsCopiedAgain()
    {
        var one = Track("One.flac");
        await _service.BackupAsync(Vault);

        // A tag edit: same length, same audio hash in the library, different bytes and a newer time.
        var bytes = File.ReadAllBytes(one);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(one, bytes);
        File.SetLastWriteTimeUtc(one, DateTime.UtcNow.AddMinutes(5));

        var again = await _service.BackupAsync(Vault);

        Assert.Equal(1, again.Copied);
        Assert.Equal(bytes, File.ReadAllBytes(CopyOf("One.flac")));
    }

    [Fact]
    public async Task OlderIndexWithoutTimes_IsNotRecopied_AndGetsTheTimeFilledIn()
    {
        Track("One.flac");
        await _service.BackupAsync(Vault);
        var index = await IndexAsync();
        index[0].SourceModifiedUtc = null; // as written by 0.5.2
        await File.WriteAllTextAsync(Path.Combine(Vault, VaultService.IndexFile), System.Text.Json.JsonSerializer.Serialize(index));

        var again = await _service.BackupAsync(Vault);

        Assert.Equal((0, 1), (again.Copied, again.Unchanged));
        Assert.NotNull((await IndexAsync())[0].SourceModifiedUtc);
    }

    [Fact]
    public async Task VerifyVault_FindsDamagedAndMissingCopies()
    {
        Track("Good.flac");
        Track("Rot.flac");
        Track("Gone.flac");
        await _service.BackupAsync(Vault);
        var rot = File.ReadAllBytes(CopyOf("Rot.flac"));
        rot[3] ^= 0x01; // one flipped bit on the backup drive
        File.WriteAllBytes(CopyOf("Rot.flac"), rot);
        File.Delete(CopyOf("Gone.flac"));

        var result = await _service.VerifyVaultAsync(Vault);

        Assert.False(result.IsHealthy);
        Assert.Equal((3, 1, 1, 1), (result.Checked, result.Ok, result.Damaged, result.Missing));
        Assert.Contains(result.Problems, p => p.Contains("Rot.flac"));
        Assert.NotNull((await _service.GetStatusAsync(Vault))!.LastVerifiedUtc);
    }

    [Fact]
    public async Task VerifyVault_GivesOlderCopiesAChecksum_WhenTheOriginalIsUnchanged()
    {
        Track("Old.flac");
        await _service.BackupAsync(Vault);
        var index = await IndexAsync();
        index[0].FileSha256 = null; // copied before checksums existed
        await File.WriteAllTextAsync(Path.Combine(Vault, VaultService.IndexFile), System.Text.Json.JsonSerializer.Serialize(index));

        var result = await _service.VerifyVaultAsync(Vault);

        Assert.True(result.IsHealthy, result.Summary);
        Assert.Equal(1, result.ChecksumsAdded);
        Assert.NotNull((await IndexAsync())[0].FileSha256);
    }

    // ── restore to another folder / another computer ────────────────────────

    [Fact]
    public void ResolveLocalRoot_AnotherComputersPathOrAGoneDrive_IsNotUsable()
    {
        var root = new VaultRoot { Id = "r" };
        root.LocalPaths["this"] = OperatingSystem.IsWindows() ? @"Q:\Nowhere\Music" : "/Volumes/GoneDrive/Music";
        root.LocalPaths["mac"] = "/Users/dj/Music";

        Assert.False(VaultService.ResolveLocalRoot(root, "this", null).Usable);  // its drive isn't here
        Assert.False(VaultService.ResolveLocalRoot(root, "other-pc", null).Usable); // nothing recorded for this computer
        root.LocalPaths["this"] = Path.Combine(_root, "DeletedMusic"); // the folder is gone, the drive isn't
        Assert.True(VaultService.ResolveLocalRoot(root, "this", null).Usable);
        var chosen = VaultService.ResolveLocalRoot(root, "this", new System.Collections.Generic.Dictionary<string, string> { ["r"] = _music });
        Assert.True(chosen.Usable);
        Assert.Equal(_music, chosen.Path);

        if (!OperatingSystem.IsWindows())
        {
            root.LocalPaths["this"] = @"D:\Music"; // a Windows path is meaningless on a Mac
            Assert.False(VaultService.ResolveLocalRoot(root, "this", null).Usable);
        }
    }

    [Fact]
    public async Task RestoreToAChosenFolder_PutsTheFilesThere_AndRemembersItForThisComputer()
    {
        var one = Track("Artist/One.flac");
        await _service.BackupAsync(Vault);
        var bytes = File.ReadAllBytes(one);
        var newHome = Path.Combine(_root, "NewDrive", "Music");
        Directory.CreateDirectory(newHome);
        var rootId = (await _service.PlanRootsAsync(Vault, null)).Single().RootId;

        var result = await _service.RestoreMissingFilesAsync(Vault, new System.Collections.Generic.Dictionary<string, string> { [rootId] = newHome });

        Assert.Equal(1, result.Restored);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(newHome, "Artist", "One.flac")));
        var plan = Assert.Single(await _service.PlanRootsAsync(Vault, null));
        Assert.Equal(newHome, plan.LocalPath); // remembered: the next restore or backup from here uses it
        Assert.Equal(0, plan.MissingHere);
    }

    [Fact]
    public async Task RestoredCopy_ThatDoesntMatchItsChecksum_IsRefused()
    {
        var lost = Track("Lost.flac");
        await _service.BackupAsync(Vault);
        File.Delete(lost);
        var copy = File.ReadAllBytes(CopyOf("Lost.flac"));
        copy[1] ^= 0x10;
        File.WriteAllBytes(CopyOf("Lost.flac"), copy);

        var result = await _service.RestoreMissingFilesAsync(Vault);

        Assert.Equal((0, 1), (result.Restored, result.Failed));
        Assert.False(File.Exists(lost)); // never leave a damaged file where the library expects a good one
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
