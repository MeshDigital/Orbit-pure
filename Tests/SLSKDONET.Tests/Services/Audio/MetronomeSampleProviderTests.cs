using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>Sample-accurate scheduling, accent pattern and seek for the Cue Forge metronome (#222).</summary>
public class MetronomeSampleProviderTests
{
    private const int Sr = 8000;

    private sealed class SilentSource : ISampleProvider
    {
        public WaveFormat WaveFormat { get; }
        private long _frames;
        public SilentSource(int sampleRate = Sr, int channels = 1)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            _frames += count / WaveFormat.Channels;
            return count;
        }
    }

    [Fact]
    public void Click_LandsOnEachBeat()
    {
        var beats = new List<double> { 0.5, 1.0, 1.5 };
        var provider = new MetronomeSampleProvider(new SilentSource(), beats, downbeatSeconds: 0.5);

        var buffer = ReadAll(provider, seconds: 2.0);

        Assert.True(EnergyAt(buffer, 0.5, Sr) > 0, "click at beat 0.5s");
        Assert.True(EnergyAt(buffer, 1.0, Sr) > 0, "click at beat 1.0s");
        Assert.True(EnergyAt(buffer, 1.5, Sr) > 0, "click at beat 1.5s");
        Assert.Equal(0f, EnergyAt(buffer, 0.75, Sr), 4); // between beats: silence
    }

    [Fact]
    public void Accent_IsLouderOnBarDownbeats()
    {
        // Downbeat at index 1 (time 1.0s) makes that beat the "1" of the bar.
        var beats = Enumerable.Range(0, 20).Select(i => 0.5 + i * 0.5).ToList(); // 120 BPM from 0.5s
        int downbeatIndex = 1; // beats[1] == 1.0s is Bar 1
        var provider = new MetronomeSampleProvider(new SilentSource(), beats, downbeatSeconds: beats[downbeatIndex]);

        var buffer = ReadAll(provider, seconds: 6.0);

        float barOneEnergy = EnergyAt(buffer, beats[downbeatIndex], Sr);
        float beatTwoEnergy = EnergyAt(buffer, beats[downbeatIndex + 1], Sr);
        Assert.True(barOneEnergy > beatTwoEnergy, $"accent ({barOneEnergy}) should exceed beat ({beatTwoEnergy})");
    }

    [Fact]
    public void Seek_ReclocksTheClick()
    {
        var beats = new List<double> { 0.5, 1.0, 1.5, 2.0, 2.5 };
        var provider = new MetronomeSampleProvider(new SilentSource(), beats, downbeatSeconds: 0.5);

        provider.Seek(2.0); // jump forward
        var buffer = ReadAll(provider, seconds: 1.0);

        Assert.True(EnergyAt(buffer, 0.0, Sr) > 0, "click on the beat at the seek point (2.0s)");
        Assert.True(EnergyAt(buffer, 0.5, Sr) > 0, "next click (2.5s) still scheduled");
    }

    private static float[] ReadAll(MetronomeSampleProvider provider, double seconds)
    {
        int samples = (int)(seconds * provider.WaveFormat.SampleRate) * provider.WaveFormat.Channels;
        var buffer = new float[samples];
        int total = 0;
        while (total < samples)
        {
            int read = provider.Read(buffer, total, Math.Min(4096, samples - total));
            if (read == 0) break;
            total += read;
        }
        return buffer;
    }

    private static float EnergyAt(float[] buffer, double seconds, int sampleRate)
    {
        int start = (int)(seconds * sampleRate);
        int length = Math.Min(sampleRate / 40, buffer.Length - start); // ~25 ms window
        if (start < 0 || start >= buffer.Length || length <= 0) return 0f;
        double sumSq = 0;
        for (int i = start; i < start + length; i++) sumSq += buffer[i] * buffer[i];
        return (float)Math.Sqrt(sumSq / length);
    }
}
