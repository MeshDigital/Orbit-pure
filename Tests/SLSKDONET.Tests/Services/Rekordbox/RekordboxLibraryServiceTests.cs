using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Models;
using SLSKDONET.Services.Rekordbox;
using Xunit;

namespace SLSKDONET.Tests.Services.Rekordbox;

public sealed class RekordboxLibraryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-rb-{Guid.NewGuid():N}");

    public RekordboxLibraryServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    // Verified on a real Rekordbox 7.2.14 library: every track with eight hot cues uses exactly
    // Kind {1,2,3,5,6,7,8,9}; 4 never occurs; 0 is a memory cue.
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(4, -1)]
    [InlineData(5, 3)]
    [InlineData(9, 7)]
    public void KindMapsToPad(int kind, int slot) => Assert.Equal(slot, RekordboxLibraryService.SlotForKind(kind));

    private const string BridgeJson = """
        [{"id":"1","path":"D:/Music/A.flac","title":"A","artist":"X","bpm":174.0,"cues":[
          {"kind":0,"inMsec":1200,"outMsec":0,"inFrame":180,"outFrame":0,"color":-1,"colorTableIndex":0,"comment":"","activeLoop":0,"beatLoopSize":0,"cueMicrosec":0},
          {"kind":5,"inMsec":64500,"outMsec":0,"inFrame":9675,"outFrame":0,"color":255,"colorTableIndex":22,"comment":"Drop","activeLoop":0,"beatLoopSize":0,"cueMicrosec":0},
          {"kind":0,"inMsec":200000,"outMsec":205517,"inFrame":30000,"outFrame":30827,"color":-1,"colorTableIndex":0,"comment":null,"activeLoop":0,"beatLoopSize":0,"cueMicrosec":0}]}]
        """;

    [Fact]
    public void Parse_ReadsTracksAndCues()
    {
        var result = RekordboxLibraryService.Parse(BridgeJson);

        var track = Assert.Single(result.Tracks);
        Assert.Equal("D:/Music/A.flac", track.Path);
        Assert.Equal(3, track.Cues.Count);
        Assert.Equal(3, track.Cues[1].Slot);
        Assert.True(track.Cues[2].IsLoop);
    }

    [Fact]
    public void Import_MapsPadsLoopsAndNames()
    {
        var cues = RekordboxCueImport.ToOrbitCues(RekordboxLibraryService.Parse(BridgeJson).Tracks[0]);

        Assert.Equal(("Rekordbox memory", -1, false), (cues[0].Name, cues[0].SlotIndex, cues[0].IsLoop));
        Assert.Equal(("Drop", 3, 64.5), (cues[1].Name, cues[1].SlotIndex, cues[1].Timestamp));
        Assert.True(cues[2].IsLoop);
        Assert.Equal(205.517, cues[2].LoopEndSeconds, 3);
        Assert.All(cues, c => Assert.Equal(CueSource.User, c.Source));
    }

    [Fact]
    public void Merge_RekordboxWins_OrbitCuesStayUnlessDuplicatedOrDisplaced()
    {
        var imported = RekordboxCueImport.ToOrbitCues(RekordboxLibraryService.Parse(BridgeJson).Tracks[0]);
        var current = new[]
        {
            new OrbitCue { Name = "[DROP 1]", Timestamp = 64.5, SlotIndex = 2, Source = CueSource.Auto, Role = CueRole.Drop },
            new OrbitCue { Name = "My vocal", Timestamp = 90, SlotIndex = 3, Source = CueSource.User },   // pad D taken by Rekordbox
            new OrbitCue { Name = "Same spot", Timestamp = 64.52, SlotIndex = 6, Source = CueSource.User }, // duplicate of Rekordbox's D
        };

        var merged = RekordboxCueImport.Merge(current, imported);

        Assert.DoesNotContain(merged, c => c.Name == "[DROP 1]");  // auto cues give way
        Assert.DoesNotContain(merged, c => c.Name == "Same spot");
        Assert.Equal(-1, merged.Single(c => c.Name == "My vocal").SlotIndex); // kept as a memory cue
        Assert.Equal(4, merged.Count);
    }

    [Fact]
    public void ColorTable_DefaultGreen_AndOrbitSwatchesRoundTrip()
    {
        Assert.Equal("#28E214", RekordboxCueColors.HexForIndex(22)); // Rekordbox's default hot cue colour
        Assert.Null(RekordboxCueColors.HexForIndex(0));
        Assert.Null(RekordboxCueColors.HexForIndex(63));

        // Every ORBIT cue swatch is an exact entry of Rekordbox's table (red 42 … pink 49).
        var expected = new[] { 42, 38, 30, 22, 18, 1, 60, 49 };
        var swatches = SLSKDONET.ViewModels.CueEditorSession.CueColorSwatches;
        for (int i = 0; i < swatches.Count; i++)
        {
            Assert.Equal(expected[i], RekordboxCueColors.IndexForHex(swatches[i]));
            Assert.Equal(swatches[i], RekordboxCueColors.HexForIndex(expected[i]));
        }
        Assert.Equal(0, RekordboxCueColors.IndexForHex("not a colour"));
    }

    [Fact]
    public void Import_UsesRekordboxsCueColour()
    {
        var cues = RekordboxCueImport.ToOrbitCues(RekordboxLibraryService.Parse(BridgeJson).Tracks[0]);

        Assert.Equal("#28E214", cues[1].Color); // index 22 on the hot cue
        Assert.Equal("#28E214", cues[0].Color); // memory cue without a colour: default green
    }

    [Fact]
    public void PathsMatchAcrossSlashesAndCase() =>
        Assert.Equal(RekordboxLibraryService.NormalizePath(@"D:\Music\A.FLAC"), RekordboxLibraryService.NormalizePath("d:/music/a.flac"));

    private static string? BuiltBridge()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj"))) dir = dir.Parent;
        if (dir == null) return null;
        var exe = Path.Combine(dir.FullName, "bin", "Debug", "net9.0", "Tools", "RekordboxBridge", "RekordboxBridge.exe");
        return File.Exists(exe) ? exe : null;
    }

    [Fact]
    public async Task Bridge_ReportsAMissingDatabase()
    {
        if (BuiltBridge() is not { } bridge) return; // only where the companion was built (Windows dev/CI)
        var service = new RekordboxLibraryService(NullLogger<RekordboxLibraryService>.Instance)
        {
            BridgePath = bridge,
            DatabasePathOverride = Path.Combine(_dir, "gone", "master.db"),
        };
        Directory.CreateDirectory(Path.Combine(_dir, "gone"));
        File.WriteAllText(Path.Combine(_dir, "gone", "master.db"), "");
        File.Delete(Path.Combine(_dir, "gone", "master.db"));

        var result = await service.ReadCuesAsync();

        Assert.False(result.Success);
        Assert.Contains("wasn't found", result.Message);
    }

    [Fact]
    public async Task Bridge_ReportsADatabaseItCantUnlock()
    {
        if (BuiltBridge() is not { } bridge) return;
        var db = Path.Combine(_dir, "master.db");
        File.WriteAllBytes(db, new byte[4096]); // not a Rekordbox library

        var service = new RekordboxLibraryService(NullLogger<RekordboxLibraryService>.Instance) { BridgePath = bridge, DatabasePathOverride = db };
        var result = await service.ReadCuesAsync();

        Assert.False(result.Success);
        Assert.Contains("unlock", result.Message);
    }
}
