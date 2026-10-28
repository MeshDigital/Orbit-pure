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
/// Confirms malformed-input handling at the export trust boundary: an invalid cue colour uses
/// the explicit white fallback, while malformed saved cue JSON blocks export instead of
/// silently dropping the user's preparation work.
/// </summary>
public class PlaylistExportLoggingHygieneTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportLogging_{Guid.NewGuid():N}");

    public PlaylistExportLoggingHygieneTests() => Directory.CreateDirectory(_tempDir);

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
            .UseInMemoryDatabase($"ExportLoggingTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private PlaylistExportService CreateService() =>
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory(), new SLSKDONET.Tests.Helpers.FakeFileWriteService());

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MalformedCueColor_FallsBackToWhite()
    {
        var file = CreateAudioFile("track.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");
        var cues = new List<OrbitCue> { new() { Timestamp = 5.0, Name = "Cue", Color = "not-a-hex-color", SlotIndex = 0 } };
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file,
            CuePointsJson = System.Text.Json.JsonSerializer.Serialize(cues),
        };

        // Must not throw.
        await CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        // SlotIndex=0 triggers the hot-cue/memory-cue dual write (item 6) — both copies must
        // share the same (fallback) colour.
        var marks = doc.Descendants("POSITION_MARK").Where(m => m.Attribute("Name")?.Value == "Cue").ToList();

        Assert.NotEmpty(marks);
        Assert.All(marks, m =>
        {
            Assert.Equal("255", m.Attribute("Red")?.Value);
            Assert.Equal("255", m.Attribute("Green")?.Value);
            Assert.Equal("255", m.Attribute("Blue")?.Value);
        });
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MalformedCuePointsJson_BlocksExportInsteadOfDroppingCues()
    {
        var file = CreateAudioFile("track.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file,
            CuePointsJson = "{this is not valid JSON at all",
        };

        var exception = await Assert.ThrowsAsync<SLSKDONET.Services.Export.RekordboxExportIntegrityException>(() =>
            CreateService().ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml));

        Assert.Contains("saved user cues are malformed", exception.Message);
        Assert.False(File.Exists(outputXml));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
