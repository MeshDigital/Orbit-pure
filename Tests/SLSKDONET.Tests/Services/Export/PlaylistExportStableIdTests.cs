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
/// Tests that Rekordbox XML TrackID values are stable (deterministic from TrackUniqueHash)
/// across separate export runs, rather than a fresh sequential counter every time.
/// </summary>
public class PlaylistExportStableIdTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportStableId_{Guid.NewGuid():N}");

    public PlaylistExportStableIdTests() => Directory.CreateDirectory(_tempDir);

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
            .UseInMemoryDatabase($"ExportStableIdTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private PlaylistExportService CreateService() =>
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory());

    private static string GetTrackId(XDocument doc, string title) =>
        doc.Descendants("TRACK")
            .Where(e => e.Attribute("Location") != null)
            .Single(e => e.Attribute("Name")?.Value == title)
            .Attribute("TrackID")!.Value;

    [Fact]
    public async Task ExportToRekordboxXmlAsync_SameTrackTwoExports_ProducesSameTrackId()
    {
        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Stable Track", Artist = "A", ResolvedFilePath = file, TrackUniqueHash = "artist|stable-track" };

        var outputA = Path.Combine(_tempDir, "a.xml");
        var outputB = Path.Combine(_tempDir, "b.xml");

        await CreateService().ExportToRekordboxXmlAsync("Playlist A", new[] { track }, outputA);
        await CreateService().ExportToRekordboxXmlAsync("Playlist B", new[] { track }, outputB);

        var idA = GetTrackId(XDocument.Load(outputA), "Stable Track");
        var idB = GetTrackId(XDocument.Load(outputB), "Stable Track");

        Assert.Equal(idA, idB);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_DifferentTracks_ProduceDifferentTrackIds()
    {
        var fileA = CreateAudioFile("a.mp3");
        var fileB = CreateAudioFile("b.mp3");
        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = fileA, TrackUniqueHash = "artist|track-a" },
            new() { Id = Guid.NewGuid(), Title = "Track B", Artist = "Artist", ResolvedFilePath = fileB, TrackUniqueHash = "artist|track-b" },
        };
        var outputXml = Path.Combine(_tempDir, "out.xml");

        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var idA = GetTrackId(doc, "Track A");
        var idB = GetTrackId(doc, "Track B");

        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MissingFileFirst_CuesAssignedToCorrectTrack()
    {
        // Regression guard: confirms the stable-TrackID change didn't reintroduce the
        // index-mismatch bug PlaylistExportDeduplicationTests already guards against.
        var existingFile = CreateAudioFile("real.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");

        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), Title = "Ghost", Artist = "Artist", ResolvedFilePath = "/nonexistent/ghost.mp3", TrackUniqueHash = "artist|ghost" },
            new() { Id = Guid.NewGuid(), Title = "Real Track", Artist = "Artist", ResolvedFilePath = existingFile, TrackUniqueHash = "artist|real" },
        };

        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", tracks, outputXml);

        var doc = XDocument.Load(outputXml);
        var collectionTracks = doc.Descendants("TRACK").Where(e => e.Attribute("Location") != null).ToList();

        Assert.Single(collectionTracks);
        Assert.Equal("Real Track", collectionTracks[0].Attribute("Name")?.Value);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
