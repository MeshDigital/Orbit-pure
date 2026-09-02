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
/// End-to-end tests for the three-way cue merge across two real
/// <see cref="PlaylistExportService.ExportToRekordboxXmlAsync"/> calls against the same target file
/// and the same (in-memory) database — the exact scenario the merge exists for: does a Cue Forge
/// edit made between exports propagate, and does a Rekordbox hand-edit survive re-export?
/// </summary>
public class PlaylistExportCueSyncMergeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportCueSync_{Guid.NewGuid():N}");

    public PlaylistExportCueSyncMergeTests() => Directory.CreateDirectory(_tempDir);

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

    private static PlaylistExportService CreateService(out IDbContextFactory<AppDbContext> factory)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ExportCueSyncTest_{Guid.NewGuid():N}")
            .Options;
        factory = new TestDbContextFactory(options);
        return new PlaylistExportService(NullLogger<PlaylistExportService>.Instance, factory);
    }

    private static List<OrbitCue> SingleCue(string name, double timestamp) =>
        new() { new OrbitCue { Timestamp = timestamp, Name = name, IsLoop = false, SlotIndex = 0 } };

    [Fact]
    public async Task SecondExport_CueEditedInOrbitSinceFirstExport_Propagates()
    {
        var file = CreateAudioFile("track.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");
        var service = CreateService(out _);

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file,
            TrackUniqueHash = "hash-1",
            CuePointsJson = JsonSerializer.Serialize(SingleCue("Drop V1", 10.0)),
        };

        await service.ExportToRekordboxXmlAsync("Playlist", new[] { track }, outputXml);

        // Simulate a Cue Forge edit before the second export — same track, moved/renamed cue.
        track.CuePointsJson = JsonSerializer.Serialize(SingleCue("Drop V2", 20.0));
        await service.ExportToRekordboxXmlAsync("Playlist", new[] { track }, outputXml);

        var element = XDocument.Load(outputXml).Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        var names = element.Elements("POSITION_MARK").Select(m => (string?)m.Attribute("Name")).ToList();

        Assert.Contains("Drop V2", names);
        Assert.DoesNotContain("Drop V1", names);
    }

    [Fact]
    public async Task SecondExport_CueHandEditedInRekordboxSinceFirstExport_IsPreserved()
    {
        var file = CreateAudioFile("track.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");
        var service = CreateService(out _);

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file,
            TrackUniqueHash = "hash-1",
            CuePointsJson = JsonSerializer.Serialize(SingleCue("Drop V1", 10.0)),
        };

        await service.ExportToRekordboxXmlAsync("Playlist", new[] { track }, outputXml);

        // Simulate the DJ renaming the cue directly in Rekordbox after the first export, without
        // going through ORBIT at all — mutate the on-disk XML the same way Rekordbox would on save.
        // SlotIndex=0 wrote a hot-cue mark plus its memory-cue duplicate; rename both.
        var doc = XDocument.Load(outputXml);
        foreach (var onDiskMark in doc.Descendants("POSITION_MARK"))
            onDiskMark.SetAttributeValue("Name", "Renamed In Rekordbox");
        doc.Save(outputXml);

        // ORBIT also has its own pending edit — this must NOT win, since Rekordbox changed first.
        track.CuePointsJson = JsonSerializer.Serialize(SingleCue("Drop V2", 20.0));
        await service.ExportToRekordboxXmlAsync("Playlist", new[] { track }, outputXml);

        var element = XDocument.Load(outputXml).Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        var names = element.Elements("POSITION_MARK").Select(m => (string?)m.Attribute("Name")).ToList();

        Assert.Contains("Renamed In Rekordbox", names);
        Assert.DoesNotContain("Drop V2", names);
    }

    [Fact]
    public async Task SecondExport_NothingChangedEitherSide_CuesStayStable()
    {
        var file = CreateAudioFile("track.mp3");
        var outputXml = Path.Combine(_tempDir, "out.xml");
        var service = CreateService(out _);

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file,
            TrackUniqueHash = "hash-1",
            CuePointsJson = JsonSerializer.Serialize(SingleCue("Drop", 10.0)),
        };

        await service.ExportToRekordboxXmlAsync("Playlist", new[] { track }, outputXml);
        await service.ExportToRekordboxXmlAsync("Playlist", new[] { track }, outputXml);

        var element = XDocument.Load(outputXml).Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        var names = element.Elements("POSITION_MARK").Select(m => (string?)m.Attribute("Name")).ToList();

        Assert.Contains("Drop", names);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
