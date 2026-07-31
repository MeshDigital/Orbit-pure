using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>
/// Integration tests (real service + in-memory DB) confirming the exporter correctly wires
/// AudioFeaturesEntity beatgrid data into real TEMPO anchors instead of the old hardcoded
/// Inizio="0.000" single anchor.
/// </summary>
public class PlaylistExportTempoTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportTempo_{Guid.NewGuid():N}");

    public PlaylistExportTempoTests() => Directory.CreateDirectory(_tempDir);

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

    private static DbContextOptions<AppDbContext> CreateInMemoryOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ExportTempoTest_{Guid.NewGuid():N}")
            .Options;

    private static PlaylistExportService CreateService(DbContextOptions<AppDbContext> options) =>
        new(NullLogger<PlaylistExportService>.Instance, new TestDbContextFactory(options));

    private static List<double> BuildBeatGrid(params (int Count, double Bpm)[] segments)
    {
        var beats = new List<double>();
        double t = 0.417; // arbitrary non-zero downbeat offset
        foreach (var (count, bpm) in segments)
        {
            var interval = 60.0 / bpm;
            for (int i = 0; i < count; i++)
            {
                beats.Add(t);
                t += interval;
            }
        }
        return beats;
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_NoBeatGridData_WritesSingleTempoWithDownbeatOffsetNotZero()
    {
        var options = CreateInMemoryOptions();
        var hash = "artist|no-beatgrid";
        var downbeatOffset = 0.417;

        await using (var db = new AppDbContext(options))
        {
            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash,
                DownbeatOffsetSeconds = downbeatOffset,
                BeatGridJson = "[]",
                BpmStability = 0.9f,
            });
            await db.SaveChangesAsync();
        }

        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file, TrackUniqueHash = hash, BPM = 128.0 };
        var outputXml = Path.Combine(_tempDir, "out.xml");

        await CreateService(options).ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        var trackElem = doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        var tempoNodes = trackElem.Elements("TEMPO").ToList();

        Assert.Single(tempoNodes);
        var inizio = double.Parse(tempoNodes[0].Attribute("Inizio")!.Value, CultureInfo.InvariantCulture);
        Assert.Equal(downbeatOffset, inizio, precision: 3);
        Assert.NotEqual("0.000", tempoNodes[0].Attribute("Inizio")?.Value);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MultiAnchorTrack_WritesMultipleTempoNodes()
    {
        var options = CreateInMemoryOptions();
        var hash = "artist|drifting-tempo";
        var beats = BuildBeatGrid((60, 128.0), (60, 132.0));

        await using (var db = new AppDbContext(options))
        {
            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash,
                DownbeatOffsetSeconds = beats[0],
                BeatGridJson = JsonSerializer.Serialize(beats),
                BpmStability = 0.3f, // unstable — eligible for multi-anchor derivation
            });
            await db.SaveChangesAsync();
        }

        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file, TrackUniqueHash = hash, BPM = 128.0 };
        var outputXml = Path.Combine(_tempDir, "out.xml");

        await CreateService(options).ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        var trackElem = doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        var tempoNodes = trackElem.Elements("TEMPO").ToList();

        Assert.True(tempoNodes.Count >= 2, $"Expected multiple TEMPO anchors, got {tempoNodes.Count}");
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_MalformedBeatGridJson_LogsWarningAndFallsBackGracefully()
    {
        var options = CreateInMemoryOptions();
        var hash = "artist|malformed";

        await using (var db = new AppDbContext(options))
        {
            db.AudioFeatures.Add(new AudioFeaturesEntity
            {
                TrackUniqueHash = hash,
                DownbeatOffsetSeconds = 0.5,
                BeatGridJson = "{not valid json array",
                BpmStability = 0.1f,
            });
            await db.SaveChangesAsync();
        }

        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file, TrackUniqueHash = hash, BPM = 128.0 };
        var outputXml = Path.Combine(_tempDir, "out.xml");

        // Must not throw — falls back to a single anchor.
        await CreateService(options).ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        var trackElem = doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        var tempoNodes = trackElem.Elements("TEMPO").ToList();

        Assert.Single(tempoNodes);
    }

    [Fact]
    public async Task ExportToRekordboxXmlAsync_NoAudioFeaturesRow_FallsBackToZeroOffsetSingleAnchor()
    {
        var options = CreateInMemoryOptions();
        var file = CreateAudioFile("track.mp3");
        var track = new PlaylistTrack { Id = Guid.NewGuid(), Title = "T", Artist = "A", ResolvedFilePath = file, TrackUniqueHash = "artist|no-row", BPM = 128.0 };
        var outputXml = Path.Combine(_tempDir, "out.xml");

        await CreateService(options).ExportToRekordboxXmlAsync("Test Playlist", new[] { track }, outputXml);

        var doc = XDocument.Load(outputXml);
        var trackElem = doc.Descendants("TRACK").Single(e => e.Attribute("Location") != null);
        var tempoNodes = trackElem.Elements("TEMPO").ToList();

        Assert.Single(tempoNodes);
        Assert.Equal("0.000", tempoNodes[0].Attribute("Inizio")?.Value);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
