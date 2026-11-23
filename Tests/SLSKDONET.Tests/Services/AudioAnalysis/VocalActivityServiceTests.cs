using System;
using System.Threading;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>#285: the RMS-per-window extraction that feeds <see cref="Engine.Analysis.VocalActivity"/>.</summary>
public class VocalActivityServiceTests
{
    [Fact]
    public void ComputeWindowRms_Mono_SplitsIntoWindows()
    {
        // 100 Hz, 0.5 s windows = 50 frames each. First two windows are a 0.5 constant, the last is silence.
        var mono = new float[150];
        for (var i = 0; i < 100; i++) mono[i] = 0.5f;

        var rms = VocalActivityService.ComputeWindowRms(mono, channels: 1, sampleRate: 100, windowSeconds: 0.5);

        Assert.Equal(new[] { 0.5f, 0.5f, 0f }, rms);
    }

    [Fact]
    public void ComputeWindowRms_AveragesChannelsToMono()
    {
        // Two identical channels: averaging leaves the RMS unchanged.
        var stereo = new float[200]; // 100 frames × 2 channels
        for (var f = 0; f < 100; f++)
        {
            stereo[f * 2] = 0.5f;
            stereo[f * 2 + 1] = 0.5f;
        }

        var rms = VocalActivityService.ComputeWindowRms(stereo, channels: 2, sampleRate: 100, windowSeconds: 0.5);

        Assert.Equal(2, rms.Length);
        Assert.Equal(0.5f, rms[0], 4);
        Assert.Equal(0.5f, rms[1], 4);
    }

    [Fact]
    public void ComputeWindowRms_KeepsTheFinalPartialWindow()
    {
        // 120 frames at 50-frame windows → 3 windows, the last holding 20 frames.
        var mono = new float[120];
        for (var i = 0; i < 120; i++) mono[i] = 0.5f;

        var rms = VocalActivityService.ComputeWindowRms(mono, channels: 1, sampleRate: 100, windowSeconds: 0.5);

        Assert.Equal(3, rms.Length);
        Assert.Equal(0.5f, rms[2], 4);
    }

    [Fact]
    public void ComputeWindowRms_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(VocalActivityService.ComputeWindowRms(Array.Empty<float>(), 1, 44100, 0.5));
    }
}
