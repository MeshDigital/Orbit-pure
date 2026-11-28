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
    private sealed class Tones : ISampleProvider
    {
        private readonly (double F, double A)[] _tones;
        private long _n;
        public Tones(int rate, params (double, double)[] tones) { _tones = tones; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count / 2; i++, _n++)
            {
                double s = 0;
                foreach (var (f, a) in _tones) s += a * Math.Sin(2 * Math.PI * f * _n / WaveFormat.SampleRate);
                buffer[offset + 2 * i] = buffer[offset + 2 * i + 1] = (float)s;
            }
            return count;
        }
    }

    private static readonly double[] Freqs = { 60.0, 1000.0 };

    /// <summary>Residual after a least-squares fit of the test tones (THD+N), in dB.</summary>
    private static double ThdN(float[] x, int rate, int skipFrames)
    {
        int k = Freqs.Length * 2 + 1, n = x.Length / 2 - skipFrames;
        var ata = new double[k, k]; var atb = new double[k]; var row = new double[k];
        double[] Row(int i)
        {
            double t = (i + skipFrames) / (double)rate;
            for (int j = 0; j < Freqs.Length; j++) { row[2 * j] = Math.Sin(2 * Math.PI * Freqs[j] * t); row[2 * j + 1] = Math.Cos(2 * Math.PI * Freqs[j] * t); }
            row[k - 1] = 1;
            return row;
        }
        for (int i = 0; i < n; i++)
        {
            var r = Row(i); double y = x[2 * (i + skipFrames)];
            for (int a = 0; a < k; a++) { atb[a] += r[a] * y; for (int b = 0; b < k; b++) ata[a, b] += r[a] * r[b]; }
        }
        var c = Solve(ata, atb);
        double sig = 0, res = 0;
        for (int i = 0; i < n; i++)
        {
            var r = Row(i); double fit = 0;
            for (int a = 0; a < k; a++) fit += c[a] * r[a];
            double y = x[2 * (i + skipFrames)];
            sig += fit * fit; res += (y - fit) * (y - fit);
        }
        return 10 * Math.Log10(res / sig);
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length; var m = (double[,])a.Clone(); var v = (double[])b.Clone();
        for (int p = 0; p < n; p++)
        {
            int max = p;
            for (int i = p + 1; i < n; i++) if (Math.Abs(m[i, p]) > Math.Abs(m[max, p])) max = i;
            for (int j = 0; j < n; j++) (m[p, j], m[max, j]) = (m[max, j], m[p, j]);
            (v[p], v[max]) = (v[max], v[p]);
            for (int i = p + 1; i < n; i++) { double f = m[i, p] / m[p, p]; v[i] -= f * v[p]; for (int j = p; j < n; j++) m[i, j] -= f * m[p, j]; }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--) { double s = v[i]; for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j]; x[i] = s / m[i, i]; }
        return x;
    }

    private static float[] RenderBus(int busRate, int sourceRate)
    {
        var bus = new AutoMixBusSampleProvider(busRate) { MasterVolume = 1f, IsPaused = false };
        var gain = new VolumeSampleProvider(new Tones(sourceRate, (60, 0.3), (1000, 0.2)));
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
        var safe = OffsetSafeResampler.Create(new Tones(44100, (60, 0.3), (1000, 0.2)), 48000);
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
