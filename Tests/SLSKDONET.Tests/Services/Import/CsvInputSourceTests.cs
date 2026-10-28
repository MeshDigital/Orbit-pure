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

    // ── Real-world exports and validation ──────────────────────────────────

    [Fact]
    public void Inspect_ExportifyExport_MapsTrackAndArtistNames()
    {
        // Before: only "Album Name" was recognised, so every row became an empty album-download row.
        var path = WriteCsv("""
            "Track URI","Track Name","Artist URI(s)","Artist Name(s)","Album Name","Album Artist Name(s)","Track Duration (ms)","ISRC"
            "spotify:track:4cOdK2wGLETKBW3PvgPWqT","One More Time","spotify:artist:x","Daft Punk","Discovery","Daft Punk","320357","GBDUW0000059"
            "spotify:track:0ZzPDzI5Rf3W6nOXr4u4wq","Glue","spotify:artist:y","Bicep","Bicep","Bicep","269000","GBCFB1700337"
            """);

        var inspection = CreateSource().Inspect(path);

        Assert.True(inspection.IsValid);
        Assert.Equal("Track Name", inspection.Mapping[CsvField.Title]);
        Assert.Equal("Artist Name(s)", inspection.Mapping[CsvField.Artist]);
        var first = inspection.Tracks[0];
        Assert.Equal(("Daft Punk", "One More Time", "Discovery"), (first.Artist, first.Title, first.Album));
        Assert.Equal(DownloadMode.Normal, first.DownloadMode);
        Assert.Equal(320, first.Length);
        Assert.Equal("GBDUW0000059", first.ISRC);
        Assert.Equal("4cOdK2wGLETKBW3PvgPWqT", first.SpotifyTrackId);
    }

    [Fact]
    public void Inspect_DutchSemicolonExcelFileInWindows1252_ReadsHeadersAccentsAndDurations()
    {
        var path = Path.Combine(_tempDir, "excel.csv");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        File.WriteAllText(path, "Artiest;Song;Duur\r\nAphex Twin;Xtal;4:54\r\nRöyksopp;Eple;220\r\n",
            System.Text.Encoding.GetEncoding(1252));

        var inspection = CreateSource().Inspect(path);

        Assert.True(inspection.IsValid);
        Assert.Equal(';', inspection.Delimiter);
        Assert.Equal(("Röyksopp", "Eple", 220), (inspection.Tracks[1].Artist, inspection.Tracks[1].Title, inspection.Tracks[1].Length));
        Assert.Equal(294, inspection.Tracks[0].Length);
        Assert.Empty(inspection.Warnings);
    }

    [Fact]
    public void Inspect_NoTitleColumn_IsRefusedWithTheColumnsFound()
    {
        var path = WriteCsv("""
            Foo,Bar
            a,b
            """);

        var inspection = CreateSource().Inspect(path);

        Assert.False(inspection.IsValid);
        Assert.Empty(inspection.Tracks);
        Assert.Contains("no title column", inspection.Errors[0]);
        Assert.Contains("\"Foo\", \"Bar\"", inspection.Errors[0]);
    }

    [Fact]
    public async Task ParseAsync_NoTitleColumn_Throws()
    {
        var path = WriteCsv("""
            Foo,Bar
            a,b
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSource().ParseAsync(path));
    }

    [Fact]
    public void Inspect_RowsWithoutTitleAndDuplicates_AreSkippedAndReported()
    {
        var path = WriteCsv("""
            Artist,Title
            Bicep,Glue
            Bicep,
            Bicep,Glue
            Four Tet,Sing
            """);

        var inspection = CreateSource().Inspect(path);

        Assert.Equal(2, inspection.Tracks.Count);
        Assert.Contains("Skipped 1 row without a title (line 3).", inspection.Warnings);
        Assert.Contains("Skipped 1 row listed twice.", inspection.Warnings);
        Assert.Contains("Title ← \"Title\"", inspection.Summary);
    }

    [Fact]
    public void Inspect_PlaylistColumn_BecomesTheSourceTitle()
    {
        var path = WriteCsv("""
            Track name,Artist name,Album,Playlist name
            Glue,Bicep,Bicep,Sunday Set
            Sing,Four Tet,There Is Love In You,Sunday Set
            """);

        Assert.Equal("Sunday Set", CreateSource().Inspect(path).PlaylistName);
    }

    [Fact]
    public void Template_IsAValidImport()
    {
        var path = WriteCsv(CsvInputSource.TemplateCsv);

        var inspection = CreateSource().Inspect(path);

        Assert.True(inspection.IsValid);
        Assert.Empty(inspection.Warnings);
        Assert.Equal(3, inspection.Tracks.Count);
        Assert.Equal(320, inspection.Tracks[0].Length);
    }

    [Theory]
    [InlineData("3:45", false, 225)]
    [InlineData("1:02:45", false, 3765)]
    [InlineData("225.4", false, 225)]
    [InlineData("320357", false, 320)]
    [InlineData("320357", true, 320)]
    public void TryParseDuration_HandlesCommonFormats(string value, bool ms, int expected)
    {
        Assert.True(CsvInputSource.TryParseDuration(value, ms, out var seconds));
        Assert.Equal(expected, seconds);
    }
}
