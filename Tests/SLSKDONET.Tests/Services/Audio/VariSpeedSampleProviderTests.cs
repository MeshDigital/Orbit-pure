using NAudio.Wave;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// Covers AudioPlayerService.VariSpeedSampleProvider — the turntable/CDJ-style pitch engine
/// (reads its source at a variable rate via linear interpolation between samples, so speed and
/// pitch move together like a real pitch fader).
/// </summary>
public class VariSpeedSampleProviderTests
{
    /// <summary>A mono source that yields a fixed, known sequence of ramp values (0, 1, 2, ...)
    /// and then reports exhaustion, so output can be checked deterministically.</summary>
    private class RampSampleProvider : ISampleProvider
    {
        private readonly int _totalFrames;
        private int _position;

        public RampSampleProvider(int totalFrames)
        {
            _totalFrames = totalFrames;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int available = _totalFrames - _position;
            int toRead = System.Math.Min(available, count);
            for (int i = 0; i < toRead; i++)
            {
                buffer[offset + i] = _position + i;
            }
            _position += toRead;
            return toRead;
        }

        /// <summary>Test-only: simulates AudioFileReader.Position being set directly by a seek,
        /// bypassing the provider entirely.</summary>
        public void Reposition(int newPosition) => _position = newPosition;
    }

    [Fact]
    public void Read_AtNormalSpeed_ReproducesSourceValues()
    {
        var source = new RampSampleProvider(1000);
        var provider = new AudioPlayerService.VariSpeedSampleProvider(source) { Speed = 1.0 };

        var buffer = new float[100];
        int read = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(100, read);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i, buffer[i], precision: 3);
        }
    }

    [Fact]
    public void Read_UntilExhausted_ProducesFewerFramesWhenSpeedIsAboveOne()
    {
        var normalSource = new RampSampleProvider(4410);
        var normalProvider = new AudioPlayerService.VariSpeedSampleProvider(normalSource) { Speed = 1.0 };
        int normalTotal = ReadUntilExhausted(normalProvider);

        var fastSource = new RampSampleProvider(4410);
        var fastProvider = new AudioPlayerService.VariSpeedSampleProvider(fastSource) { Speed = 2.0 };
        int fastTotal = ReadUntilExhausted(fastProvider);

        // Speeding up consumes the same source material in roughly half the output frames.
        Assert.True(fastTotal < normalTotal, $"Expected fewer output frames at 2x speed (got {fastTotal} vs {normalTotal})");
        Assert.InRange(fastTotal, normalTotal / 2 - 50, normalTotal / 2 + 50);
    }

    [Fact]
    public void Read_UntilExhausted_ProducesMoreFramesWhenSpeedIsBelowOne()
    {
        var normalSource = new RampSampleProvider(4410);
        var normalProvider = new AudioPlayerService.VariSpeedSampleProvider(normalSource) { Speed = 1.0 };
        int normalTotal = ReadUntilExhausted(normalProvider);

        var slowSource = new RampSampleProvider(4410);
        var slowProvider = new AudioPlayerService.VariSpeedSampleProvider(slowSource) { Speed = 0.5 };
        int slowTotal = ReadUntilExhausted(slowProvider);

        Assert.True(slowTotal > normalTotal, $"Expected more output frames at 0.5x speed (got {slowTotal} vs {normalTotal})");
        Assert.InRange(slowTotal, normalTotal * 2 - 50, normalTotal * 2 + 50);
    }

    [Fact]
    public void Reset_DiscardsStaleBufferedSamples_AfterSourceIsRepositioned()
    {
        var source = new RampSampleProvider(1000);
        var provider = new AudioPlayerService.VariSpeedSampleProvider(source) { Speed = 1.0 };

        var buffer = new float[10];
        provider.Read(buffer, 0, buffer.Length); // reads ahead into an internal buffer

        // Simulate a seek: the underlying source is repositioned directly (as
        // AudioFileReader.Position does), bypassing the provider entirely, leaving it holding
        // a buffer of now-stale samples from before the seek.
        source.Reposition(500);
        provider.Reset();

        var afterSeek = new float[10];
        int read = provider.Read(afterSeek, 0, afterSeek.Length);

        Assert.Equal(10, read);
        Assert.Equal(500, afterSeek[0], precision: 3); // reflects the new position, not the stale pre-seek buffer
    }

    private static int ReadUntilExhausted(AudioPlayerService.VariSpeedSampleProvider provider)
    {
        var buffer = new float[256];
        int total = 0;
        int read;
        int safetyIterations = 0;
        do
        {
            read = provider.Read(buffer, 0, buffer.Length);
            total += read;
            safetyIterations++;
        }
        while (read > 0 && safetyIterations < 10000);

        return total;
    }
}
