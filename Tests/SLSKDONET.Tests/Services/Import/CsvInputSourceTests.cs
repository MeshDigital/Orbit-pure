using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>
/// Tests for CSV column auto-detection and track parsing.
/// All tests use temp files to avoid filesystem side-effects.
/// </summary>
public class CsvInputSourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_CSV_{Guid.NewGuid():N}");

    public CsvInputSourceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteCsv(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }

    private static CsvInputSource CreateSource() => new();

    // ── Basic parsing ──────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_StandardArtistTitleColumns_ParsesCorrectly()
    {
        var path = WriteCsv("""
            artist,title
            Bicep,Glue
            Aphex Twin,Windowlicker
            Four Tet,Sing
            """);

        var source = CreateSource();
        var tracks = await source.ParseAsync(path);

        Assert.Equal(3, tracks.Count);
        Assert.Equal("Bicep", tracks[0].Artist);
        Assert.Equal("Glue", tracks[0].Title);
        Assert.Equal("Aphex Twin", tracks[1].Artist);
    }

    [Fact]
    public async Task ParseAsync_AlternativeColumnNames_ParsesCorrectly()
    {
        // "song" is an alias for title, "track" also works
        var path = WriteCsv("""
            Artist,Song
            Bicep,Glue
            Four Tet,Sing
            """);

        var source = CreateSource();
        var tracks = await source.ParseAsync(path);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("Glue", tracks[0].Title);
    }

    [Fact]
    public async Task ParseAsync_SkipsEmptyRows()
    {
        var path = WriteCsv("""
            artist,title
            Bicep,Glue
            ,
            Four Tet,Sing
            """);

        var source = CreateSource();
        var tracks = await source.ParseAsync(path);

        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public async Task ParseAsync_WithAlbumColumn_PopulatesAlbum()
    {
        var path = WriteCsv("""
            artist,title,album
            Bicep,Glue,Bicep
            """);

        var source = CreateSource();
        var tracks = await source.ParseAsync(path);

        Assert.Single(tracks);
        Assert.Equal("Bicep", tracks[0].Album);
    }

    [Fact]
    public async Task ParseAsync_TitleOnlyRow_SetsAlbumDownloadMode()
    {
        var path = WriteCsv("""
            artist,album
            Bicep,Bicep
            """);

        var source = CreateSource();
        var tracks = await source.ParseAsync(path);

        Assert.Single(tracks);
        Assert.Equal(DownloadMode.Album, tracks[0].DownloadMode);
    }

    [Fact]
    public async Task ParseAsync_NonExistentFile_ThrowsOrReturnsEmpty()
    {
        var source = CreateSource();

        // Should either throw FileNotFoundException or return empty — both are acceptable defensive behaviour
        try
        {
            var tracks = await source.ParseAsync("/nonexistent/path/file.csv");
            Assert.Empty(tracks);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or DirectoryNotFoundException)
        {
            // expected
        }
    }

    [Fact]
    public async Task ParseAsync_LargeFile_ReturnsAllTracks()
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("artist,title");
        for (int i = 0; i < 500; i++)
            lines.AppendLine($"Artist {i},Track {i}");

        var path = WriteCsv(lines.ToString());
        var source = CreateSource();
        var tracks = await source.ParseAsync(path);

        Assert.Equal(500, tracks.Count);
    }

    [Fact]
    public async Task ParseAsync_QuotedFieldsWithCommas_ParsesCorrectly()
    {
        var path = WriteCsv("""
            artist,title
            "Boards of Canada","Music Has the Right to Children, Vol. 1"
            """);

        var source = CreateSource();
        var tracks = await source.ParseAsync(path);

        Assert.Single(tracks);
        Assert.Equal("Music Has the Right to Children, Vol. 1", tracks[0].Title);
    }
}
