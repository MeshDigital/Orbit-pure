using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Export;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Library;
using SLSKDONET.Tests.Helpers;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>
/// Regression coverage for the Rekordbox M3U8 playlist export. Rekordbox's M3U8 importer requires
/// "#EXTM3U" to be the literal first bytes of the file — System.Text.Encoding.UTF8 (the static
/// instance ExportToM3uAsync used to pass to File.WriteAllLinesAsync) writes a UTF-8 byte-order-mark
/// ahead of it by default, which Rekordbox doesn't strip, so an otherwise well-formed playlist
/// silently failed to import. Also now routes through IFileWriteService.WriteAtomicAsync, same as
/// the Rekordbox XML export, since this is routinely written straight to a USB drive.
/// </summary>
public class PlaylistExportM3uTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportM3u_{Guid.NewGuid():N}");

    public PlaylistExportM3uTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateAudioFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        using var writer = new NAudio.Wave.WaveFileWriter(
            path, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
        for (var i = 0; i < 8000; i++)
            writer.WriteSample(0.01f);
        return path;
    }

    private static IDbContextFactory<AppDbContext> CreateInMemoryFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ExportM3uTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

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
    public async Task ExportToM3uAsync_WritesNoByteOrderMark()
    {
        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new FakeFileWriteService());

        var file = CreateAudioFile("a.wav");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a" };
        var outputPath = Path.Combine(_tempDir, "playlist.m3u8");

        await service.ExportToM3uAsync("Test Playlist", new[] { track }, outputPath);

        var rawBytes = await File.ReadAllBytesAsync(outputPath);
        var utf8Bom = System.Text.Encoding.UTF8.GetPreamble();
        Assert.False(rawBytes.Length >= utf8Bom.Length && rawBytes.Take(utf8Bom.Length).SequenceEqual(utf8Bom),
            "M3U8 output must not start with a UTF-8 BOM — Rekordbox requires #EXTM3U as the literal first bytes.");

        // "#EXTM3U" as literal ASCII bytes right at offset 0 (what Rekordbox actually checks for).
        var expectedHeader = System.Text.Encoding.ASCII.GetBytes("#EXTM3U");
        Assert.True(rawBytes.Take(expectedHeader.Length).SequenceEqual(expectedHeader));
    }

    [Fact]
    public async Task ExportToM3uAsync_ContainsExpectedPlaylistAndTrackLines()
    {
        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new FakeFileWriteService());

        var file = CreateAudioFile("a.wav");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a", CanonicalDuration = 215000 };
        var outputPath = Path.Combine(_tempDir, "playlist.m3u8");

        await service.ExportToM3uAsync("Test Playlist", new[] { track }, outputPath);

        var lines = await File.ReadAllLinesAsync(outputPath);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#PLAYLIST:Test Playlist", lines[1]);
        Assert.Equal("#EXTINF:215,Artist - Track A", lines[2]);
        Assert.Equal(file, lines[3]);
    }

    [Fact]
    public async Task ExportToM3uAsync_BlocksWhenEveryTrackIsMissing()
    {
        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new FakeFileWriteService());

        var missingTrack = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Gone", Artist = "Artist", ResolvedFilePath = Path.Combine(_tempDir, "nope.mp3"), TrackUniqueHash = "artist|gone" };
        var outputPath = Path.Combine(_tempDir, "playlist.m3u8");

        var error = await Assert.ThrowsAsync<TrackExportValidationException>(() =>
            service.ExportToM3uAsync("Test Playlist", new[] { missingTrack }, outputPath));
        Assert.Contains("Gone", error.Message);
        Assert.False(File.Exists(outputPath));
    }

    /// <summary>
    /// Regression coverage for a real reported bug: the caller (LibraryViewModel.Commands'
    /// ExecuteExportPlaylistM3uAsync / ExecuteBatchExportM3uAsync) showed a "N track(s) exported"
    /// notification built from the INPUT playlist count, not this method's actual post-filter
    /// output — so a playlist with, say, 47 tracks where only 35 were downloaded claimed "47
    /// exported" even though the file only ever contained the 35 that actually existed on disk.
    /// Missing or unreadable files are omitted when usable files remain, and reported explicitly.
    /// Successful exports report actual output counts.
    /// </summary>
    [Fact]
    public async Task ExportToM3uAsync_ExportsValidTracksAndReportsMissingTracks()
    {
        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new FakeFileWriteService());

        var downloadedA = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Here A", Artist = "Artist", ResolvedFilePath = CreateAudioFile("a.wav"), TrackUniqueHash = "artist|a" };
        var downloadedB = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Here B", Artist = "Artist", ResolvedFilePath = CreateAudioFile("b.wav"), TrackUniqueHash = "artist|b" };
        var notDownloaded = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Not Downloaded", Artist = "Artist", ResolvedFilePath = Path.Combine(_tempDir, "missing.mp3"), TrackUniqueHash = "artist|missing" };
        var noPathAtAll = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Pending", Artist = "Artist", ResolvedFilePath = null, TrackUniqueHash = "artist|pending" };
        var outputPath = Path.Combine(_tempDir, "playlist.m3u8");

        var result = await service.ExportToM3uAsync(
            "Test Playlist", new[] { downloadedA, downloadedB, notDownloaded, noPathAtAll }, outputPath);

        Assert.Equal(2, result.Exported);
        Assert.Equal(2, result.SkippedTrackProblems.Count);
        Assert.Contains(result.SkippedTrackProblems, problem => problem.Contains("Not Downloaded"));
        Assert.Contains(result.SkippedTrackProblems, problem => problem.Contains("Pending"));
        Assert.Equal(
            new[] { downloadedA.ResolvedFilePath, downloadedB.ResolvedFilePath },
            (await File.ReadAllLinesAsync(outputPath)).Where(line => !line.StartsWith('#')));
    }

    [Fact]
    public async Task ExportToM3uAsync_SkipsUnreadableAudioAndExportsOtherTracks()
    {
        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new FakeFileWriteService());
        var path = Path.Combine(_tempDir, "broken.wav");
        await File.WriteAllTextAsync(path, "not an audio file");
        var track = new PlaylistTrack { Title = "Broken", Artist = "Artist", ResolvedFilePath = path };
        var validFile = CreateAudioFile("valid.wav");
        var validTrack = new PlaylistTrack { Title = "Valid", Artist = "Artist", ResolvedFilePath = validFile };
        var outputPath = Path.Combine(_tempDir, "playlist.m3u8");

        var result = await service.ExportToM3uAsync("Test Playlist", new[] { track, validTrack }, outputPath);

        Assert.Equal(1, result.Exported);
        Assert.Single(result.SkippedTrackProblems);
        Assert.Contains("Broken", result.SkippedTrackProblems[0]);
        Assert.Contains(path, result.SkippedTrackProblems[0]);
        Assert.Contains(validFile, await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task UsbExport_BlocksInvalidSourceBeforeCreatingDestination()
    {
        var exportService = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new FakeFileWriteService());
        var orchestrator = new UsbExportOrchestrator(
            exportService, new FakeFileWriteService(), NullLogger<UsbExportOrchestrator>.Instance);
        var destination = Path.Combine(_tempDir, "usb");
        var missing = new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Missing USB track",
            ResolvedFilePath = Path.Combine(_tempDir, "gone.wav"),
        };

        var error = await Assert.ThrowsAsync<TrackExportValidationException>(() =>
            orchestrator.ExportAsync(
                "Test Playlist", new[] { missing }, destination, ExportMode.FilesAndXml));

        Assert.Contains("Missing USB track", error.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExportToM3uAsync_WhenAtomicWriteFails_ThrowsInsteadOfSilentlySucceeding()
    {
        var service = new PlaylistExportService(
            NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new AlwaysFailFileWriteService());

        var file = CreateAudioFile("a.wav");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a" };
        var outputPath = Path.Combine(_tempDir, "playlist.m3u8");

        await Assert.ThrowsAsync<IOException>(() =>
            service.ExportToM3uAsync("Test Playlist", new[] { track }, outputPath));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
