using System;
using System.IO;
using System.Linq;
using SLSKDONET.Services.Entertainment;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>#426: MilkDrop .milk parser defensive validation, clamping, built-in pack, and user folder loading.</summary>
public class MilkDropPresetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-milk-{Guid.NewGuid():N}");

    public MilkDropPresetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void TryParse_ValidMilkFile_ExtractsParametersAndEquations()
    {
        const string milk = """
        MILKDROP_PRESET_VERSION=201
        [preset00]
        fDecay=0.975
        zoom=1.04
        rot=-0.02
        warp=0.45
        nWaveMode=2
        fWaveAlpha=0.88
        wave_r=0.25
        wave_g=0.75
        wave_b=1.0
        bAdditiveWaves=1
        per_frame_1=zoom = zoom + 0.02*bass;
        per_pixel_1=rot = rot + 0.01*rad;
        """;

        var ok = MilkDropPresetParser.TryParse("Test Preset", milk, out var preset, out var error);

        Assert.True(ok, error);
        Assert.NotNull(preset);
        Assert.Equal("Test Preset", preset!.Name);
        Assert.Equal(0.975f, preset.Decay, 3);
        Assert.Equal(1.04f, preset.Zoom, 3);
        Assert.Equal(-0.02f, preset.Rot, 3);
        Assert.Equal(2, preset.WaveMode);
        Assert.True(preset.AdditiveWaves);
        Assert.Equal(1, preset.PerFrameEquationCount);
        Assert.Equal(1, preset.PerPixelEquationCount);
    }

    [Fact]
    public void TryParse_ClampsOutOfRangeAndNaNValuesSafely()
    {
        const string hostile = """
        [preset00]
        fDecay=999
        zoom=NaN
        rot=-50
        nWaveMode=99
        wave_r=Infinity
        """;

        var ok = MilkDropPresetParser.TryParse("Hostile", hostile, out var preset, out _);

        Assert.True(ok);
        Assert.NotNull(preset);
        Assert.InRange(preset!.Decay, 0.80f, 0.995f);
        Assert.True(float.IsFinite(preset.Zoom));
        Assert.InRange(preset.Rot, -0.25f, 0.25f);
        Assert.InRange(preset.WaveMode, 0, 7);
        Assert.True(float.IsFinite(preset.WaveR));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("not a milkdrop file at all")]
    [InlineData("binary\0garbage")]
    public void TryParse_RejectsMalformedContentGracefully(string bad)
    {
        var ok = MilkDropPresetParser.TryParse("Bad", bad, out var preset, out var error);
        Assert.False(ok);
        Assert.Null(preset);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void BuiltInAndUserFolder_LoadsAllBuiltInsAndMergesValidUserPresets()
    {
        Assert.Equal(12, MilkDropPresetLibrary.BuiltIn.Count);

        File.WriteAllText(Path.Combine(_dir, "User Good.milk"), "[preset00]\nfDecay=0.95\nzoom=1.02\n");
        File.WriteAllText(Path.Combine(_dir, "User Corrupt.milk"), "this is totally broken");

        var all = MilkDropPresetLibrary.LoadAll(_dir);

        Assert.Equal(13, all.Count);
        Assert.Contains(all, p => p.Name == "User Good");
        Assert.DoesNotContain(all, p => p.Name == "User Corrupt");
    }
}
