using System;
using System.Collections.Generic;
using CpuArch = System.Runtime.InteropServices.Architecture;
using SLSKDONET.Services.Update;
using Xunit;

namespace SLSKDONET.Tests.Services.Update;

/// <summary>#459: updater reads cross-platform AssemblyInformationalVersion and selects OS/CPU-matched assets.</summary>
public sealed class GitHubUpdateServiceTests
{
    [Fact]
    public void GetCurrentAppVersion_ReadsRealAssemblyVersionNotZero()
    {
        var version = GitHubUpdateService.GetCurrentAppVersion();
        Assert.True(version > new Version(0, 0, 0), $"Expected a real version > 0.0.0, got {version}");
    }

    [Fact]
    public void SelectInstallerUrl_PicksMatchingAssetPerOsAndCpu()
    {
        var assets = new List<GitHubAsset>
        {
            new("ORBIT-Setup-0.5.0-win-x64.exe", "https://example.com/win.exe"),
            new("ORBIT-0.5.0-osx-arm64.dmg", "https://example.com/mac-arm64.dmg"),
            new("ORBIT-0.5.0-osx-x64.dmg", "https://example.com/mac-x64.dmg"),
            new("ORBIT-0.5.0-linux-x64.AppImage", "https://example.com/linux.AppImage"),
        };

        Assert.Equal("https://example.com/win.exe",
            GitHubUpdateService.SelectInstallerUrl(assets, isWindows: true, isMacOS: false, CpuArch.X64));
        Assert.Equal("https://example.com/mac-arm64.dmg",
            GitHubUpdateService.SelectInstallerUrl(assets, isWindows: false, isMacOS: true, CpuArch.Arm64));
        Assert.Equal("https://example.com/mac-x64.dmg",
            GitHubUpdateService.SelectInstallerUrl(assets, isWindows: false, isMacOS: true, CpuArch.X64));
        Assert.Equal("https://example.com/linux.AppImage",
            GitHubUpdateService.SelectInstallerUrl(assets, isWindows: false, isMacOS: false, CpuArch.X64));
    }

    [Fact]
    public void SelectInstallerUrl_MacFallsBackToUniversalDmg_NeverWrongArch()
    {
        var universalOnly = new List<GitHubAsset>
        {
            new("ORBIT-0.5.0-universal.dmg", "https://example.com/universal.dmg"),
        };
        Assert.Equal("https://example.com/universal.dmg",
            GitHubUpdateService.SelectInstallerUrl(universalOnly, isWindows: false, isMacOS: true, CpuArch.Arm64));

        var intelOnly = new List<GitHubAsset>
        {
            new("ORBIT-0.5.0-osx-x64.dmg", "https://example.com/intel.dmg"),
        };
        Assert.Null(GitHubUpdateService.SelectInstallerUrl(intelOnly, isWindows: false, isMacOS: true, CpuArch.Arm64));
    }
}
