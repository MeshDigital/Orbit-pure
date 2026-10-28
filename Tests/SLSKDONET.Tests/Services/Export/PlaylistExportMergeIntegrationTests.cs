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
/// End-to-end tests for non-destructive Rekordbox synchronization through the real
/// <see cref="PlaylistExportService.ExportToRekordboxXmlAsync"/> entry point: re-exporting to a
/// path that already has a Rekordbox XML file preserves existing values unless explicitly confirmed.
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
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new SLSKDONET.Tests.Helpers.FakeFileWriteService());

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
    public async Task ExportToRekordboxXmlAsync_ReExportToSamePath_PreservesBpmAndRatingWithoutConfirmation()
    {
        var file = CreateAudioFile("a.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a", BPM = 120 };
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");

        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null).SetAttributeValue("Rating", "153");
        doc.Save(outputXml);

        track.BPM = 174;
        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var mergedTrack = XDocument.Load(outputXml).Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        Assert.Equal("153", mergedTrack.Attribute("Rating")!.Value);
        Assert.Equal("120.00", mergedTrack.Attribute("AverageBpm")!.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_ReExportToSamePath_ReplacesPreviewedValuesAfterConfirmation()
    {
        var file = CreateAudioFile("a.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a", BPM = 120 };
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");

        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);
        var doc = XDocument.Load(outputXml);
        doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null).SetAttributeValue("Rating", "153");
        doc.Save(outputXml);

        track.BPM = 174;
        var previewedFields = Array.Empty<string>();
        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml,
            confirmOverwrite: conflicts =>
            {
                previewedFields = conflicts.Tracks.Single().Fields.ToArray();
                return Task.FromResult(true);
            });

        var mergedTrack = XDocument.Load(outputXml).Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        Assert.Contains("BPM", previewedFields);
        Assert.Contains("rating", previewedFields);
        Assert.Equal("0", mergedTrack.Attribute("Rating")!.Value);
        Assert.Equal("174.00", mergedTrack.Attribute("AverageBpm")!.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_ExistingNonRekordboxFileAtPath_BlocksAndPreservesOriginal()
    {
        var file = CreateAudioFile("a.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "Track A", Artist = "Artist", ResolvedFilePath = file, TrackUniqueHash = "artist|track-a" };
        var outputXml = Path.Combine(_tempDir, "rekordbox.xml");
        const string original = "<not-a-rekordbox-file/>";

        await File.WriteAllTextAsync(outputXml, original);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml));

        Assert.Equal(original, await File.ReadAllTextAsync(outputXml));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
