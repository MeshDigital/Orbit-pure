using System;
using NAudio.Wave;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

public class TransitionPreviewTempoTests
{
    [Fact]
    public void ApplyPlaybackRate_Unity_ReturnsTheOriginalProvider()
    {
        var source = new FiniteSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1), 48_000);

        var rated = SurgicalProcessingService.ApplyPlaybackRate(source, 1.0);

        Assert.Same(source, rated);
    }

    [Fact]
    public void ApplyPlaybackRate_DoubleSpeed_PreservesFormatAndHalvesSampleCount()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var source = new FiniteSampleProvider(format, 48_000);

        var rated = SurgicalProcessingService.ApplyPlaybackRate(source, 2.0);

        Assert.Same(format, rated.WaveFormat);
        int samples = ReadAll(rated);
        Assert.InRange(samples, 23_800, 24_100); // allow a small resampler-latency margin
    }

    private static int ReadAll(ISampleProvider provider)
    {
        var buffer = new float[1024];
        int total = 0;
        while (true)
        {
            int read = provider.Read(buffer, 0, buffer.Length);
            if (read == 0) return total;
            total += read;
        }
    }

    private sealed class FiniteSampleProvider : ISampleProvider
    {
        private int _remaining;

        public FiniteSampleProvider(WaveFormat waveFormat, int sampleCount)
        {
            WaveFormat = waveFormat;
            _remaining = sampleCount;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _remaining);
            for (int i = 0; i < read; i++) buffer[offset + i] = 0.25f;
            _remaining -= read;
            return read;
        }
    }
}
