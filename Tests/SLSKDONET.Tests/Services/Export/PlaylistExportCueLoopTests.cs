using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Tests hot-loop pad assignment (honoring an explicit SlotIndex on a loop, previously always
/// forced to Num=-1) and the hot-cue/memory-cue dual write (a cue on a hot-cue pad also gets a
/// memory-cue duplicate at the same position).
/// </summary>
public class PlaylistExportCueLoopTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportCueLoop_{Guid.NewGuid():N}");

    public PlaylistExportCueLoopTests() => Directory.CreateDirectory(_tempDir);

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
            .UseInMemoryDatabase($"ExportCueLoopTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private PlaylistExportService CreateService() =>
        new(NullLogger<PlaylistExportService>.Instance, CreateInMemoryFactory());

    private async Task<XElement> ExportTrackWithCuesAndGetElementAsync(List<OrbitCue> cues)
    {
        var file = CreateAudioFile("track.mp3");
        var outputXml = Path.Combine(_tempDir, $"out_{Guid.NewGuid():N}.xml");
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file,
            CuePointsJson = JsonSerializer.Serialize(cues),
        };

        var service = CreateService();
        await service.ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        return doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_LoopWithSlotIndex_WritesHotLoopNum()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 10.0, Name = "Loop", IsLoop = true, LoopEndSeconds = 14.0, SlotIndex = 3 },
        };

        var element = await ExportTrackWithCuesAndGetElementAsync(cues);
        var loopMark = element.Elements("POSITION_MARK").Single(m => m.Attribute("Type")?.Value == "4");

        Assert.Equal("3", loopMark.Attribute("Num")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_LoopWithoutSlotIndex_WritesMemoryLoopNumMinusOne()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 10.0, Name = "Loop", IsLoop = true, LoopEndSeconds = 14.0, SlotIndex = -1 },
        };

        var element = await ExportTrackWithCuesAndGetElementAsync(cues);
        var loopMark = element.Elements("POSITION_MARK").Single(m => m.Attribute("Type")?.Value == "4");

        Assert.Equal("-1", loopMark.Attribute("Num")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_LoopReservesPointCuePadAssignment()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 10.0, Name = "Loop", IsLoop = true, LoopEndSeconds = 14.0, SlotIndex = 0 },
            new() { Timestamp = 20.0, Name = "Cue1", IsLoop = false, SlotIndex = -1 }, // auto-assign
            new() { Timestamp = 30.0, Name = "Cue2", IsLoop = false, SlotIndex = -1 }, // auto-assign
        };

        var element = await ExportTrackWithCuesAndGetElementAsync(cues);
        var pointMarks = element.Elements("POSITION_MARK")
            .Where(m => m.Attribute("Type")?.Value == "0" && m.Attribute("Num")?.Value != "-1")
            .OrderBy(m => double.Parse(m.Attribute("Start")!.Value))
            .ToList();

        // Pad 0 is reserved by the loop — auto-assigned point cues must skip it.
        Assert.Equal("1", pointMarks[0].Attribute("Num")?.Value);
        Assert.Equal("2", pointMarks[1].Attribute("Num")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_HotCue_AlsoWritesMemoryCueDuplicate()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 15.0, Name = "Drop", IsLoop = false, SlotIndex = 2 },
        };

        var element = await ExportTrackWithCuesAndGetElementAsync(cues);
        var marks = element.Elements("POSITION_MARK").Where(m => m.Attribute("Type")?.Value == "0").ToList();

        Assert.Equal(2, marks.Count);
        Assert.Contains(marks, m => m.Attribute("Num")?.Value == "2");
        Assert.Contains(marks, m => m.Attribute("Num")?.Value == "-1");
        Assert.All(marks, m => Assert.Equal("15.000", m.Attribute("Start")?.Value));
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MemoryOnlyCue_WritesSingleEntry()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 15.0, Name = "Marker", IsLoop = false, SlotIndex = -1 },
        };

        // With no other cues present, auto-assignment will still claim pad 0 for this cue —
        // to test a genuinely memory-cue-only scenario, fill all 8 pads with other cues first.
        var fillerCues = Enumerable.Range(0, 8)
            .Select(i => new OrbitCue { Timestamp = i + 1, Name = $"Filler{i}", IsLoop = false, SlotIndex = i })
            .ToList();
        fillerCues.Add(new OrbitCue { Timestamp = 15.0, Name = "Marker", IsLoop = false, SlotIndex = -1 });

        var element = await ExportTrackWithCuesAndGetElementAsync(fillerCues);
        var markerMarks = element.Elements("POSITION_MARK")
            .Where(m => m.Attribute("Name")?.Value == "Marker")
            .ToList();

        Assert.Single(markerMarks);
        Assert.Equal("-1", markerMarks[0].Attribute("Num")?.Value);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
