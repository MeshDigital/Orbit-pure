using System;
using NAudio.Wave;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>Stereo sum of sine tones (both channels identical), for distortion measurements.</summary>
internal sealed class TestTones : ISampleProvider
{
    private readonly (double Freq, double Amp)[] _tones;
    private long _n;

    public TestTones(int sampleRate, params (double Freq, double Amp)[] tones)
    {
        _tones = tones;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    }

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

/// <summary>Measures THD+N: the residual after a least-squares fit of known tones (any gain, phase, delay).</summary>
internal static class AudioQualityProbe
{
    /// <summary>Residual-to-signal ratio in dB of the left channel of <paramref name="stereo"/>,
    /// ignoring the first <paramref name="skipFrames"/> frames (filter settling).</summary>
    public static double ThdNDb(float[] stereo, int sampleRate, double[] freqs, int skipFrames)
    {
        int k = freqs.Length * 2 + 1, n = stereo.Length / 2 - skipFrames;
        var ata = new double[k, k];
        var atb = new double[k];
        var row = new double[k];

        void Fill(int i)
        {
            double t = (i + skipFrames) / (double)sampleRate;
            for (int j = 0; j < freqs.Length; j++)
            {
                row[2 * j] = Math.Sin(2 * Math.PI * freqs[j] * t);
                row[2 * j + 1] = Math.Cos(2 * Math.PI * freqs[j] * t);
            }
            row[k - 1] = 1;
        }

        for (int i = 0; i < n; i++)
        {
            Fill(i);
            double y = stereo[2 * (i + skipFrames)];
            for (int a = 0; a < k; a++)
            {
                atb[a] += row[a] * y;
                for (int b = 0; b < k; b++) ata[a, b] += row[a] * row[b];
            }
        }

        var c = Solve(ata, atb);
        double signal = 0, residual = 0;
        for (int i = 0; i < n; i++)
        {
            Fill(i);
            double fit = 0;
            for (int a = 0; a < k; a++) fit += c[a] * row[a];
            double y = stereo[2 * (i + skipFrames)];
            signal += fit * fit;
            residual += (y - fit) * (y - fit);
        }
        return 10 * Math.Log10(residual / signal);
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (int p = 0; p < n; p++)
        {
            int max = p;
            for (int i = p + 1; i < n; i++) if (Math.Abs(m[i, p]) > Math.Abs(m[max, p])) max = i;
            for (int j = 0; j < n; j++) (m[p, j], m[max, j]) = (m[max, j], m[p, j]);
            (v[p], v[max]) = (v[max], v[p]);
            for (int i = p + 1; i < n; i++)
            {
                double f = m[i, p] / m[p, p];
                v[i] -= f * v[p];
                for (int j = p; j < n; j++) m[i, j] -= f * m[p, j];
            }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = v[i];
            for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return x;
    }
}
