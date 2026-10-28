using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>
/// Tests that PlaylistExportService deduplicates collection tracks by ResolvedFilePath
/// and blocks the entire export when any source audio file is missing.
/// </summary>
public class PlaylistExportDeduplicationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_Export_{Guid.NewGuid():N}");

    public PlaylistExportDeduplicationTests() => Directory.CreateDirectory(_tempDir);

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
            .UseInMemoryDatabase($"ExportTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private PlaylistExportService CreateService() =>
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new SLSKDONET.Tests.Helpers.FakeFileWriteService());

    // ── Duplicate track deduplication ─────────────────────────────────────

    [Fact]
    public async Task ExportToRekordboxXmlAsync_DuplicateFilePaths_WritesEachFileOnce()
    {
        var file = CreateAudioFile("track_a.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");

        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file },
            // Same physical file — second import of the same track
            new() { Id = Guid.NewGuid(), Title = "Track A (duplicate)", Artist = "Artist", ResolvedFilePath = file },
        };

        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var trackElements = doc.Descendants("TRACK").ToList();
        // COLLECTION tracks (have Location attribute)
        var collectionTracks = trackElements.Where(e => e.Attribute("Location") != null).ToList();

        Assert.Single(collectionTracks);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_UniquePaths_WritesAllTracks()
    {
        var fileA = CreateAudioFile("a.mp3");
        var fileB = CreateAudioFile("b.mp3");
        var fileC = CreateAudioFile("c.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");

        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "A", Artist = "Artist", ResolvedFilePath = fileA },
            new() { Id = Guid.NewGuid(), Title = "B", Artist = "Artist", ResolvedFilePath = fileB },
            new() { Id = Guid.NewGuid(), Title = "C", Artist = "Artist", ResolvedFilePath = fileC },
        };

        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var collectionTracks = doc.Descendants("TRACK")
            .Where(e => e.Attribute("Location") != null)
            .ToList();

        Assert.Equal(3, collectionTracks.Count);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MultipleDuplicates_DeduplicatesAll()
    {
        var fileA = CreateAudioFile("a.mp3");
        var fileB = CreateAudioFile("b.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");

        // 3 entries pointing to fileA, 2 entries pointing to fileB
        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "A1", Artist = "Artist", ResolvedFilePath = fileA },
            new() { Id = Guid.NewGuid(), Title = "A2", Artist = "Artist", ResolvedFilePath = fileA },
            new() { Id = Guid.NewGuid(), Title = "A3", Artist = "Artist", ResolvedFilePath = fileA },
            new() { Id = Guid.NewGuid(), Title = "B1", Artist = "Artist", ResolvedFilePath = fileB },
            new() { Id = Guid.NewGuid(), Title = "B2", Artist = "Artist", ResolvedFilePath = fileB },
        };

        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var collectionTracks = doc.Descendants("TRACK")
            .Where(e => e.Attribute("Location") != null)
            .ToList();

        Assert.Equal(2, collectionTracks.Count);
    }

    // ── Missing source audio is an export blocker ──────────────────────────

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MissingFile_BlocksEntireExport()
    {
        var existingFile = CreateAudioFile("real.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");
        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "Ghost", Artist = "Artist", ResolvedFilePath = "/nonexistent/ghost.mp3", TrackUniqueHash = "artist|ghost" },
            new() { Id = Guid.NewGuid(), Title = "Real Track", Artist = "Artist", ResolvedFilePath = existingFile, TrackUniqueHash = "artist|real" },
        };

        var exception = await Assert.ThrowsAsync<SLSKDONET.Services.Export.RekordboxExportIntegrityException>(() =>
            CreateService().ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml));

        Assert.Contains("audio file not found", exception.Message);
        Assert.False(File.Exists(outputXml));
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_EntryCountMatchesCollectionTracks()
    {
        var fileA = CreateAudioFile("x.mp3");
        var fileB = CreateAudioFile("b.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");

        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "A", Artist = "Artist", ResolvedFilePath = fileA },
            new() { Id = Guid.NewGuid(), Title = "B", Artist = "Artist", ResolvedFilePath = fileB },
        };

        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var entriesAttr = doc.Descendants("COLLECTION").First().Attribute("Entries");
        var collectionTracks = doc.Descendants("TRACK")
            .Where(e => e.Attribute("Location") != null)
            .ToList();

        Assert.Equal(collectionTracks.Count.ToString(), entriesAttr?.Value);
        Assert.Equal(2, collectionTracks.Count);
    }

    // ── Case-insensitive path deduplication ───────────────────────────────

    [Fact]
    public async Task ExportToRekordboxXmlAsync_SamePathDifferentCase_DeduplicatesOnWindows()
    {
        var file = CreateAudioFile("Track.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");

        // Windows paths are case-insensitive
        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "Upper", Artist = "Artist", ResolvedFilePath = file },
            new() { Id = Guid.NewGuid(), Title = "Lower", Artist = "Artist", ResolvedFilePath = file.ToLowerInvariant() },
        };

        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        // On Windows only one file actually exists, so at most one track will be written.
        // The test verifies the export doesn't crash and doesn't duplicate.
        var collectionTracks = doc.Descendants("TRACK")
            .Where(e => e.Attribute("Location") != null)
            .ToList();

        Assert.True(collectionTracks.Count <= 1);
    }

    // ── Helper: in-memory EF context factory ──────────────────────────────

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
