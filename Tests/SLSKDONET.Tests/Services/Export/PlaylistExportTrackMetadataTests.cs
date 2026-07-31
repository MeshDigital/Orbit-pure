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
/// Tests that the Rekordbox XML export writes the user's real Rating/Comments/ColorTag
/// fields verbatim instead of synthesizing them from energy/key data.
/// </summary>
public class PlaylistExportTrackMetadataTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportMeta_{Guid.NewGuid():N}");

    public PlaylistExportTrackMetadataTests() => Directory.CreateDirectory(_tempDir);

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
            .UseInMemoryDatabase($"ExportMetaTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private PlaylistExportService CreateService() =>
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory());

    private async Task<XElement> ExportSingleTrackAndGetElementAsync(PlaylistTrack track)
    {
        var outputXml = Path.Combine(_tempDir, $"out_{Guid.NewGuid():N}.xml");
        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        return doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "51")]
    [InlineData(2, "102")]
    [InlineData(3, "153")]
    [InlineData(4, "204")]
    [InlineData(5, "255")]
    public async Task ExportToRekordboxXmlAsync_RealRating_WritesCorrectRekordboxScale(int rating, string expected)
    {
        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file, Rating = rating };

        var element = await ExportSingleTrackAndGetElementAsync(track);

        Assert.Equal(expected, element.Attribute("Rating")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_RealComments_ArePreservedVerbatim()
    {
        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file,
            Comments = "Great transition into next track",
            MusicalKey = "8A", // previously would have been prepended to Comments — must not be
        };

        var element = await ExportSingleTrackAndGetElementAsync(track);

        Assert.Equal("Great transition into next track", element.Attribute("Comments")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_NoComments_WritesEmptyCommentsAttribute()
    {
        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file };

        var element = await ExportSingleTrackAndGetElementAsync(track);

        Assert.Equal(string.Empty, element.Attribute("Comments")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_ColorTagSet_WritesColourAttribute()
    {
        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file, ColorTag = "#FF0000" };

        var element = await ExportSingleTrackAndGetElementAsync(track);

        Assert.Equal("FF0000", element.Attribute("Colour")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_ColorTagNull_OmitsColourAttribute()
    {
        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file, ColorTag = null };

        var element = await ExportSingleTrackAndGetElementAsync(track);

        Assert.Null(element.Attribute("Colour"));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
