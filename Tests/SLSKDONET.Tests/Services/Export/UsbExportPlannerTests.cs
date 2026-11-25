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
}
