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
/// Tests that PlaylistExportService deduplicates tracks by ResolvedFilePath
/// and that cue points are assigned to the correct track even when some
/// playlist tracks have missing files and are skipped.
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
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory());

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

    // ── Index-mismatch: cues go to the correct track ───────────────────────

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MissingFileFirst_CuesAssignedToCorrectTrack()
    {
        // trackList[0] → missing file (will be skipped)
        // trackList[1] → file exists, has cue points
        // Before the fix, rbTracks[0] would be mapped to trackList[0] (missing)
        // and its cues would be looked up using the wrong hash.

        var existingFile = CreateAudioFile("real.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");

        const string hashWithCues = "artist|trackwithcues";

        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "Ghost",         Artist = "Artist", ResolvedFilePath = "/nonexistent/ghost.mp3", TrackUniqueHash = "artist|ghost" },
            new() { Id = Guid.NewGuid(), Title = "Track With Cues", Artist = "Artist", ResolvedFilePath = existingFile,            TrackUniqueHash = hashWithCues },
        };

        // Cue data is loaded from DB in the real service; with an in-memory DB with no rows the
        // test verifies track metadata (Name) is correct, proving the correct src track was used.
        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var collectionTracks = doc.Descendants("TRACK")
            .Where(e => e.Attribute("Location") != null)
            .ToList();

        Assert.Single(collectionTracks);
        var name = collectionTracks[0].Attribute("Name")?.Value;
        Assert.Equal("Track With Cues", name);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_EntryCountMatchesCollectionTracks()
    {
        var fileA = CreateAudioFile("x.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");

        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "A", Artist = "Artist", ResolvedFilePath = fileA },
            new() { Id = Guid.NewGuid(), Title = "B", Artist = "Artist", ResolvedFilePath = "/nonexistent/b.mp3" },
        };

        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var entriesAttr = doc.Descendants("COLLECTION").First().Attribute("Entries");
        var collectionTracks = doc.Descendants("TRACK")
            .Where(e => e.Attribute("Location") != null)
            .ToList();

        Assert.Equal(collectionTracks.Count.ToString(), entriesAttr?.Value);
        Assert.Equal(1, collectionTracks.Count); // B was skipped (missing)
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
