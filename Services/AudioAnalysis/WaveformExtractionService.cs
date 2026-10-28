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
/// Extracts the shared compact tri-band waveform stored in <see cref="AudioFeaturesEntity.WaveformBlob"/>.
/// Every output bucket represents an exact, contiguous fraction of the source duration. Windowed
/// overlapping FFT frames cover the whole bucket, and all three bands use one loudness reference so
/// the colour balance and drop contrast reflect the audio instead of three unrelated normalizations.
/// Layout: [low_band₀…lowN | mid_band₀…midN | high_band₀…highN].
/// </summary>
public sealed class WaveformExtractionService
{
    /// <summary>Stored on completed analysis rows so older, incorrectly mapped blobs can be refreshed.</summary>
    public const string CurrentWaveformVersion = "2026.10.28-triband-v2";

    /// <summary>Legacy fixed-size buffer used by the Workstation stem waveform preview.</summary>
    public const int TargetSamples = 1000;

    private const double SamplesPerSecond = 8.0;
    private const int MinSamples = 256;
    private const int MaxSamples = 12000;
    private const int MaxFftSize = 2048;
    private const int MinFftSize = 32;

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

    /// <summary>Reads the decoded PCM WAV and writes a full-duration tri-band waveform.</summary>
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

            var blob = new byte[sampleCount * 3];
            Buffer.BlockCopy(low, 0, blob, 0, sampleCount);
            Buffer.BlockCopy(mid, 0, blob, sampleCount, sampleCount);
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

    private static (byte[] Low, byte[] Mid, byte[] High, int SampleCount) ExtractBands(
        string wavPath, CancellationToken ct)
    {
        using var reader = new AudioFileReader(wavPath);

        int sampleRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        long totalFrames = reader.Length / (reader.WaveFormat.BitsPerSample / 8) / channels;
        int targetSamples = ComputeSampleCount(sampleRate > 0 ? (double)totalFrames / sampleRate : 0);

        if (totalFrames <= 0 || channels <= 0)
            return (new byte[targetSamples], new byte[targetSamples], new byte[targetSamples], targetSamples);

        var lowRaw = new float[targetSamples];
        var midRaw = new float[targetSamples];
        var highRaw = new float[targetSamples];

        int maxBucketFrames = (int)Math.Min(int.MaxValue, (totalFrames + targetSamples - 1) / targetSamples);
        int fftSize = Math.Max(MinFftSize, NextPow2(Math.Min(maxBucketFrames, MaxFftSize)));
        int hopSize = fftSize / 2;
        var window = CreateHannWindow(fftSize);
        var fft = new RealFft(fftSize);
        var frame = new float[fftSize];
        var re = new float[fftSize];
        var im = new float[fftSize];
        var spectrum = new float[fftSize / 2];

        float binHz = (float)sampleRate / fftSize;
        int lowStart = FreqBin(20f, binHz, spectrum.Length);
        int lowEnd = Math.Max(lowStart + 1, FreqBin(250f, binHz, spectrum.Length));
        int midEnd = Math.Max(lowEnd + 1, FreqBin(4000f, binHz, spectrum.Length));
        lowEnd = Math.Min(lowEnd, spectrum.Length);
        midEnd = Math.Min(midEnd, spectrum.Length);

        var interleaved = new float[maxBucketFrames * channels];
        var mono = new float[maxBucketFrames];

        for (int bucket = 0; bucket < targetSamples; bucket++)
        {
            ct.ThrowIfCancellationRequested();

            // Integer boundaries distribute any remainder without gaps or overlaps. The previous
            // implementation read fftSize frames instead of a complete bucket, so later audio was
            // never analyzed and the resulting bytes were still stretched across the full track.
            long startFrame = (long)bucket * totalFrames / targetSamples;
            long endFrame = (long)(bucket + 1) * totalFrames / targetSamples;
            int requestedFrames = (int)Math.Max(0, endFrame - startFrame);
            int requestedValues = requestedFrames * channels;
            int valuesRead = ReadExactly(reader, interleaved, requestedValues);
            int framesRead = valuesRead / channels;

            for (int i = 0; i < framesRead; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
                mono[i] = sum / channels;
            }

            double lowPower = 0, midPower = 0, highPower = 0;
            double lowPeak = 0, midPeak = 0, highPeak = 0;
            int windows = 0;
            for (int offset = 0; offset < framesRead; offset += hopSize)
            {
                Array.Clear(frame, 0, frame.Length);
                int count = Math.Min(fftSize, framesRead - offset);
                for (int i = 0; i < count; i++) frame[i] = mono[offset + i] * window[i];

                ComputeSpectrum(fft, frame, re, im, spectrum);
                double lowBandPower = BandPower(spectrum, lowStart, lowEnd);
                double midBandPower = BandPower(spectrum, lowEnd, midEnd);
                double highBandPower = BandPower(spectrum, midEnd, spectrum.Length);
                lowPower += lowBandPower; midPower += midBandPower; highPower += highBandPower;
                lowPeak = Math.Max(lowPeak, lowBandPower);
                midPeak = Math.Max(midPeak, midBandPower);
                highPeak = Math.Max(highPeak, highBandPower);
                windows++;
            }

            if (windows > 0)
            {
                // Mostly sustained band energy, with a transient contribution so a short bass
                // return or kick remains visible inside a 125 ms display bucket.
                lowRaw[bucket] = (float)Math.Sqrt(0.7 * lowPower / windows + 0.3 * lowPeak);
                midRaw[bucket] = (float)Math.Sqrt(0.7 * midPower / windows + 0.3 * midPeak);
                highRaw[bucket] = (float)Math.Sqrt(0.7 * highPower / windows + 0.3 * highPeak);
            }
        }

        NormalizeToBytes(lowRaw, midRaw, highRaw, out var low, out var mid, out var high);
        return (low, mid, high, targetSamples);
    }

    private static int ReadExactly(AudioFileReader reader, float[] buffer, int requestedValues)
    {
        int total = 0;
        while (total < requestedValues)
        {
            int read = reader.Read(buffer, total, requestedValues - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static float[] CreateHannWindow(int size)
    {
        var window = new float[size];
        for (int i = 0; i < size; i++)
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
        return window;
    }

    /// <summary>
    /// Uses one robust reference for every band. Independent per-band peaks made the quietest hi-hat
    /// as bright as the loudest bass drop. A 98th-percentile reference rejects isolated spikes; the
    /// gentle 0.75 display gamma keeps breakdowns readable without flattening drop contrast.
    /// </summary>
    private static void NormalizeToBytes(
        float[] lowRaw,
        float[] midRaw,
        float[] highRaw,
        out byte[] low,
        out byte[] mid,
        out byte[] high)
    {
        low = new byte[lowRaw.Length];
        mid = new byte[midRaw.Length];
        high = new byte[highRaw.Length];

        var all = new float[lowRaw.Length * 3];
        Array.Copy(lowRaw, 0, all, 0, lowRaw.Length);
        Array.Copy(midRaw, 0, all, lowRaw.Length, midRaw.Length);
        Array.Copy(highRaw, 0, all, lowRaw.Length + midRaw.Length, highRaw.Length);
        Array.Sort(all);
        float reference = all[(int)Math.Floor((all.Length - 1) * 0.98)];
        if (reference < 1e-8f) return;

        for (int i = 0; i < lowRaw.Length; i++)
        {
            low[i] = Quantize(lowRaw[i], reference);
            mid[i] = Quantize(midRaw[i], reference);
            high[i] = Quantize(highRaw[i], reference);
        }
    }

    private static byte Quantize(float value, float reference) =>
        (byte)Math.Clamp((int)Math.Round(MathF.Pow(Math.Max(0, value) / reference, 0.75f) * 255f), 0, 255);

    private static void ComputeSpectrum(RealFft fft, float[] frame, float[] re, float[] im, float[] spectrum)
    {
        fft.Direct(frame, re, im);
        int n = frame.Length;
        for (int k = 0; k < spectrum.Length; k++)
            spectrum[k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]) / n;
    }

    private static double BandPower(float[] spectrum, int from, int to)
    {
        from = Math.Clamp(from, 0, spectrum.Length);
        to = Math.Clamp(to, from, spectrum.Length);
        double sum = 0;
        for (int i = from; i < to; i++) sum += spectrum[i] * spectrum[i];
        return sum;
    }

    private static int FreqBin(float hz, float binHz, int maxBin) =>
        Math.Clamp((int)(hz / binHz), 0, maxBin);

    private static int NextPow2(int value)
    {
        if (value <= 1) return 1;
        value--;
        value |= value >> 1; value |= value >> 2; value |= value >> 4;
        value |= value >> 8; value |= value >> 16;
        return value + 1;
    }
}
