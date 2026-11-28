using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Files;
using SLSKDONET.Services.Vault;
using Xunit;

namespace SLSKDONET.Tests.Vault;

/// <summary>
/// Restoring on another computer (feedback 2026-11-28): the music folder is somewhere else there
/// (another drive letter, a Mac), and the library database restored from the Vault still points at
/// the first computer's paths. Copying the music back must go to the folder chosen here and leave
/// the library pointing at it.
/// </summary>
[Collection(NonParallelCollection.Name)] // FreshDatabaseScope swaps the process-wide ORBIT_DB_PATH
public sealed class VaultNewComputerTests : IDisposable
{
    private readonly FreshDatabaseScope _scope = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orbit-vault-move-{Guid.NewGuid():N}");

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AppDbContext());
    }

    public VaultNewComputerTests()
    {
        Directory.CreateDirectory(_root);
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _scope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private VaultService ServiceFor(string deviceFile) =>
        new(new Factory(), new VaultLocalState(Path.Combine(_root, deviceFile), NullLogger<VaultLocalState>.Instance),
            NullLogger<VaultService>.Instance, () => _scope.DbPath,
            new FileRelocationService(new Factory(), new Mock<IEventBus>().Object, NullLogger<FileRelocationService>.Instance));

    [Fact]
    public async Task NewComputer_ChoosesWhereTheMusicGoes_GetsItBack_AndTheLibraryPointsThere()
    {
        // Computer A: music under its own folder, backed up.
        var oldHome = Path.Combine(_root, "ComputerA", "Music");
        var oldFile = Path.Combine(oldHome, "DnB", "Drop.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
        var audio = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();
        File.WriteAllBytes(oldFile, audio);
        await using (var db = new AppDbContext())
        {
            db.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = oldHome });
            db.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = "drop", Artist = "A", Title = "Drop", FilePath = oldFile,
                AvailabilityState = TrackAvailabilityState.Ready, AudioContentHash = "pcm:drop",
            });
            db.PlaylistTracks.Add(new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = Guid.NewGuid(), TrackUniqueHash = "drop", Artist = "A", Title = "Drop", ResolvedFilePath = oldFile });
            db.Tracks.Add(new TrackEntity { GlobalId = "drop", Artist = "A", Title = "Drop", LocalFilePath = oldFile });
            await db.SaveChangesAsync();
        }
        var vault = VaultService.ResolveVaultRoot(Path.Combine(_root, "BackupDrive"));
        Directory.CreateDirectory(Path.GetDirectoryName(vault)!);
        var a = ServiceFor("device-a.json");
        Assert.True((await a.BackupAsync(vault)).IsHealthy);

        // Computer B: same library database (restored from the Vault), but computer A's folder isn't here.
        Directory.Delete(Path.Combine(_root, "ComputerA"), recursive: true);
        var b = ServiceFor("device-b.json");
        var plan = Assert.Single(await b.PlanRootsAsync(vault, null));
        Assert.False(plan.LocalPathUsable); // nothing recorded for computer B yet
        Assert.Contains(oldHome, plan.PathsOnOtherDevices);

        var newHome = Path.Combine(_root, "ComputerB", "Music");
        Directory.CreateDirectory(newHome);
        var result = await b.RestoreMissingFilesAsync(vault, new Dictionary<string, string> { [plan.RootId] = newHome });

        var newFile = Path.Combine(newHome, "DnB", "Drop.flac");
        Assert.Equal(1, result.Restored);
        Assert.Equal(audio, File.ReadAllBytes(newFile));
        Assert.Equal(1, result.LibraryPathsRepointed);
        await using (var db = new AppDbContext())
        {
            Assert.Equal(newFile, (await db.LibraryEntries.SingleAsync()).FilePath);
            Assert.Equal(newFile, (await db.PlaylistTracks.SingleAsync()).ResolvedFilePath);
            Assert.Equal(newFile, (await db.Tracks.SingleAsync()).LocalFilePath);
        }
    }
}
