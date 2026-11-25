using SLSKDONET.Models;
using SLSKDONET.Services.Export;
using Xunit;

namespace SLSKDONET.Tests.Services.Export;

/// <summary>LF7 (#349): USB export layouts produce FAT32-safe folders.</summary>
public class UsbExportLayoutTests
{
    private static Track MakeTrack(string? key, double? bpm, string? genre) =>
        new() { MusicalKey = key, BPM = bpm, Genres = genre };

    [Fact]
    public void GetFolder_ByKey_UsesTheCamelotKey()
    {
        Assert.Equal("8A", UsbExportLayout.GetFolder(MakeTrack("8A", 128, null), UsbLayoutMode.ByKey));
    }

    [Fact]
    public void GetFolder_ByBpmBracket_BracketsTheTempo()
    {
        Assert.Equal("120-129", UsbExportLayout.GetFolder(MakeTrack(null, 128.5, null), UsbLayoutMode.ByBpmBracket));
        Assert.Equal("170-179", UsbExportLayout.GetFolder(MakeTrack(null, 174, null), UsbLayoutMode.ByBpmBracket));
        Assert.Equal("Unknown", UsbExportLayout.GetFolder(MakeTrack(null, null, null), UsbLayoutMode.ByBpmBracket));
    }

    [Fact]
    public void GetFolder_ByGenre_UsesTheGenre()
    {
        Assert.Equal("Drum & Bass", UsbExportLayout.GetFolder(MakeTrack(null, null, "Drum & Bass"), UsbLayoutMode.ByGenre));
    }

    [Fact]
    public void GetFolder_ByPlaylist_UsesThePlaylistName()
    {
        Assert.Equal("My Set", UsbExportLayout.GetFolder(MakeTrack(null, null, null), UsbLayoutMode.ByPlaylist, "My Set"));
    }

    [Theory]
    [InlineData("A:B*C?D\"E<F>G|H/I", "A_B_C_D_E_F_G_H_I")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("trailing.", "trailing")]
    [InlineData("", "Unknown")]
    [InlineData("   ", "Unknown")]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("COM1", "_COM1")]
    public void SafeFolder_StripsFat32InvalidAndFallsBack(string input, string expected)
    {
        Assert.Equal(expected, UsbExportLayout.SafeFolder(input));
    }

    [Fact]
    public void SafeFolder_CapsLength()
    {
        var longName = new string('a', 200);
        Assert.Equal(120, UsbExportLayout.SafeFolder(longName).Length);
    }
}
