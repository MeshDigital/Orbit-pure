using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;

namespace SLSKDONET.Services.Similarity;

/// <summary>
/// Extracts DiscogsEffnet audio embeddings and built-in style predictions via ONNX Runtime
/// (DirectML). Run in-process rather than via ORBIT's bundled Essentia CLI binary because that
/// binary's TensorFlow-model layer was found to silently produce no output at all (confirmed
/// empirically — the profile.yaml's `tensorflow_models` directive is not recognized by the
/// stock `essentia_streaming_extractor_music` build ORBIT bundles).
///
/// Expected model: <c>Tools/Essentia/models/discogs-effnet-bsdynamic-1.onnx</c> (the officially
/// published ONNX export from essentia.upf.edu — the "bs64" filename referenced by earlier code
/// here does not exist as an ONNX file; "bsdynamic" is the correct, dynamic-batch-size variant).
///
/// Input:  mel-spectrogram patches, <see cref="EffnetMelSpectrogramExtractor"/> — NOT raw PCM
///         (an earlier version of this class assumed raw audio input; the model actually expects
///         pre-computed mel-spectrogram frames, confirmed against the model's published schema).
/// Outputs: "PartitionedCall:0" — 400-D Discogs-style predictions (sigmoid, multi-label)
///          "PartitionedCall:1" — 1280-D embedding (not 2048-D, corrected from the same
///          earlier incorrect assumption)
///
/// A full track produces multiple 128-frame patches; both outputs are mean-aggregated across
/// patches to a single track-level vector, the standard MIR convention for this kind of model.
///
/// When the model file is absent, all Extract calls return null and log a warning — the
/// application continues without embeddings (similarity search degrades gracefully).
/// </summary>
public sealed class DiscogsEffnetEmbeddingExtractor : IDisposable
{
    public const int EmbeddingDimension = 1280;
    public const int StyleClassCount = 400;

    private static readonly string DefaultModelRelativePath =
        Path.Combine("Tools", "Essentia", "models", "discogs-effnet-bsdynamic-1.onnx");

    private readonly string _modelPath;
    private readonly ILogger<DiscogsEffnetEmbeddingExtractor> _logger;

    private InferenceSession? _session;
    private bool _loadAttempted;
    private string? _modelTag;
    private bool _disposed;

    public DiscogsEffnetEmbeddingExtractor(
        ILogger<DiscogsEffnetEmbeddingExtractor> logger,
        string? modelPath = null)
    {
        _logger    = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelPath = modelPath ?? Path.Combine(AppContext.BaseDirectory, DefaultModelRelativePath);
    }

    /// <summary>True when the ONNX model file exists and was loaded successfully.</summary>
    public bool IsAvailable
    {
        get
        {
            EnsureSessionLoaded();
            return _session != null;
        }
    }

    /// <summary>
    /// Model version tag used for cache-invalidation, stored in
    /// <see cref="AudioFeaturesEntity.EmbeddingModelTag"/>. Format: "discogs-effnet-bsdynamic-1|{SHA256_8_HEX}".
    /// </summary>
    public string? ModelTag
    {
        get
        {
            EnsureSessionLoaded();
            return _modelTag;
        }
    }

    /// <summary>
    /// Extracts the track-level embedding and style-prediction vectors from mono 16 kHz audio.
    /// Returns <c>null</c> when the model is unavailable or the audio is too short for even one
    /// mel-spectrogram patch (~2 seconds).
    /// </summary>
    public Task<(float[] Embedding, float[] StylePredictions)?> ExtractAsync(
        float[] monoAudioSamples16k,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(monoAudioSamples16k);

        EnsureSessionLoaded();
        if (_session == null) return Task.FromResult<(float[], float[])?>(null);

        ct.ThrowIfCancellationRequested();

        // ONNX inference is CPU/GPU-bound with no native async API; offload to a thread-pool
        // thread so async/await callers don't block on it.
        return Task.Run(() => RunInference(monoAudioSamples16k, ct), ct);
    }

    /// <summary>
    /// Populates <see cref="AudioFeaturesEntity.EmbeddingBlob"/> and
    /// <see cref="AudioFeaturesEntity.EmbeddingModelTag"/> in-place. Style predictions are
    /// returned separately for the caller to fuse into genre inference — they aren't stored on
    /// the entity directly (no dedicated column for them today).
    /// </summary>
    public async Task<float[]?> PopulateEntityAsync(
        AudioFeaturesEntity entity,
        float[] monoAudioSamples16k,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var result = await ExtractAsync(monoAudioSamples16k, ct).ConfigureAwait(false);
        if (result is null) return null;

        entity.Embedding         = result.Value.Embedding;
        entity.EmbeddingModelTag = _modelTag;
        return result.Value.StylePredictions;
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private (float[] Embedding, float[] StylePredictions)? RunInference(float[] audio16k, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var patches = EffnetMelSpectrogramExtractor.ExtractPatches(audio16k);
        if (patches.Count == 0)
        {
            _logger.LogDebug("[EmbeddingExtractor] Audio too short for a single mel-spectrogram patch — skipping");
            return null;
        }

        // Batch every patch into one inference call: input shape [patchCount, 128, 96].
        var tensor = new DenseTensor<float>(new[]
        {
            patches.Count,
            EffnetMelSpectrogramExtractor.PatchFrames,
            EffnetMelSpectrogramExtractor.MelBands,
        });

        for (int p = 0; p < patches.Count; p++)
            patches[p].AsSpan().CopyTo(tensor.Buffer.Span.Slice(p * patches[p].Length, patches[p].Length));

        // Note: the model's published JSON metadata documents these as "serving_default_melspectrogram" /
        // "PartitionedCall:0" / "PartitionedCall:1" (the original TensorFlow graph's node names) — the
        // actual ONNX export renames them to these simpler names, confirmed directly against the loaded
        // InferenceSession's real InputMetadata/OutputMetadata rather than trusting the JSON.
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("melspectrogram", tensor) };

        using var results = _session!.Run(inputs);
        ct.ThrowIfCancellationRequested();

        var predictionsTensor = results.First(r => r.Name == "activations").AsTensor<float>();
        var embeddingTensor   = results.First(r => r.Name == "embeddings").AsTensor<float>();

        var embedding    = MeanAcrossPatches(embeddingTensor, patches.Count, EmbeddingDimension);
        var predictions  = MeanAcrossPatches(predictionsTensor, patches.Count, StyleClassCount);

        return (embedding, predictions);
    }

    private static float[] MeanAcrossPatches(Tensor<float> tensor, int patchCount, int dim)
    {
        var mean = new float[dim];
        for (int p = 0; p < patchCount; p++)
            for (int d = 0; d < dim; d++)
                mean[d] += tensor[p, d];

        for (int d = 0; d < dim; d++)
            mean[d] /= patchCount;

        return mean;
    }

    private void EnsureSessionLoaded()
    {
        if (_loadAttempted) return;
        _loadAttempted = true;

        if (!File.Exists(_modelPath))
        {
            _logger.LogWarning(
                "[EmbeddingExtractor] DiscogsEffnet model not found at {Path}. " +
                "Similarity search and ONNX-based genre/mood classification will operate without it.",
                _modelPath);
            return;
        }

        try
        {
            var opts = new SessionOptions();
            opts.AppendExecutionProvider_DML();

            _session  = new InferenceSession(_modelPath, opts);
            _modelTag = BuildModelTag(_modelPath);

            _logger.LogInformation(
                "[EmbeddingExtractor] Loaded DiscogsEffnet model. Tag={Tag}", _modelTag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[EmbeddingExtractor] Failed to load ONNX model from {Path}. Embeddings disabled.",
                _modelPath);
        }
    }

    private static string BuildModelTag(string modelPath)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(modelPath);
        var hash = sha.ComputeHash(fs);
        var prefix = Convert.ToHexString(hash)[..8];
        var name   = Path.GetFileNameWithoutExtension(modelPath);
        return $"{name}|{prefix}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
    }
}
