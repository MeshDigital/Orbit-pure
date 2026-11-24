using System;
using System.IO;
using SLSKDONET.Services.Downloads;
using Xunit;

namespace SLSKDONET.Tests.Services.Downloads;

/// <summary>LF3 (#345): the staging path convention for in-flight .part files.</summary>
public class DownloadStagingPathTests
{
    [Fact]
    public void GetStagingDirectory_IsHiddenFolderUnderDownloadDir()
    {
        var dir = DownloadStagingPath.GetStagingDirectory(Path.Combine("C:", "music"));

        Assert.Equal(Path.Combine("C:", "music", ".orbit-staging"), dir);
    }

    [Fact]
    public void GetPartPath_IsHashKeyedInsideStaging()
    {
        var part = DownloadStagingPath.GetPartPath(Path.Combine("C:", "music"), "abc123");

        Assert.Equal(Path.Combine("C:", "music", ".orbit-staging", "abc123.part"), part);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetStagingDirectory_BlankDownloadDir_Throws(string? dir)
    {
        Assert.Throws<ArgumentException>(() => DownloadStagingPath.GetStagingDirectory(dir!));
    }
}
