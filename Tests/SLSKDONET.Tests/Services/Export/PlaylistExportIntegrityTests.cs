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
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>End-to-end guards against silent data loss at the Rekordbox trust boundary.</summary>
public class PlaylistExportIntegrityTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ExportIntegrity_{Guid.NewGuid():N}");

    public PlaylistExportIntegrityTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task RepeatedFile_CollectionIsDeduplicatedButPlaylistPositionsArePreserved()
    {
        var options = CreateOptions();
        var fileA = CreateAudioFile("a.mp3");
        var fileB = CreateAudioFile("b.mp3");
        var trackA = Track(fileA, "A", "hash-a");
        var trackB = Track(fileB, "B", "hash-b");
        var repeatedA = Track(fileA, "A (repeat)", "hash-a");
        var tracks = new[] { trackA, trackB, repeatedA };
        var output = OutputPath();
        var service = CreateService(options);

        var preflight = await service.PreflightRekordboxExportAsync(tracks);
        Assert.True(preflight.CanExport);
        Assert.Equal(1, preflight.RepeatedTrackEntryCount);
        Assert.Contains(preflight.Warnings, warning => warning.Contains("playlist position"));

        await service.ExportToRekordboxXmlAsync("Repeated", tracks, output);

        var doc = XDocument.Load(output);
        var collection = doc.Descendants("TRACK").Where(element => element.Attribute("Location") != null).ToList();
        Assert.Equal(2, collection.Count);
        var idA = collection.Single(element => element.Attribute("Name")?.Value == "A").Attribute("TrackID")!.Value;
        var idB = collection.Single(element => element.Attribute("Name")?.Value == "B").Attribute("TrackID")!.Value;
        var playlist = PlaylistNode(doc);
        Assert.Equal("3", playlist.Attribute("Entries")?.Value);
        Assert.Equal(new[] { idA, idB, idA }, playlist.Elements("TRACK").Select(element => element.Attribute("Key")?.Value));
    }

    [Fact]
    public async Task NonMonotonicBeatGrid_IsAHardBlocker()
    {
        var options = CreateOptions();
        const string hash = "bad-grid";
        await SeedAudioFeaturesAsync(options, new AudioFeaturesEntity
        {
            TrackUniqueHash = hash,
            BeatGridJson = "[0.4,0.3,0.8]",
            DownbeatOffsetSeconds = 0.4,
            TrackDuration = 30,
        });
        var track = Track(CreateAudioFile("bad-grid.mp3"), "Bad Grid", hash, bpm: 128, durationMs: 30000);
        var service = CreateService(options);

        var result = await service.PreflightRekordboxExportAsync(new[] { track });

        Assert.False(result.CanExport);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("non-increasing"));
        await Assert.ThrowsAsync<SLSKDONET.Services.Export.RekordboxExportIntegrityException>(() =>
            service.ExportToRekordboxXmlAsync("Blocked", new[] { track }, OutputPath()));
    }

    [Fact]
    public async Task LoopEndingBeforeItsStart_IsAHardBlocker()
    {
        var options = CreateOptions();
        var track = Track(CreateAudioFile("bad-loop.mp3"), "Bad Loop", "bad-loop", durationMs: 30000);
        track.CuePointsJson = JsonSerializer.Serialize(new[]
        {
            new OrbitCue { Timestamp = 10, LoopEndSeconds = 9, IsLoop = true, Name = "Broken", SlotIndex = -1 },
        });
        var service = CreateService(options);

        var result = await service.PreflightRekordboxExportAsync(new[] { track });

        Assert.False(result.CanExport);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("non-positive loop range"));
    }

    [Fact]
    public async Task TwoCuesClaimingTheSameHotPad_IsAHardBlocker()
    {
        var options = CreateOptions();
        var track = Track(CreateAudioFile("pad-collision.mp3"), "Pad Collision", "pad-collision");
        track.CuePointsJson = JsonSerializer.Serialize(new[]
        {
            new OrbitCue { Timestamp = 5, Name = "First", SlotIndex = 2 },
            new OrbitCue { Timestamp = 10, Name = "Second", SlotIndex = 2 },
        });
        var service = CreateService(options);

        var result = await service.PreflightRekordboxExportAsync(new[] { track });

        Assert.False(result.CanExport);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("pad 3") && blocker.Contains("collision"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-120)]
    [InlineData(1000)]
    public async Task InvalidTrackBpmWithoutDetailedGrid_IsAHardBlocker(double bpm)
    {
        var options = CreateOptions();
        var track = Track(CreateAudioFile($"invalid-bpm-{bpm}.mp3"), "Invalid BPM", $"invalid-bpm-{bpm}", bpm: bpm);
        var service = CreateService(options);

        var result = await service.PreflightRekordboxExportAsync(new[] { track });

        Assert.False(result.CanExport);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("saved BPM") && blocker.Contains("invalid"));
        await Assert.ThrowsAsync<SLSKDONET.Services.Export.RekordboxExportIntegrityException>(() =>
            service.ExportToRekordboxXmlAsync("Blocked", new[] { track }, OutputPath()));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(8)]
    public async Task OutOfRangeHotCuePad_IsAHardBlocker(int slotIndex)
    {
        var options = CreateOptions();
        var track = Track(CreateAudioFile($"invalid-pad-{slotIndex}.mp3"), "Invalid Pad", $"invalid-pad-{slotIndex}");
        track.CuePointsJson = JsonSerializer.Serialize(new[]
        {
            new OrbitCue { Timestamp = 5, Name = "Broken pad", SlotIndex = slotIndex },
        });
        var service = CreateService(options);

        var result = await service.PreflightRekordboxExportAsync(new[] { track });

        Assert.False(result.CanExport);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("Broken pad") && blocker.Contains($"index {slotIndex}"));
    }

    [Fact]
    public async Task MoreThanThirtyTwoSavedCues_AreNotTruncated()
    {
        var options = CreateOptions();
        var track = Track(CreateAudioFile("many-cues.mp3"), "Many Cues", "many-cues");
        var cues = Enumerable.Range(0, 40)
            .Select(index => new OrbitCue
            {
                Timestamp = index + 1,
                Name = $"Cue {index:00}",
                SlotIndex = -1,
            })
            .ToList();
        track.CuePointsJson = JsonSerializer.Serialize(cues);
        var output = OutputPath();

        await CreateService(options).ExportToRekordboxXmlAsync("Many", new[] { track }, output);

        var names = XDocument.Load(output).Descendants("POSITION_MARK")
            .Select(mark => mark.Attribute("Name")?.Value)
            .Where(name => name?.StartsWith("Cue ", StringComparison.Ordinal) == true)
            .Distinct()
            .ToList();
        Assert.Equal(40, names.Count);
        Assert.Contains("Cue 39", names);
    }

    [Fact]
    public async Task UnicodeTitleAndFilename_SurviveXmlAndLocationRoundTrip()
    {
        var options = CreateOptions();
        var file = CreateAudioFile("日本語 🎛️ Кириллица & #1.mp3");
        var track = Track(file, "夜の Mix 🎧", "unicode");
        track.Artist = "Артист";
        var output = OutputPath();

        await CreateService(options).ExportToRekordboxXmlAsync("Unicode", new[] { track }, output);

        var element = XDocument.Load(output).Descendants("TRACK").Single(trackElement => trackElement.Attribute("Location") != null);
        Assert.Equal("夜の Mix 🎧", element.Attribute("Name")?.Value);
        Assert.Equal("Артист", element.Attribute("Artist")?.Value);
        var location = element.Attribute("Location")!.Value;
        Assert.Contains("%23", location);
        var parsed = new Uri(location).LocalPath;
        const string localhostPrefix = @"\\localhost\";
        if (parsed.StartsWith(localhostPrefix, StringComparison.OrdinalIgnoreCase))
            parsed = parsed[localhostPrefix.Length..];
        Assert.Equal(file.Replace('\\', '/'), parsed.Replace('\\', '/'), ignoreCase: true);
    }

    [Fact]
    public async Task VeryShortTrack_WithHalfSecondCueAndGrid_ExportsWithoutClipping()
    {
        var options = CreateOptions();
        const string hash = "short-track";
        await SeedAudioFeaturesAsync(options, new AudioFeaturesEntity
        {
            TrackUniqueHash = hash,
            BeatGridJson = "[0.1,0.6]",
            DownbeatOffsetSeconds = 0.1,
            TrackDuration = 1,
            BpmStability = 0.9f,
        });
        var track = Track(CreateAudioFile("short.mp3"), "Short", hash, bpm: 128, durationMs: 1000);
        track.CuePointsJson = JsonSerializer.Serialize(new[]
        {
            new OrbitCue { Timestamp = 0.5, Name = "Immediate cue", SlotIndex = -1 },
        });
        var output = OutputPath();
        var service = CreateService(options);

        var preflight = await service.PreflightRekordboxExportAsync(new[] { track });
        Assert.True(preflight.CanExport, string.Join(Environment.NewLine, preflight.Blockers));
        await service.ExportToRekordboxXmlAsync("Short", new[] { track }, output);

        var element = XDocument.Load(output).Descendants("TRACK").Single(trackElement => trackElement.Attribute("Location") != null);
        Assert.Equal("0.100", element.Element("TEMPO")?.Attribute("Inizio")?.Value);
        Assert.Contains(element.Elements("POSITION_MARK"), mark => mark.Attribute("Start")?.Value == "0.500");
    }

    [Fact]
    public async Task VariableBpmGrid_PassesIntegrityAndWritesMultipleAnchors()
    {
        var options = CreateOptions();
        const string hash = "variable-grid";
        var beats = BuildVariableGrid();
        await SeedAudioFeaturesAsync(options, new AudioFeaturesEntity
        {
            TrackUniqueHash = hash,
            BeatGridJson = JsonSerializer.Serialize(beats),
            DownbeatOffsetSeconds = beats[0],
            TrackDuration = beats[^1] + 2,
            BpmStability = 0.3f,
        });
        var track = Track(CreateAudioFile("variable.mp3"), "Variable", hash, bpm: 128, durationMs: (int)((beats[^1] + 2) * 1000));
        var output = OutputPath();
        var service = CreateService(options);

        var preflight = await service.PreflightRekordboxExportAsync(new[] { track });
        Assert.True(preflight.CanExport, string.Join(Environment.NewLine, preflight.Blockers));
        await service.ExportToRekordboxXmlAsync("Variable", new[] { track }, output);

        var tempos = XDocument.Load(output).Descendants("TEMPO").ToList();
        Assert.True(tempos.Count >= 2, $"Expected multiple anchors, got {tempos.Count}.");
    }

    [Fact]
    public async Task SameTitleAndTrackNumber_DifferentFilesRemainDistinct()
    {
        var options = CreateOptions();
        var first = Track(CreateAudioFile("disc-one.mp3"), "Shared Title", "disc-one");
        var second = Track(CreateAudioFile("disc-two.mp3"), "Shared Title", "disc-two");
        first.TrackNumber = 1;
        second.TrackNumber = 1;
        var output = OutputPath();

        await CreateService(options).ExportToRekordboxXmlAsync("Compilation", new[] { first, second }, output);

        var elements = XDocument.Load(output).Descendants("TRACK").Where(element => element.Attribute("Location") != null).ToList();
        Assert.Equal(2, elements.Count);
        Assert.NotEqual(elements[0].Attribute("TrackID")?.Value, elements[1].Attribute("TrackID")?.Value);
    }

    private static List<double> BuildVariableGrid()
    {
        var beats = new List<double>();
        var timestamp = 0.417;
        foreach (var (count, bpm) in new[] { (60, 128.0), (60, 132.0) })
        {
            for (var index = 0; index < count; index++)
            {
                beats.Add(timestamp);
                timestamp += 60.0 / bpm;
            }
        }
        return beats;
    }

    private string CreateAudioFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "fake audio");
        return path;
    }

    private string OutputPath() => Path.Combine(_tempDir, $"out-{Guid.NewGuid():N}.xml");

    private static PlaylistTrack Track(string path, string title, string hash, double? bpm = null, int? durationMs = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Artist = "Artist",
            ResolvedFilePath = path,
            TrackUniqueHash = hash,
            BPM = bpm,
            CanonicalDuration = durationMs,
        };

    private static XElement PlaylistNode(XDocument doc) =>
        doc.Descendants("NODE").Single(node => node.Attribute("Type")?.Value == "1");

    private static DbContextOptions<AppDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ExportIntegrity_{Guid.NewGuid():N}")
            .Options;

    private static PlaylistExportService CreateService(DbContextOptions<AppDbContext> options) =>
        new(NullLogger<PlaylistExportService>.Instance, new Factory(options), new Helpers.FakeFileWriteService());

    private static async Task SeedAudioFeaturesAsync(DbContextOptions<AppDbContext> options, AudioFeaturesEntity features)
    {
        await using var db = new AppDbContext(options);
        db.AudioFeatures.Add(features);
        await db.SaveChangesAsync();
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public Factory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
