using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
public sealed class DemucsOnnxSeparator : IStemSeparator, IDisposable
{
    private const string InputNodeName = "mix";

    /// <summary>Chunk length used when the ONNX graph doesn't declare a fixed sample count
    /// (dynamic input shape). Matches the segment length Demucs models are typically trained/
    /// evaluated on, so quality should match the reference implementation's chunked mode.</summary>
    private const double DefaultSegmentSeconds = 10.0;

    /// <summary>Fraction of each chunk that overlaps its neighbours, stitched back together via
    /// weighted overlap-add. 25% matches the reference Demucs `apply_model` default.</summary>
    private const double OverlapRatio = 0.25;

    private static readonly IReadOnlyDictionary<string, StemType> OutputNodeToStemType =
        new Dictionary<string, StemType>(StringComparer.OrdinalIgnoreCase)
        {
            { "drums",  StemType.Drums  },
            { "bass",   StemType.Bass   },
            { "other",  StemType.Other  },
            { "vocals", StemType.Vocals },
        };

    private readonly DemucsModelManager _modelManager;
    private readonly ILogger<DemucsOnnxSeparator>? _logger;
    private readonly object _sessionLock = new();
    private InferenceSession? _session;

    public string Name => "Demucs v4 ONNX (4-stem)";

    public bool IsAvailable => _modelManager.IsAvailable;

    public string ModelTag => _modelManager.ModelTag;

    public DemucsOnnxSeparator() : this(new DemucsModelManager(), null) { }

    public DemucsOnnxSeparator(DemucsModelManager modelManager, ILogger<DemucsOnnxSeparator>? logger = null)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns the shared inference session, creating it on first use. The ONNX model
    /// (and its DirectML/CPU execution provider setup) is expensive to load — reusing
    /// one session across every separation call instead of recreating it per-track is
    /// the difference between paying that cost once per app run vs. once per track.
    /// <see cref="InferenceSession.Run"/> is safe to call concurrently once constructed,
    /// so a single shared instance is fine even with multiple analysis workers.
    /// </summary>
    private InferenceSession GetOrCreateSession()
    {
        if (_session != null) return _session;

        lock (_sessionLock)
        {
            if (_session != null) return _session;

            using var sessionOptions = new SessionOptions();
            try
            {
                sessionOptions.AppendExecutionProvider_DML(deviceId: 0);
                _logger?.LogInformation("Demucs ONNX: DirectML (GPU) execution provider enabled.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Demucs ONNX: DirectML unavailable, falling back to CPU. Stem separation will be slower.");
            }

            _session = new InferenceSession(_modelManager.ModelPath, sessionOptions);
            return _session;
        }
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

        // Convert to stereo interleaved (Demucs ONNX input shape is [1, 2, samples])
        float[] stereo = ToStereoInterleaved(audio, totalFrames, channels);

        // ── 2. Chunked ONNX inference with weighted overlap-add ───────────
        // Running the whole track through in one shot doesn't match how Demucs models are
        // trained/evaluated (fixed-length segments) and scales memory with track length.
        // Chunk into overlapping segments, run each through the model, and stitch the results
        // back together with a tapered window so chunk boundaries don't produce audible seams.
        var session = GetOrCreateSession();
        int segmentSamples = ResolveSegmentSamples(session, sampleRate, totalFrames);
        int overlapSamples = segmentSamples >= totalFrames ? 0 : Math.Max(1, (int)(segmentSamples * OverlapRatio));
        int strideSamples  = Math.Max(1, segmentSamples - overlapSamples);
        float[] window     = BuildOverlapWindow(segmentSamples);

        var stemOutputs = new Dictionary<StemType, float[]>();
        foreach (var stemType in OutputNodeToStemType.Values)
        {
            stemOutputs.TryAdd(stemType, new float[totalFrames * 2]);
        }
        var weightAccum = new float[totalFrames];

        _logger?.LogDebug(
            "Demucs ONNX: separating {Frames} samples in {SegmentSamples}-sample chunks (stride {Stride}, overlap {Overlap}).",
            totalFrames, segmentSamples, strideSamples, overlapSamples);

        for (int start = 0; start < totalFrames; start += strideSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkLen = Math.Min(segmentSamples, totalFrames - start);

            var chunkTensor = new DenseTensor<float>(new[] { 1, 2, segmentSamples });
            for (int i = 0; i < chunkLen; i++)
            {
                int srcIdx = (start + i) * 2;
                chunkTensor[0, 0, i] = stereo[srcIdx];
                chunkTensor[0, 1, i] = stereo[srcIdx + 1];
            }
            // Remaining [chunkLen, segmentSamples) — only on the final, shorter chunk — stays
            // zero-padded (DenseTensor default), trimmed back out when writing results below.

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(InputNodeName, chunkTensor) };
            using var results = session.Run(inputs);

            foreach (var result in results)
            {
                if (!OutputNodeToStemType.TryGetValue(result.Name, out var stemType)) continue;

                var outTensor = result.AsTensor<float>();
                var destBuf   = stemOutputs[stemType];

                for (int i = 0; i < chunkLen; i++)
                {
                    float w = window[i];
                    int destIdx = (start + i) * 2;
                    destBuf[destIdx]     += outTensor[0, 0, i] * w;
                    destBuf[destIdx + 1] += outTensor[0, 1, i] * w;
                }
            }

            for (int i = 0; i < chunkLen; i++)
            {
                weightAccum[start + i] += window[i];
            }

            if (start + segmentSamples >= totalFrames) break;
        }

        // Normalise: divide every sample by the total window weight applied to it, so
        // overlapping regions (summed from 2+ chunks) return to unity gain.
        foreach (var buf in stemOutputs.Values)
        {
            for (int frame = 0; frame < totalFrames; frame++)
            {
                float w = weightAccum[frame];
                if (w <= 0f) continue;
                buf[frame * 2]     /= w;
                buf[frame * 2 + 1] /= w;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 3. Write stems to WAV ──────────────────────────────────────────
        var stemFiles = new Dictionary<StemType, string>();
        foreach (var (stemType, buf) in stemOutputs)
        {
            string path = Path.Combine(outputDir, $"{stemType.ToString().ToLowerInvariant()}.wav");
            using var writer = new NAudio.Wave.WaveFileWriter(
                path, new NAudio.Wave.WaveFormat(sampleRate, 2));
            writer.WriteSamples(buf, 0, buf.Length);

            stemFiles[stemType] = path;
        }

        return stemFiles;
    }

    /// <summary>
    /// Determines the chunk length (in samples) to feed the model per inference call.
    /// If the ONNX graph declares a fixed (non-dynamic) sample dimension for "mix", that
    /// exact size must be used — the graph won't accept anything else. Otherwise falls back
    /// to <see cref="DefaultSegmentSeconds"/>, capped to the track length for short tracks
    /// (so a 30-second track doesn't get padded out to a full 10s+ chunk boundary needlessly).
    /// </summary>
    internal static int ResolveSegmentSamples(InferenceSession session, int sampleRate, int totalFrames)
    {
        session.InputMetadata.TryGetValue(InputNodeName, out var meta);
        return ResolveSegmentSamplesCore(meta?.Dimensions, sampleRate, totalFrames);
    }

    /// <summary>ONNX-Runtime-free core of <see cref="ResolveSegmentSamples"/>, split out so the
    /// decision logic is unit-testable without needing a real loaded model.</summary>
    internal static int ResolveSegmentSamplesCore(int[]? declaredDimensions, int sampleRate, int totalFrames)
    {
        if (declaredDimensions is { Length: 3 } dims && dims[2] > 0)
        {
            return dims[2];
        }

        int defaultSegment = Math.Max(1, (int)(DefaultSegmentSeconds * sampleRate));
        return Math.Max(1, Math.Min(defaultSegment, Math.Max(totalFrames, 1)));
    }

    /// <summary>
    /// Raised-cosine (Hann) taper used to weight each chunk's contribution during overlap-add,
    /// floored well above zero so the final per-sample normalisation never divides by ~0 at the
    /// very start/end of the track (where only one chunk contributes).
    /// </summary>
    internal static float[] BuildOverlapWindow(int length)
    {
        var window = new float[length];
        if (length <= 1)
        {
            if (length == 1) window[0] = 1f;
            return window;
        }

        for (int i = 0; i < length; i++)
        {
            double raisedCosine = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (length - 1));
            window[i] = (float)Math.Max(raisedCosine, 0.01);
        }

        return window;
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

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
