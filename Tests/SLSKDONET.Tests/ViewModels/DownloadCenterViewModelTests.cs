using SLSKDONET.Models;
using SLSKDONET.ViewModels.Downloads;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class DownloadCenterViewModelTests
{
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

    [Fact]
    public void BuildIngestionDisplayName_UsesFileNameWhenPathProvided()
    {
        var value = DownloadCenterViewModel.BuildIngestionDisplayName(@"C:\\music\\artist\\track.flac", "hash123");

        Assert.Equal("track.flac", value);
    }

    [Fact]
    public void BuildIngestionDisplayName_FallsBackToHash()
    {
        var value = DownloadCenterViewModel.BuildIngestionDisplayName(null, "hash123");

        Assert.Equal("hash123", value);
    }
}
