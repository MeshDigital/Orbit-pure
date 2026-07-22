using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NWaves.Transforms;
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
    /// <summary>
    /// Baseline sample count per band. Kept as a fixed constant for callers that need a fixed-size
    /// buffer up front (e.g. the Workstation stem waveform preview) — NOT used for the main track
    /// waveform blob any more, see <see cref="ComputeSampleCount"/>.
    /// </summary>
    public const int TargetSamples = 1000;

    // A fixed 1,000-sample budget meant an 8-minute track got ~480ms per sample (visibly blockier
    // than a 3-minute track at ~180ms/sample) and CueForgeWaveformControl's nearest-neighbor pixel
    // mapping made that worse still, especially when zoomed in. Scaling resolution with duration
    // instead keeps detail roughly constant regardless of track length. Byte-per-sample storage
    // keeps this cheap even at the high end — 12,000 samples × 3 bands is 36KB, negligible in SQLite.
    private const double SamplesPerSecond = 8.0;
    private const int MinSamples = 2000;
    private const int MaxSamples = 12000;

    private readonly ILogger<WaveformExtractionService> _logger;

    public WaveformExtractionService(ILogger<WaveformExtractionService> logger)
    {
        _logger = logger;
    }

    private static int ComputeSampleCount(double durationSeconds)
    {
        if (durationSeconds <= 0) return TargetSamples;
        int scaled = (int)Math.Round(durationSeconds * SamplesPerSecond);
        return Math.Clamp(scaled, MinSamples, MaxSamples);
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
            var (low, mid, high, sampleCount) = await Task.Run(
                () => ExtractBands(decodedWavPath, ct), ct).ConfigureAwait(false);

            // Pack three byte arrays into one contiguous blob
            var blob = new byte[sampleCount * 3];
            Buffer.BlockCopy(low,  0, blob, 0,               sampleCount);
            Buffer.BlockCopy(mid,  0, blob, sampleCount,     sampleCount);
            Buffer.BlockCopy(high, 0, blob, sampleCount * 2, sampleCount);

            target.WaveformBlob = blob;
            target.WaveformBlobSampleCount = sampleCount;
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

    private static (byte[] Low, byte[] Mid, byte[] High, int SampleCount) ExtractBands(
        string wavPath, CancellationToken ct)
    {
        using var reader = new AudioFileReader(wavPath);

        int sampleRate  = reader.WaveFormat.SampleRate;
        int channels    = reader.WaveFormat.Channels;
        long totalFrames = reader.Length / (reader.WaveFormat.BitsPerSample / 8) / channels;

        int targetSamples = ComputeSampleCount(sampleRate > 0 ? (double)totalFrames / sampleRate : 0);

        if (totalFrames <= 0)
            return (new byte[targetSamples], new byte[targetSamples], new byte[targetSamples], targetSamples);

        // One FFT frame every (totalFrames / targetSamples) samples
        int framesPerBucket = (int)Math.Max(1, totalFrames / targetSamples);
        int fftSize = NextPow2(Math.Min(framesPerBucket, 4096));

        // Raw per-bucket RMS first, quantized to bytes only after normalizing each band against
        // its own observed peak (see below) — a fixed global multiplier either saturated
        // sustained-loud masters (typical of EDM/DnB, where sub-bass sits near-max almost
        // continuously) to near-255 everywhere, flattening the visual dynamic range exactly
        // where a drop should stand out, or under-used the range for quieter masters.
        var lowRaw  = new float[targetSamples];
        var midRaw  = new float[targetSamples];
        var highRaw = new float[targetSamples];

        var pcmBuffer = new float[fftSize * channels];
        var mono      = new float[fftSize];
        var spectrum  = new float[fftSize / 2];

        var fft = new RealFft(fftSize);
        var re  = new float[fftSize];
        var im  = new float[fftSize];

        // Frequency bin boundaries
        float binHz = (float)sampleRate / fftSize;
        int lowEnd  = FreqBin(250f,  binHz, spectrum.Length);
        int midEnd  = FreqBin(4000f, binHz, spectrum.Length);

        for (int bucket = 0; bucket < targetSamples; bucket++)
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

            // Zero-pad if this is a partial last block
            if (monoLen < fftSize)
            {
                Array.Clear(mono, monoLen, fftSize - monoLen);
            }

            // Simple magnitude spectrum via highly optimized NWaves RealFft
            ComputeSpectrum(fft, mono, re, im, spectrum, fftSize);

            lowRaw[bucket]  = BandRms(spectrum, 0,      lowEnd);
            midRaw[bucket]  = BandRms(spectrum, lowEnd, midEnd);
            highRaw[bucket] = BandRms(spectrum, midEnd, spectrum.Length);
        }

        var low  = NormalizeToBytes(lowRaw);
        var mid  = NormalizeToBytes(midRaw);
        var high = NormalizeToBytes(highRaw);

        return (low, mid, high, targetSamples);
    }

    /// <summary>
    /// Scales a band's raw RMS values so the loudest moment in THIS track maps to 255,
    /// instead of a fixed global multiplier that saturates or under-uses the byte range
    /// depending on how loud/quiet the track's mastering happens to be.
    /// </summary>
    private static byte[] NormalizeToBytes(float[] raw)
    {
        float peak = 0f;
        foreach (var v in raw) if (v > peak) peak = v;
        if (peak < 1e-6f) return new byte[raw.Length]; // silent band — all zero, no divide-by-near-zero blowup

        var bytes = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            bytes[i] = (byte)Math.Clamp((int)(raw[i] / peak * 255f), 0, 255);
        return bytes;
    }

    private static void ComputeSpectrum(RealFft fft, float[] mono, float[] re, float[] im, float[] spectrum, int n)
    {
        fft.Direct(mono, re, im);
        // Half-spectrum FFT magnitude; adequate for RMS-per-band visualisation
        for (int k = 0; k < spectrum.Length; k++)
        {
            spectrum[k] = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / n;
        }
    }

    private static float BandRms(float[] spectrum, int from, int to)
    {
        if (from >= to) return 0f;
        float sum = 0f;
        for (int i = from; i < to; i++) sum += spectrum[i] * spectrum[i];
        return MathF.Sqrt(sum / (to - from));
    }

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
