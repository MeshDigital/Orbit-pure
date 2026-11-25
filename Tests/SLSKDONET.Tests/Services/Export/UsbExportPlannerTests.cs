using System.IO;
using SLSKDONET.Models;
using SLSKDONET.Services.Export;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>LF7 (#349): the export planner dedupes, computes FAT32-safe paths and sums sizes.</summary>
public class UsbExportPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-export-plan-{Guid.NewGuid():N}");

    public UsbExportPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static Track MakeTrack(string key, double? bpm, string? genre, string filename) =>
        new() { MusicalKey = key, BPM = bpm, Genres = genre, Filename = filename, Title = filename };

    private string MakeFile(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Plan_ByKey_DedupesAndComputesRelativePathsAndSizes()
    {
        var a = MakeFile("a.flac", "abcd");
        var b = MakeFile("b.flac", "abcdefgh");

        var plan = UsbExportPlanner.Plan(new[]
        {
            new UsbExportTrack("h1", a, MakeTrack("8A", 128, null, "a.flac")),
            new UsbExportTrack("h1", a, MakeTrack("8A", 128, null, "a.flac")), // duplicate hash → deduped
            new UsbExportTrack("h2", b, MakeTrack("10B", 140, null, "b.flac")),
        }, UsbLayoutMode.ByKey);

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(Path.Combine("8A", "a.flac"), plan.Items[0].RelativeTargetPath);
        Assert.Equal(Path.Combine("10B", "b.flac"), plan.Items[1].RelativeTargetPath);
        Assert.Equal(12, plan.TotalBytes); // 4 + 8
    }

    [Fact]
    public void Plan_ByPlaylist_UsesThePlaylistNameResolver()
    {
        var a = MakeFile("a.flac", "x");
        var plan = UsbExportPlanner.Plan(new[]
        {
            new UsbExportTrack("h1", a, MakeTrack(null, null, null, "a.flac")),
        }, UsbLayoutMode.ByPlaylist, _ => "My Set");

        Assert.Equal(Path.Combine("My Set", "a.flac"), Assert.Single(plan.Items).RelativeTargetPath);
    }

    [Fact]
    public void Plan_UnknownBpm_FallsBackToUnknownFolder()
    {
        var a = MakeFile("a.flac", "x");
        var plan = UsbExportPlanner.Plan(new[]
        {
            new UsbExportTrack("h1", a, MakeTrack(null, null, null, "a.flac")),
        }, UsbLayoutMode.ByBpmBracket);

        Assert.Equal(Path.Combine("Unknown", "a.flac"), Assert.Single(plan.Items).RelativeTargetPath);
    }

    [Fact]
    public void Plan_SameFilenameDifferentHashesInSameFolder_DisambiguatesTargets()
    {
        var a = MakeFile("dir1/same.flac", "abcd");
        var b = MakeFile("dir2/same.flac", "efgh");

        var plan = UsbExportPlanner.Plan(new[]
        {
            new UsbExportTrack("hash1111aaaa", a, MakeTrack("8A", 128, null, "same.flac")),
            new UsbExportTrack("hash2222bbbb", b, MakeTrack("8A", 128, null, "same.flac")),
        }, UsbLayoutMode.ByKey);

        Assert.Equal(2, plan.Items.Count);
        Assert.NotEqual(plan.Items[0].RelativeTargetPath, plan.Items[1].RelativeTargetPath);
        Assert.Equal(Path.Combine("8A", "same.flac"), plan.Items[0].RelativeTargetPath);
        Assert.Equal(Path.Combine("8A", "same [hash2222].flac"), plan.Items[1].RelativeTargetPath);
    }

    [Fact]
    public void LayoutDestPath_SanitizesFat32AndIncludesHashDisambiguator()
    {
        var t1 = new PlaylistTrack
        {
            Artist = "AC/DC: Live?",
            Title = "Track*1",
            MusicalKey = "8A",
            ResolvedFilePath = "song.flac",
            TrackUniqueHash = "deadbeef1234",
        };
        var t2 = new PlaylistTrack
        {
            Artist = "AC/DC: Live?",
            Title = "Track*1",
            MusicalKey = "8A",
            ResolvedFilePath = "song.flac",
            TrackUniqueHash = "cafebabe5678",
        };

        var p1 = UsbExportOrchestrator.LayoutDestPath("usb", t1, UsbLayoutMode.ByKey, "Set");
        var p2 = UsbExportOrchestrator.LayoutDestPath("usb", t2, UsbLayoutMode.ByKey, "Set");

        Assert.NotEqual(p1, p2);
        Assert.DoesNotContain(":", Path.GetFileName(p1));
        Assert.DoesNotContain("?", Path.GetFileName(p1));
        Assert.DoesNotContain("*", Path.GetFileName(p1));
        Assert.Contains("[deadbeef]", Path.GetFileName(p1));
        Assert.Contains("[cafebabe]", Path.GetFileName(p2));
    }

    [Fact]
    public void BuildPreviewSummary_GroupsFoldersAndSumsSizes()
    {
        var a = MakeFile("a.flac", new string('x', 2048));
        var b = MakeFile("b.flac", new string('y', 1024));
        var tracks = new[]
        {
            new PlaylistTrack { TrackUniqueHash = "h1", Artist = "A", Title = "One", MusicalKey = "8A", ResolvedFilePath = a },
            new PlaylistTrack { TrackUniqueHash = "h2", Artist = "B", Title = "Two", MusicalKey = "10B", ResolvedFilePath = b },
        };

        var byKey = UsbExportPlanner.BuildPreviewSummary("Warmup", tracks, UsbLayoutMode.ByKey);
        Assert.Equal(2, byKey.TrackCount);
        Assert.Equal(2, byKey.FolderCount);
        Assert.Equal(3072, byKey.TotalBytes);
        Assert.Equal("3 KB", byKey.TotalSizeDisplay);
        Assert.Contains(byKey.FolderLines, l => l.StartsWith("8A/"));
        Assert.Contains(byKey.FolderLines, l => l.StartsWith("10B/"));

        var flat = UsbExportPlanner.BuildPreviewSummary("Warmup: Live?", tracks, null);
        Assert.Equal(2, flat.TrackCount);
        Assert.Equal(1, flat.FolderCount);
        Assert.Equal(3072, flat.TotalBytes);
        Assert.Contains("OrbitAudio/Warmup_ Live_/", Assert.Single(flat.FolderLines));
    }
}
