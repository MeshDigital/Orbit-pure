using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Task 1.6 — Extracts a compact tri-band waveform blob from an audio file.
///
/// Output: ~1 000 samples per band stored in <see cref="AudioFeaturesEntity.WaveformBlob"/>.
/// Each sample is a byte (0–255) representing mean RMS for the time window.
/// Layout: [low_band₀…lowN | mid_band₀…midN | high_band₀…highN]
///
/// Band frequency ranges (post-FFT, half-spectrum):
///   Low  0 – 250 Hz   (kick, sub-bass)
///   Mid  250 – 4 000 Hz (melodic content, snare)
///   High 4 000+ Hz    (cymbals, hi-hats, air)
/// </summary>
public sealed class WaveformExtractionService
{
    /// <summary>Target sample count per band.</summary>
    public const int TargetSamples = 1000;

    private readonly ILogger<WaveformExtractionService> _logger;

    public WaveformExtractionService(ILogger<WaveformExtractionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads the PCM WAV file produced by <see cref="AudioIngestionPipeline"/> and
    /// writes the waveform blob into <paramref name="target"/>.
    /// </summary>
    public async Task ExtractAsync(
        string decodedWavPath,
        AudioFeaturesEntity target,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(decodedWavPath) || !File.Exists(decodedWavPath))
        {
            _logger.LogWarning("[WaveformExtraction] WAV file not found: {Path}", decodedWavPath);
            return;
        }

        try
        {
            var (low, mid, high) = await Task.Run(
                () => ExtractBands(decodedWavPath, ct), ct).ConfigureAwait(false);

            // Pack three byte arrays into one contiguous blob
            var blob = new byte[TargetSamples * 3];
            Buffer.BlockCopy(low,  0, blob, 0,             TargetSamples);
            Buffer.BlockCopy(mid,  0, blob, TargetSamples, TargetSamples);
            Buffer.BlockCopy(high, 0, blob, TargetSamples * 2, TargetSamples);

            target.WaveformBlob = blob;
            target.WaveformBlobSampleCount = TargetSamples;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[WaveformExtraction] Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WaveformExtraction] Failed for {Path}", decodedWavPath);
        }
    }

    // ── Band extraction ────────────────────────────────────────────────────

    private static (byte[] Low, byte[] Mid, byte[] High) ExtractBands(
        string wavPath, CancellationToken ct)
    {
        using var reader = new AudioFileReader(wavPath);

        int sampleRate  = reader.WaveFormat.SampleRate;
        int channels    = reader.WaveFormat.Channels;
        long totalFrames = reader.Length / (reader.WaveFormat.BitsPerSample / 8) / channels;

        if (totalFrames <= 0)
            return (new byte[TargetSamples], new byte[TargetSamples], new byte[TargetSamples]);

        // One FFT frame every (totalFrames / TargetSamples) samples
        int framesPerBucket = (int)Math.Max(1, totalFrames / TargetSamples);
        int fftSize = NextPow2(Math.Min(framesPerBucket, 4096));

        var low  = new byte[TargetSamples];
        var mid  = new byte[TargetSamples];
        var high = new byte[TargetSamples];

        var pcmBuffer = new float[fftSize * channels];
        var mono      = new float[fftSize];
        var spectrum  = new float[fftSize / 2];

        // Frequency bin boundaries
        float binHz = (float)sampleRate / fftSize;
        int lowEnd  = FreqBin(250f,  binHz, spectrum.Length);
        int midEnd  = FreqBin(4000f, binHz, spectrum.Length);

        for (int bucket = 0; bucket < TargetSamples; bucket++)
        {
            ct.ThrowIfCancellationRequested();

            int read = reader.Read(pcmBuffer, 0, pcmBuffer.Length);
            if (read == 0) break;

            // Down-mix to mono
            int monoLen = read / channels;
            for (int i = 0; i < monoLen; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                    sum += pcmBuffer[i * channels + c];
                mono[i] = sum / channels;
            }

            // Simple magnitude spectrum via DFT (Goertzel-style for speed on small windows)
            ComputeSpectrum(mono, monoLen, spectrum);

            low[bucket]  = RmsToByte(BandRms(spectrum, 0,      lowEnd));
            mid[bucket]  = RmsToByte(BandRms(spectrum, lowEnd, midEnd));
            high[bucket] = RmsToByte(BandRms(spectrum, midEnd, spectrum.Length));
        }

        return (low, mid, high);
    }

    private static void ComputeSpectrum(float[] mono, int len, float[] spectrum)
    {
        int n = Math.Min(len, spectrum.Length * 2);
        // Half-spectrum DFT magnitude; adequate for RMS-per-band visualisation
        for (int k = 0; k < spectrum.Length; k++)
        {
            double re = 0, im = 0;
            double theta = -2.0 * Math.PI * k / n;
            for (int t = 0; t < n; t++)
            {
                re += mono[t] * Math.Cos(theta * t);
                im += mono[t] * Math.Sin(theta * t);
            }
            spectrum[k] = (float)Math.Sqrt(re * re + im * im) / n;
        }
    }

    private static float BandRms(float[] spectrum, int from, int to)
    {
        if (from >= to) return 0f;
        float sum = 0f;
        for (int i = from; i < to; i++) sum += spectrum[i] * spectrum[i];
        return MathF.Sqrt(sum / (to - from));
    }

    private static byte RmsToByte(float rms) =>
        (byte)Math.Clamp((int)(rms * 2048f), 0, 255);

    private static int FreqBin(float hz, float binHz, int maxBin) =>
        Math.Clamp((int)(hz / binHz), 0, maxBin);

    private static int NextPow2(int v)
    {
        if (v <= 1) return 1;
        v--;
        v |= v >> 1; v |= v >> 2; v |= v >> 4;
        v |= v >> 8; v |= v >> 16;
        return v + 1;
    }
}
