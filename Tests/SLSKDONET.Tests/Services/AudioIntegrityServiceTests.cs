using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Unit tests for <see cref="AudioIntegrityService"/>.
///
/// Because real audio files are not available in the test environment, these tests verify:
/// 1. The result model contract is consistent.
/// 2. File-not-found and cancellation paths return the expected verdicts.
/// 3. The classification logic maps broadband vs. bandlimited synthetic audio to the
///    correct <see cref="AudioAuthenticityVerdict"/> values.
/// </summary>
public class AudioIntegrityServiceTests
{
    private static AudioIntegrityService CreateService() =>
        new(NullLogger<AudioIntegrityService>.Instance);

    // ── file-not-found ────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyseAsync_FileNotFound_ReturnsUnknownVerdict()
    {
        var svc = CreateService();
        var result = await svc.AnalyseAsync("/tmp/__nonexistent_audio_file__.flac");

        Assert.Equal(AudioAuthenticityVerdict.Unknown, result.Verdict);
        Assert.False(result.IsGenuineLossless);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    // ── cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyseAsync_CancelledToken_ReturnsUnknownVerdict()
    {
        var svc = CreateService();

        var wavPath = Path.GetTempFileName() + ".wav";
        try
        {
            WriteWhiteNoiseWav(wavPath, sampleRate: 44100, durationSeconds: 60);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // already cancelled

            var result = await svc.AnalyseAsync(wavPath, cts.Token);

            Assert.Equal(AudioAuthenticityVerdict.Unknown, result.Verdict);
        }
        finally
        {
            if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath);
        }
    }

    // ── result model ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioAuthenticityVerdict.GenuineLossless,        true)]
    [InlineData(AudioAuthenticityVerdict.TranscodedHighBitrate,  false)]
    [InlineData(AudioAuthenticityVerdict.TranscodedMediumBitrate,false)]
    [InlineData(AudioAuthenticityVerdict.TranscodedLowBitrate,   false)]
    [InlineData(AudioAuthenticityVerdict.Unknown,                false)]
    public void IsGenuineLossless_ReflectsVerdict(AudioAuthenticityVerdict verdict, bool expected)
    {
        var result = new SpectralIntegrityResult { Verdict = verdict };
        Assert.Equal(expected, result.IsGenuineLossless);
    }

    [Fact]
    public void SpectralIntegrityResult_DefaultValues_AreConsistent()
    {
        var r = new SpectralIntegrityResult();
        Assert.Equal(AudioAuthenticityVerdict.Unknown, r.Verdict);
        Assert.Equal(0.0, r.Confidence);
        Assert.Equal(0.0, r.SpectralCutoffHz);
        Assert.Equal(0, r.FileBitDepth);
    }

    // ── synthetic WAV analysis ────────────────────────────────────────────────

    /// <summary>
    /// Broadband white noise spans the full spectrum and should be classified as
    /// GenuineLossless since all frequency bins carry significant energy.
    /// </summary>
    [Fact]
    public async Task AnalyseAsync_BroadbandWhiteNoise_44100sr_ReturnsGenuineLossless()
    {
        var wavPath = Path.GetTempFileName() + ".wav";
        try
        {
            WriteWhiteNoiseWav(wavPath, sampleRate: 44100, durationSeconds: 35);

            var svc = CreateService();
            var result = await svc.AnalyseAsync(wavPath);

            Assert.Equal(AudioAuthenticityVerdict.GenuineLossless, result.Verdict);
            Assert.True(result.SpectralCutoffHz > 19_000,
                $"Expected cutoff > 19 kHz for broadband noise, got {result.SpectralCutoffHz:F0} Hz");
        }
        finally
        {
            if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath);
        }
    }

    /// <summary>
    /// White noise low-pass–filtered at 14 kHz simulates a heavily encoded (fake) file.
    /// The spectral cutoff should be detected near 14 kHz and the verdict should indicate
    /// a lossy source (low or medium bitrate).
    /// </summary>
    [Fact]
    public async Task AnalyseAsync_LowPassedWhiteNoise_14kHz_ReturnsLowOrMediumBitrate()
    {
        var wavPath = Path.GetTempFileName() + ".wav";
        try
        {
            WriteLowPassWhiteNoiseWav(wavPath, sampleRate: 44100, durationSeconds: 35,
                cutoffHz: 14000);

            var svc = CreateService();
            var result = await svc.AnalyseAsync(wavPath);

            Assert.True(result.SpectralCutoffHz < 16_000,
                $"Expected cutoff < 16 kHz, got {result.SpectralCutoffHz:F0} Hz");

            Assert.True(
                result.Verdict == AudioAuthenticityVerdict.TranscodedLowBitrate ||
                result.Verdict == AudioAuthenticityVerdict.TranscodedMediumBitrate,
                $"Unexpected verdict: {result.Verdict}");
        }
        finally
        {
            if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath);
        }
    }

    /// <summary>
    /// White noise low-pass–filtered at 16 kHz simulates a 192 kbps MP3 encode.
    /// The verdict should indicate a medium-bitrate lossy source.
    /// </summary>
    [Fact]
    public async Task AnalyseAsync_LowPassedWhiteNoise_16kHz_ReturnsMediumBitrate()
    {
        var wavPath = Path.GetTempFileName() + ".wav";
        try
        {
            WriteLowPassWhiteNoiseWav(wavPath, sampleRate: 44100, durationSeconds: 35,
                cutoffHz: 16000);

            var svc = CreateService();
            var result = await svc.AnalyseAsync(wavPath);

            Assert.True(result.SpectralCutoffHz < 18_000,
                $"Expected cutoff < 18 kHz, got {result.SpectralCutoffHz:F0} Hz");

            Assert.True(
                result.Verdict == AudioAuthenticityVerdict.TranscodedLowBitrate ||
                result.Verdict == AudioAuthenticityVerdict.TranscodedMediumBitrate,
                $"Unexpected verdict: {result.Verdict}");
        }
        finally
        {
            if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath);
        }
    }

    /// <summary>
    /// White noise low-pass–filtered at 20 kHz (very close to Nyquist for 44.1 kHz audio)
    /// should be classified as genuine lossless or at worst TranscodedHighBitrate, because
    /// 320 kbps MP3 cuts off right around 20 kHz.
    /// </summary>
    [Fact]
    public async Task AnalyseAsync_LowPassedWhiteNoise_20kHz_NotLowBitrate()
    {
        var wavPath = Path.GetTempFileName() + ".wav";
        try
        {
            WriteLowPassWhiteNoiseWav(wavPath, sampleRate: 44100, durationSeconds: 35,
                cutoffHz: 20000);

            var svc = CreateService();
            var result = await svc.AnalyseAsync(wavPath);

            Assert.NotEqual(AudioAuthenticityVerdict.TranscodedLowBitrate, result.Verdict);
            Assert.NotEqual(AudioAuthenticityVerdict.TranscodedMediumBitrate, result.Verdict);
        }
        finally
        {
            if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a mono 16-bit PCM WAV with broadband white noise (full spectrum).
    /// </summary>
    private static void WriteWhiteNoiseWav(string path, int sampleRate, int durationSeconds)
    {
        int sampleCount = sampleRate * durationSeconds;
        short[] samples = new short[sampleCount];
        var rng = new Random(42);

        for (int i = 0; i < sampleCount; i++)
            samples[i] = (short)(rng.NextDouble() * 2 * short.MaxValue - short.MaxValue);

        WriteWav(path, sampleRate, samples);
    }

    /// <summary>
    /// Writes a mono 16-bit PCM WAV with white noise band-limited to <paramref name="cutoffHz"/>
    /// using a Hamming-windowed sinc FIR low-pass filter.
    /// </summary>
    private static void WriteLowPassWhiteNoiseWav(
        string path, int sampleRate, int durationSeconds, double cutoffHz)
    {
        int sampleCount = sampleRate * durationSeconds;
        var rawSamples = new double[sampleCount];
        var rng = new Random(42);
        for (int i = 0; i < sampleCount; i++)
            rawSamples[i] = rng.NextDouble() * 2.0 - 1.0;

        // Hamming-windowed sinc low-pass filter (511 taps → ~50 dB stopband attenuation)
        int order = 511;
        double[] h = BuildLowPassKernel(order, cutoffHz / sampleRate);
        double[] filtered = ApplyFir(rawSamples, h);

        // Normalise and convert to int16
        double peak = filtered.Max(Math.Abs);
        if (peak < 1e-10) peak = 1.0;

        short[] samples = filtered.Select(s => (short)(s / peak * short.MaxValue * 0.9)).ToArray();
        WriteWav(path, sampleRate, samples);
    }

    private static double[] BuildLowPassKernel(int order, double normalizedCutoff)
    {
        double[] h = new double[order + 1];
        int M = order;
        double wc = 2 * Math.PI * normalizedCutoff;

        for (int n = 0; n <= M; n++)
        {
            int k = n - M / 2;
            double sinc = k == 0 ? wc / Math.PI : Math.Sin(wc * k) / (Math.PI * k);
            double hamming = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / M);
            h[n] = sinc * hamming;
        }

        return h;
    }

    private static double[] ApplyFir(double[] signal, double[] kernel)
    {
        int n = signal.Length;
        int k = kernel.Length;
        double[] output = new double[n];

        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < k && j <= i; j++)
                sum += signal[i - j] * kernel[j];
            output[i] = sum;
        }

        return output;
    }

    /// <summary>
    /// Writes a minimal mono 16-bit PCM WAV file.
    /// </summary>
    private static void WriteWav(string path, int sampleRate, short[] samples)
    {
        using var fs = System.IO.File.Create(path);
        using var bw = new BinaryWriter(fs);

        int byteRate = sampleRate * 2; // 1 channel, 16-bit
        int dataSize = samples.Length * 2;

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt  chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);           // chunk size
        bw.Write((short)1);     // PCM
        bw.Write((short)1);     // mono
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)2);     // block align
        bw.Write((short)16);    // bits per sample

        // data chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        foreach (var s in samples) bw.Write(s);
    }
}

