using System.IO;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class FileOriginDeriverTests
{
    private static readonly string Temp = Path.GetTempPath();
    private static readonly string DownloadDir = Path.Combine(Temp, "orbit-fo-downloads");
    private static readonly string LibraryRoot = Path.Combine(Temp, "orbit-fo-linked");

    [Fact]
    public void Derive_UnderDownloadDirectory_ReturnsDownloaded()
    {
        var file = Path.Combine(DownloadDir, "Artist", "track.flac");

        Assert.Equal(FileOriginDeriver.Downloaded, FileOriginDeriver.Derive(file, new[] { LibraryRoot }, DownloadDir));
    }

    [Fact]
    public void Derive_UnderLibraryFolder_ReturnsLinkedFolder()
    {
        var file = Path.Combine(LibraryRoot, "Artist", "track.flac");

        Assert.Equal(FileOriginDeriver.LinkedFolder, FileOriginDeriver.Derive(file, new[] { LibraryRoot }, DownloadDir));
    }

    [Fact]
    public void Derive_Elsewhere_ReturnsImported()
    {
        var file = Path.Combine(Temp, "somewhere-else", "track.flac");

        Assert.Equal(FileOriginDeriver.Imported, FileOriginDeriver.Derive(file, new[] { LibraryRoot }, DownloadDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Derive_NullOrBlank_ReturnsImported(string? file)
    {
        Assert.Equal(FileOriginDeriver.Imported, FileOriginDeriver.Derive(file, new[] { LibraryRoot }, DownloadDir));
    }

    [Fact]
    public void Derive_DownloadDirectoryWins_WhenNestedInsideALibraryFolder()
    {
        // A watched library folder that itself contains the download dir: the download dir must win.
        var nested = Path.Combine(LibraryRoot, "orbit-fo-downloads", "track.flac");
        var downloadInsideLibrary = Path.Combine(LibraryRoot, "orbit-fo-downloads");

        Assert.Equal(FileOriginDeriver.Downloaded, FileOriginDeriver.Derive(nested, new[] { LibraryRoot }, downloadInsideLibrary));
    }
}
