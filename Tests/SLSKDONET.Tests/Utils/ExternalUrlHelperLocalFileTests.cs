using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Utils;

/// <summary>#332: a CSV import's file:/// source is recognised as a local file; other schemes are still refused.</summary>
public class ExternalUrlHelperLocalFileTests
{
    [Fact]
    public void FileUris_MapToTheirLocalPath()
    {
        var path = ExternalUrlHelper.LocalFilePath("file:///C:/Users/dj/Downloads/tracklist.csv");
        Assert.NotNull(path);
        Assert.EndsWith("tracklist.csv", path);
    }

    [Theory]
    [InlineData("https://www.1001tracklists.com/tracklist/abc")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_IsNotALocalFile(string? url) => Assert.Null(ExternalUrlHelper.LocalFilePath(url));

    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("ms-settings:privacy")]
    public void TryOpen_StillRefusesNonHttp(string url) => Assert.False(ExternalUrlHelper.TryOpen(url));
}
