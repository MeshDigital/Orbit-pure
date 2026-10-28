using System.Linq;
using System.Runtime.CompilerServices;
using CpuArch = System.Runtime.InteropServices.Architecture;
using SLSKDONET.Services.Platform;
using Xunit;

namespace SLSKDONET.Tests.Services.Platform;

/// <summary>
/// Runs before any test: pins a capable machine so engine and queue tests don't depend on the
/// hardware (or OS) of whoever runs them. The verdict rules themselves are tested on fixed profiles.
/// </summary>
internal static class TestHardwareSetup
{
    [ModuleInitializer]
    internal static void Initialize() => HardwareCapabilities.Override = HardwareCapabilitiesTests.Machine(ramGb: 32, cores: 16, gpuGb: 8);
}

public class HardwareCapabilitiesTests
{
    internal static HardwareProfile Machine(double ramGb, int cores, double gpuGb = 0, bool windows = true,
        CpuArch arch = CpuArch.X64) => new()
    {
        OsName = windows ? "Windows" : "Linux",
        IsWindows = windows,
        Architecture = arch,
        LogicalCores = cores,
        // Usable memory is a little under the label, as on real machines.
        TotalMemoryBytes = (long)(ramGb * 0.97 * HardwareProfile.GB),
        Gpus = gpuGb > 0 ? new[] { new GpuInfo("Test GPU", (long)(gpuGb * HardwareProfile.GB)) } : System.Array.Empty<GpuInfo>(),
    };

    [Fact]
    public void GamingPc_RunsEverything()
    {
        var pc = Machine(ramGb: 16, cores: 12, gpuGb: 8);

        Assert.Empty(pc.Blocked);
        Assert.Null(pc.VerdictFor(HardwareFeature.StemSeparation).Advice);
    }

    [Fact]
    public void EightGbLaptopWithIntegratedGraphics_BlocksOnlyStems()
    {
        var laptop = Machine(ramGb: 8, cores: 8, gpuGb: 0.5);

        Assert.True(laptop.Allows(HardwareFeature.TrackAnalysis));
        Assert.True(laptop.Allows(HardwareFeature.AiTagging));
        var stems = laptop.VerdictFor(HardwareFeature.StemSeparation);
        Assert.False(stems.IsAllowed);
        Assert.Contains("16 GB", stems.Reason);
    }

    [Fact]
    public void SixteenGbWithoutGpu_AllowsStemsOnCpuWithAdvice()
    {
        var pc = Machine(ramGb: 16, cores: 8);

        var stems = pc.VerdictFor(HardwareFeature.StemSeparation);
        Assert.True(stems.IsAllowed);
        Assert.Contains("CPU", stems.Advice);
    }

    [Fact]
    public void FourGbDualCore_KeepsAnalysisButBlocksAi()
    {
        var netbook = Machine(ramGb: 4, cores: 2);

        Assert.True(netbook.Allows(HardwareFeature.TrackAnalysis));
        Assert.False(netbook.Allows(HardwareFeature.AiTagging));
        Assert.Contains("8 GB", netbook.VerdictFor(HardwareFeature.AiTagging).Reason);
    }

    [Fact]
    public void TwoGbMachine_BlocksAnalysis()
    {
        Assert.False(Machine(ramGb: 2, cores: 4).Allows(HardwareFeature.TrackAnalysis));
    }

    [Theory]
    [InlineData(false, CpuArch.X64)]
    [InlineData(true, CpuArch.Arm64)]
    public void UnsupportedPlatform_BlocksEveryFeatureWithTheReason(bool windows, CpuArch arch)
    {
        var pc = Machine(ramGb: 64, cores: 32, gpuGb: 24, windows, arch);

        Assert.Equal(3, pc.Blocked.Count());
        Assert.All(pc.Blocked, v => Assert.Contains("64-bit Windows", v.Reason));
    }

    [Theory]
    [InlineData(8.0, "8 GB")]
    [InlineData(7.8, "7.8 GB")]
    [InlineData(31.9, "32 GB")]
    public void FormatGb_IsReadable(double gb, string expected)
    {
        Assert.Equal(expected, HardwareProfile.FormatGb((long)(gb * HardwareProfile.GB)));
    }
}
