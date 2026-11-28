using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// DJ report 2026-11-28: "a weird buzzing, like electricity crackling" on speakers and earbuds in
/// 0.5.2-beta.2. The mix bus ran at the device's 48 kHz and read the 44.1 kHz → 48 kHz resampler in
/// 256-frame blocks at buffer offsets, which NAudio's WDL port corrupts (−19 dB THD+N).
/// </summary>
public sealed class OffsetSafeResamplerTests
{
    private static readonly double[] Freqs = { 60.0, 1000.0 };

    private static double ThdN(float[] x, int rate, int skipFrames) => AudioQualityProbe.ThdNDb(x, rate, Freqs, skipFrames);

    private static float[] RenderBus(int busRate, int sourceRate)
    {
        var bus = new AutoMixBusSampleProvider(busRate) { MasterVolume = 1f, IsPaused = false };
        var gain = new VolumeSampleProvider(new TestTones(sourceRate, (60, 0.3), (1000, 0.2)));
        bus.SetActiveDeck(new MixBusDeck { Source = bus.AdaptToBusFormat(gain), Gain = gain });
        var output = new float[busRate * 2 * 2];
        var chunk = new float[busRate / 100 * 2]; // 10 ms, like a WASAPI period
        for (int pos = 0; pos < output.Length; pos += chunk.Length)
        {
            bus.Read(chunk, 0, chunk.Length);
            Array.Copy(chunk, 0, output, pos, Math.Min(chunk.Length, output.Length - pos));
        }
        return output;
    }

    [Theory]
    [InlineData(48000, 44100)] // the crackle: a 44.1 kHz track on a 48 kHz device
    [InlineData(44100, 48000)]
    [InlineData(96000, 44100)]
    public void MixBus_ResampledPlayback_IsClean(int busRate, int sourceRate)
    {
        var output = RenderBus(busRate, sourceRate);
        Assert.True(ThdN(output, busRate, busRate / 4) < -60, "resampled mix bus output is distorted");
    }

    [Fact]
    public void Wrapper_ReadAtOffsetsInsideOneBuffer_MatchesAPlainSequentialRead()
    {
        var safe = OffsetSafeResampler.Create(new TestTones(44100, (60, 0.3), (1000, 0.2)), 48000);
        var output = new float[48000 * 4];
        var chunk = new float[960];
        for (int pos = 0; pos < output.Length; pos += 960)
        {
            safe.Read(chunk, 0, 512);
            safe.Read(chunk, 512, 448); // the read NAudio's resampler gets wrong
            Array.Copy(chunk, 0, output, pos, 960);
        }
        Assert.True(ThdN(output, 48000, 12000) < -60);
    }
}
