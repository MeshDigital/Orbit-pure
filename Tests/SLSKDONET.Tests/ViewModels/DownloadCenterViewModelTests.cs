using SLSKDONET.Models;
using SLSKDONET.ViewModels.Downloads;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class DownloadCenterViewModelTests
{
    [Theory]
    [InlineData("All")]
    [InlineData("")]
    public void BuildSessionFilterBanner_ReturnsNull_WhenFilterIsInactive(string mode)
    {
        var banner = DownloadCenterViewModel.BuildSessionFilterBanner(mode, 12);

        Assert.Null(banner);
    }

    [Fact]
    public void BuildSessionFilterBanner_DescribesActiveFilter()
    {
        var banner = DownloadCenterViewModel.BuildSessionFilterBanner("Queued", 3);

        Assert.NotNull(banner);
        Assert.Contains("Queued", banner);
        Assert.Contains("3 visible", banner);
    }

    [Fact]
    public void ShouldAutoDismissGlobalStatus_TrueForProfileMessages()
    {
        var evt = new GlobalStatusEvent("Download profile overwrite applied: Strict", true, false);

        Assert.True(DownloadCenterViewModel.ShouldAutoDismissGlobalStatus(evt));
    }

    [Fact]
    public void ShouldAutoDismissGlobalStatus_FalseForErrors()
    {
        var evt = new GlobalStatusEvent("Disconnected: Waiting for Soulseek...", true, true);

        Assert.False(DownloadCenterViewModel.ShouldAutoDismissGlobalStatus(evt));
    }
}
