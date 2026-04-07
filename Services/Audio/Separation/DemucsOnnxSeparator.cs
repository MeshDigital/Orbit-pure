using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio.Separation;

/// <summary>
/// IStemSeparator implementation using the Demucs v4 "4-stem" ONNX model.
/// Produces four stems: Vocals, Drums, Bass, Other.
///
/// GPU acceleration via DirectML (Windows) with automatic CPU fallback.
/// Model: demucs-4s.onnx — MIT license.
/// </summary>
public sealed class DemucsOnnxSeparator : IStemSeparator
{
    private readonly DemucsModelManager _modelManager;

    public string Name => "Demucs v4 ONNX (4-stem)";

    public bool IsAvailable => _modelManager.IsAvailable;

    public string ModelTag => _modelManager.ModelTag;

    public DemucsOnnxSeparator() : this(new DemucsModelManager()) { }

    public DemucsOnnxSeparator(DemucsModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    /// <inheritdoc />
    public async Task<Dictionary<StemType, string>> SeparateAsync(
        string inputFilePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new FileNotFoundException(
                $"Demucs-4s ONNX model not found at '{_modelManager.ModelPath}'. " +
                $"Download from: {DemucsModelManager.ModelDownloadUrl}",
                _modelManager.ModelPath);

        return await Task.Run(
            () => SeparateInternal(inputFilePath, outputDirectory, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    // ──────────────────────────────────── core inference ──────────────────

    private Dictionary<StemType, string> SeparateInternal(
        string inputPath,
        string outputDir,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDir);

        // ── 1. Load and normalise audio ───────────────────────────────────
        float[] audio;
        int sampleRate, channels, totalFrames;

        using (var reader = new NAudio.Wave.AudioFileReader(inputPath))
        {
            sampleRate  = reader.WaveFormat.SampleRate;
            channels    = reader.WaveFormat.Channels;
            var buf     = new float[reader.Length / sizeof(float)];
            int read    = reader.Read(buf, 0, buf.Length);
            audio       = buf[..read];
            totalFrames = read / channels;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Convert to stereo interleaved [1, 2, totalFrames] (Demucs ONNX input shape)
        float[] stereo = ToStereoInterleaved(audio, totalFrames, channels);

        // Input tensor shape: [batch=1, channels=2, samples=totalFrames]
        var tensor = new DenseTensor<float>(new[] { 1, 2, totalFrames });
        for (int i = 0; i < totalFrames; i++)
        {
            tensor[0, 0, i] = stereo[i * 2];
            tensor[0, 1, i] = stereo[i * 2 + 1];
        }

        // ── 2. ONNX inference (DirectML → CPU fallback) ───────────────────
        using var sessionOptions = new SessionOptions();
        try   { sessionOptions.AppendExecutionProvider_DML(deviceId: 0); }
        catch { /* CPU fallback */ }

        cancellationToken.ThrowIfCancellationRequested();

        using var session = new InferenceSession(_modelManager.ModelPath, sessionOptions);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("mix", tensor)
        };

        using var results = session.Run(inputs);

        cancellationToken.ThrowIfCancellationRequested();

        // ── 3. Map output tensors → WAV files ─────────────────────────────
        // Demucs-4s output node names: "drums", "bass", "other", "vocals"
        // Each tensor shape: [1, 2, totalFrames]
        var outputMap = new Dictionary<string, StemType>(StringComparer.OrdinalIgnoreCase)
        {
            { "drums",  StemType.Drums  },
            { "bass",   StemType.Bass   },
            { "other",  StemType.Other  },
            { "vocals", StemType.Vocals },
        };

        var stemFiles = new Dictionary<StemType, string>();

        foreach (var result in results)
        {
            if (!outputMap.TryGetValue(result.Name, out var stemType)) continue;

            var outTensor  = result.AsTensor<float>();
            int outFrames  = outTensor.Dimensions[2];
            var buf        = new float[outFrames * 2];

            for (int i = 0; i < outFrames; i++)
            {
                buf[i * 2]     = outTensor[0, 0, i];
                buf[i * 2 + 1] = outTensor[0, 1, i];
            }

            string path = Path.Combine(outputDir, $"{stemType.ToString().ToLowerInvariant()}.wav");
            using var writer = new NAudio.Wave.WaveFileWriter(
                path, new NAudio.Wave.WaveFormat(sampleRate, 2));
            writer.WriteSamples(buf, 0, buf.Length);

            stemFiles[stemType] = path;
        }

        return stemFiles;
    }

    // ──────────────────────────────────── helpers ─────────────────────────

    /// <summary>Converts mono or existing stereo buffer to interleaved stereo float[].</summary>
    private static float[] ToStereoInterleaved(float[] audio, int frames, int channels)
    {
        if (channels == 2) return audio;

        // Mono → duplicate to both channels
        var stereo = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            stereo[i * 2]     = audio[i];
            stereo[i * 2 + 1] = audio[i];
        }
        return stereo;
    }
}
