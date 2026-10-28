using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>
/// Regression coverage for a real bug: <see cref="PlaylistExportService.ExportToRekordboxXmlAsync"/>
/// used to call <c>XDocument.Save(targetPath)</c> directly, which truncates the target file before
/// streaming new content — a crash, a killed process, or a USB drive pulled mid-write left a
/// corrupted, truncated <c>rekordbox.xml</c>. Worse, on a merge-mode re-export this would destroy
/// the EXISTING file, including whatever ratings/colours/cues the user had already edited inside
/// Rekordbox — exactly what the merge feature exists to protect. The fix routes the write through
/// <see cref="IFileWriteService.WriteAtomicAsync"/> (temp file, verified, then renamed into place).
/// These tests use a fake write service that can be told to fail, to prove the existing file
/// survives a failed write instead of being silently destroyed.
/// </summary>
public class PlaylistExportAtomicWriteTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportAtomic_{Guid.NewGuid():N}");

    public PlaylistExportAtomicWriteTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateAudioFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "fake audio");
        return path;
    }

    private static IDbContextFactory<AppDbContext> CreateInMemoryFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ExportAtomicTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    /// <summary>Always reports write failure without touching the target path — simulates a crash,
    /// a disk-full condition, or a verification failure partway through the real atomic write.</summary>
    private sealed class AlwaysFailFileWriteService : IFileWriteService
    {
        public Task<bool> WriteAtomicAsync(string targetPath, Func<string, Task> writeAction, Func<string, Task<bool>>? verifyAction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<bool> WriteAllBytesAtomicAsync(string targetPath, byte[] data, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<bool> CopyFileAtomicAsync(string sourcePath, string targetPath, bool preserveTimestamps = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<bool> MoveAtomicAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_WhenAtomicWriteFails_ThrowsInsteadOfSilentlySucceeding()
    {
        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new AlwaysFailFileWriteService());

        var file = CreateAudioFile("a.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a" };
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");

        await Assert.ThrowsAsync<IOException>(() =>
            service.ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml));
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_WhenAtomicWriteFails_LeavesExistingFileUntouched()
    {
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");
        const string originalContent = "<DJ_PLAYLISTS>this is the user's real, hand-edited Rekordbox export</DJ_PLAYLISTS>";
        await File.WriteAllTextAsync(outputXml, originalContent);

        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new AlwaysFailFileWriteService());

        var file = CreateAudioFile("a.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a" };

        await Assert.ThrowsAsync<IOException>(() =>
            service.ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml));

        // The whole point of the fix: a failed write must never touch — let alone truncate or
        // delete — a pre-existing file at the target path.
        Assert.True(File.Exists(outputXml));
        Assert.Equal(originalContent, await File.ReadAllTextAsync(outputXml));
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_SuccessfulWrite_ProducesValidXmlOnDisk()
    {
        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new SLSKDONET.Tests.Helpers.FakeFileWriteService());

        var file = CreateAudioFile("a.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a" };
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");

        await service.ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        Assert.True(File.Exists(outputXml));
        var doc = System.Xml.Linq.XDocument.Load(outputXml);
        Assert.Equal("DJ_PLAYLISTS", doc.Root?.Name.LocalName);
        // No leftover .tmp file from the atomic write should remain after a successful export.
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
