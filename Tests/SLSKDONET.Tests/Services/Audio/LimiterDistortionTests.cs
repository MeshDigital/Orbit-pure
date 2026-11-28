using System;
using SLSKDONET.Services.Audio;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// Loudness normalisation is off by default, so hot masters (DnB peaking near 0 dBFS) play through
/// the mix bus's −1 dBFS limiter all the time. A zero-latency limiter whose gain follows every
/// peak inside a bass cycle modulates the music with its own waveform: audible grit.
/// </summary>
public sealed class LimiterDistortionTests
{
    private readonly ITestOutputHelper _out;
    public LimiterDistortionTests(ITestOutputHelper output) => _out = output;

    private static readonly double[] Freqs = { 60.0, 1000.0 };

    private double LimitedThdN(double amplitude)
    {
        const int rate = 48000;
        var limiter = new Limiter(thresholdDb: AutoMixBusSampleProvider.LimiterCeilingDb, sampleRate: rate);
        var x = new float[rate * 2 * 3];
        new TestTones(rate, (60, amplitude * 0.6), (1000, amplitude * 0.4)).Read(x, 0, x.Length);
        for (int p = 0; p < x.Length; p += 960) limiter.Process(x, p, Math.Min(960, x.Length - p));
        double thd = AudioQualityProbe.ThdNDb(x, rate, Freqs, rate / 2);
        _out.WriteLine($"amplitude {amplitude}: THD+N {thd:0.0} dB");
        return thd;
    }

    [Theory]
    [InlineData(0.99)] // a hot master, just over the −1 dBFS ceiling
    [InlineData(1.3)]  // two loud decks overlapping
    public void SustainedOverCeilingMusic_IsTurnedDown_NotDistorted(double amplitude)
    {
        Assert.True(LimitedThdN(amplitude) < -60, "the limiter distorts sustained material over its ceiling");
    }

    [Fact]
    public void NoSampleEverExceedsTheCeiling()
    {
        const int rate = 48000;
        var limiter = new Limiter(thresholdDb: AutoMixBusSampleProvider.LimiterCeilingDb, sampleRate: rate);
        var x = new float[rate * 2];
        new TestTones(rate, (60, 0.9), (1000, 0.6), (7000, 0.3)).Read(x, 0, x.Length);
        limiter.Process(x, 0, x.Length);
        float max = 0f;
        foreach (var s in x) max = Math.Max(max, Math.Abs(s));
        Assert.True(max <= limiter.ThresholdLinear + 1e-6f, $"peak {max} over ceiling {limiter.ThresholdLinear}");
    }

    [Fact]
    public void QuietMaterial_PassesUntouched()
    {
        var limiter = new Limiter(thresholdDb: AutoMixBusSampleProvider.LimiterCeilingDb, sampleRate: 48000);
        var x = new float[4800];
        new TestTones(48000, (60, 0.3), (1000, 0.2)).Read(x, 0, x.Length);
        var copy = (float[])x.Clone();
        limiter.Process(x, 0, x.Length);
        Assert.Equal(copy, x);
    }
}
