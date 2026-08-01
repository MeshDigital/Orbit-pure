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
/// End-to-end tests for the Phase 2 refresh/merge-mode behavior through the real
/// <see cref="PlaylistExportService.ExportToRekordboxXmlAsync"/> entry point: re-exporting to a
/// path that already has a Rekordbox XML file must merge into it rather than overwrite it.
/// </summary>
public class PlaylistExportMergeIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportMerge_{Guid.NewGuid():N}");

    public PlaylistExportMergeIntegrationTests() => Directory.CreateDirectory(_tempDir);

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
            .UseInMemoryDatabase($"ExportMergeTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private static PlaylistExportService CreateService() =>
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory());

    [Fact]
    public async Task ExportToRekordboxXmlAsync_ReExportToSamePath_PreservesHandEditedRatingAndColour()
    {
        var fileA = CreateAudioFile("a.mp3");
        var trackA = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = fileA, TrackUniqueHash = "artist|track-a", Rating = 2 };
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");

        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { trackA }, outputXml);

        // Simulate the user re-rating and re-colouring the track directly inside Rekordbox.
        var doc = XDocument.Load(outputXml);
        var trackElem = doc.Descendants("TRACK").Single(e => e.Attribute("Name")?.Value == "Track A");
        trackElem.SetAttributeValue("Rating", "255");
        trackElem.SetAttributeValue("Colour", "AA00FF");
        doc.Save(outputXml);

        // Re-export the same playlist with a newly downloaded second track added.
        var fileB = CreateAudioFile("b.mp3");
        var trackB = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track B", Artist = "Artist", ResolvedFilePath = fileB, TrackUniqueHash = "artist|track-b", Rating = 0 };

        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { trackA, trackB }, outputXml);

        var merged = XDocument.Load(outputXml);
        var mergedTrackA = merged.Descendants("TRACK").Single(e => e.Attribute("Name")?.Value == "Track A");
        var mergedTrackB = merged.Descendants("TRACK").SingleOrDefault(e => e.Attribute("Name")?.Value == "Track B");

        Assert.Equal("255", mergedTrackA.Attribute("Rating")!.Value);
        Assert.Equal("AA00FF", mergedTrackA.Attribute("Colour")!.Value);
        Assert.NotNull(mergedTrackB);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_ReExportToSamePath_RefreshesBpmEvenWhenRatingPreserved()
    {
        var file = CreateAudioFile("a.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a", BPM = 120 };
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");

        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null).SetAttributeValue("Rating", "153");
        doc.Save(outputXml);

        track.BPM = 174; // ORBIT re-analyzed the track with a corrected BPM
        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var merged = XDocument.Load(outputXml);
        var mergedTrack = merged.Descendants("TRACK").Single(e => e.Attribute("Location") != null);

        Assert.Equal("153", mergedTrack.Attribute("Rating")!.Value); // preserved
        Assert.Equal("174.00", mergedTrack.Attribute("AverageBpm")!.Value); // refreshed
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_ExistingNonRekordboxFileAtPath_FallsBackToOverwrite()
    {
        var file = CreateAudioFile("a.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a" };
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");

        await File.WriteAllTextAsync(outputXml, "<not-a-rekordbox-file/>");

        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        Assert.Equal("DJ_PLAYLISTS", doc.Root!.Name.LocalName);
        Assert.Single(doc.Descendants("TRACK").Where(e => e.Attribute("Location") != null));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
