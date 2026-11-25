using System.Collections.Generic;
using System.IO;
using CpuArch = System.Runtime.InteropServices.Architecture;
using SLSKDONET.Services.Platform;
using Xunit;

namespace SLSKDONET.Tests.Services.Platform;

/// <summary>#453: IToolLocator resolves FFmpeg, Essentia, fpcalc, and curl across win-x64, osx-x64, osx-arm64, linux-x64.</summary>
public sealed class ToolLocatorTests
{
    [Theory]
    [InlineData(true, false, CpuArch.X64, "win-x64")]
    [InlineData(false, true, CpuArch.X64, "osx-x64")]
    [InlineData(false, true, CpuArch.Arm64, "osx-arm64")]
    [InlineData(false, false, CpuArch.X64, "linux-x64")]
    public void DetectRid_MapsOsAndArchCorrectly(bool isWin, bool isMac, CpuArch arch, string expected)
    {
        Assert.Equal(expected, DefaultToolLocator.DetectRid(isWin, isMac, arch));
    }

    [Fact]
    public void Locate_PrefersPerRidBundledBinaryOverLegacyAndPath()
    {
        var baseDir = Path.Combine("app");
        var perRid = Path.Combine(baseDir, "Tools", "osx-arm64", "ffmpeg");
        var flat = Path.Combine(baseDir, "ffmpeg");
        var existing = new HashSet<string> { perRid, flat };

        var locator = new DefaultToolLocator(
            baseDirectory: baseDir,
            rid: "osx-arm64",
            isWindows: false,
            isMacOS: true,
            pathEnv: "/usr/bin",
            managedFfmpegPath: "",
            fileExists: existing.Contains);

        Assert.Equal(perRid, locator.Locate(ExternalTool.Ffmpeg));
        Assert.True(locator.IsAvailable(ExternalTool.Ffmpeg));
    }

    [Fact]
    public void Locate_OnAppleSiliconMac_ChecksHomebrewOptFirst()
    {
        var brewArm = Path.Combine("/opt/homebrew/bin", "ffmpeg");
        var brewIntel = Path.Combine("/usr/local/bin", "ffmpeg");
        var existing = new HashSet<string> { brewArm, brewIntel };

        var locator = new DefaultToolLocator(
            baseDirectory: "/Applications/ORBIT.app/Contents/MacOS",
            rid: "osx-arm64",
            isWindows: false,
            isMacOS: true,
            pathEnv: "",
            managedFfmpegPath: "",
            fileExists: existing.Contains);

        Assert.Equal(brewArm, locator.Locate(ExternalTool.Ffmpeg));
    }

    [Fact]
    public void Locate_ResolvesCurlFromSystem32OnWindowsAndUsrBinOnLinux()
    {
        var winCurl = Path.Combine(@"C:\Windows\System32", "curl.exe");
        var winLocator = new DefaultToolLocator(
            baseDirectory: @"C:\ORBIT",
            rid: "win-x64",
            isWindows: true,
            isMacOS: false,
            pathEnv: "",
            systemDirectory: @"C:\Windows\System32",
            managedFfmpegPath: "",
            fileExists: p => p == winCurl);

        Assert.Equal(winCurl, winLocator.Locate(ExternalTool.Curl));

        var linuxCurl = Path.Combine("/usr/bin", "curl");
        var linuxLocator = new DefaultToolLocator(
            baseDirectory: "/opt/orbit",
            rid: "linux-x64",
            isWindows: false,
            isMacOS: false,
            pathEnv: "",
            managedFfmpegPath: "",
            fileExists: p => p == linuxCurl);

        Assert.Equal(linuxCurl, linuxLocator.Locate(ExternalTool.Curl));
    }

    [Fact]
    public void Locate_ResolvesEssentiaAndFpcalcWithoutExeSuffixOnUnix()
    {
        var essentia = Path.Combine("/opt/orbit", "Tools", "linux-x64", "essentia_streaming_extractor_music");
        var fpcalc = Path.Combine("/usr/bin", "fpcalc");
        var existing = new HashSet<string> { essentia, fpcalc };

        var locator = new DefaultToolLocator(
            baseDirectory: "/opt/orbit",
            rid: "linux-x64",
            isWindows: false,
            isMacOS: false,
            pathEnv: "",
            managedFfmpegPath: "",
            fileExists: existing.Contains);

        Assert.Equal(essentia, locator.Locate(ExternalTool.Essentia));
        Assert.Equal(fpcalc, locator.Locate(ExternalTool.Fpcalc));
    }
}
