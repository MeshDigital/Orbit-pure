using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Similarity;

/// <summary>
/// Extracts 2 048-dimensional Essentia DiscogsEffnet audio embeddings via ONNX Runtime (DirectML).
///
/// Expected model: <c>Tools/Essentia/models/discogs-effnet-bs64-1.onnx</c>
/// Input:  <c>"input"</c>  — float32 tensor [1, samples] (mono PCM at 16 kHz)
/// Output: <c>"output"</c> — float32 tensor [1, 2048]   (DiscogsEffnet activations)
///
/// When the model file is absent, all Extract calls return <c>null</c> and log a warning — the
/// application continues without embeddings (similarity search degrades gracefully).
/// </summary>
public sealed class DiscogsEffnetEmbeddingExtractor : IDisposable
{
    public const int EmbeddingDimension = 2048;

    // Relative path resolved against the application base directory at runtime.
    private static readonly string DefaultModelRelativePath =
        Path.Combine("Tools", "Essentia", "models", "discogs-effnet-bs64-1.onnx");

    private readonly string _modelPath;
    private readonly ILogger<DiscogsEffnetEmbeddingExtractor> _logger;

    // Lazily created; null when the model file is absent.
    private InferenceSession? _session;
    private bool _loadAttempted;
    private string? _modelTag;
    private bool _disposed;

    public DiscogsEffnetEmbeddingExtractor(
        ILogger<DiscogsEffnetEmbeddingExtractor> logger,
        string? modelPath = null)
    {
        _logger   = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelPath = modelPath
            ?? Path.Combine(AppContext.BaseDirectory, DefaultModelRelativePath);
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
    /// Model version tag used for cache-invalidation stored in
    /// <see cref="AudioFeaturesEntity.EmbeddingModelTag"/>.
    /// Format: <c>"discogs-effnet-bs64-1|{SHA256_8_HEX}"</c>.
    /// Returns <c>null</c> when the model is not available.
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
    /// Extracts a 2 048-D embedding from the supplied mono PCM audio samples.
    /// </summary>
    /// <param name="monoAudioSamples">Mono float[] at 16 kHz (any length).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Float array of length 2 048, or <c>null</c> when the model is unavailable.</returns>
    public Task<float[]?> ExtractAsync(
        float[] monoAudioSamples,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(monoAudioSamples);

        EnsureSessionLoaded();
        if (_session == null) return Task.FromResult<float[]?>(null);

        ct.ThrowIfCancellationRequested();

        // ONNX inference is CPU/GPU-bound but has no native async API; offload to a thread-pool
        // thread so that calling code using async/await is not blocked on the UI thread.
        return Task.Run(() => RunInference(monoAudioSamples, ct), ct);
    }

    /// <summary>
    /// Populates <see cref="AudioFeaturesEntity.EmbeddingBlob"/> and
    /// <see cref="AudioFeaturesEntity.EmbeddingModelTag"/> in-place.
    /// </summary>
    public async Task PopulateEntityAsync(
        AudioFeaturesEntity entity,
        float[] monoAudioSamples,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var embedding = await ExtractAsync(monoAudioSamples, ct).ConfigureAwait(false);
        if (embedding == null) return;

        // Zero-copy: reinterpret float[] as byte[] via MemoryMarshal
        entity.EmbeddingBlob     = MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray();
        entity.EmbeddingModelTag = _modelTag;
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private float[]? RunInference(float[] audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Build input tensor [1, samples]
        var tensor = new DenseTensor<float>(new[] { 1, audio.Length });
        audio.AsSpan().CopyTo(MemoryMarshal.Cast<float, float>(tensor.Buffer.Span));

        var inputs = new NamedOnnxValue[]
        {
            NamedOnnxValue.CreateFromTensor("input", tensor),
        };

        using var results = _session!.Run(inputs);

        ct.ThrowIfCancellationRequested();

        // Extract the first output tensor as float[]
        var output = results[0].AsEnumerable<float>();
        var embedding = new float[EmbeddingDimension];
        int i = 0;
        foreach (var v in output)
        {
            if (i >= EmbeddingDimension) break;
            embedding[i++] = v;
        }

        return embedding;
    }

    private void EnsureSessionLoaded()
    {
        if (_loadAttempted) return;
        _loadAttempted = true;

        if (!File.Exists(_modelPath))
        {
            _logger.LogWarning(
                "[EmbeddingExtractor] DiscogsEffnet model not found at {Path}. " +
                "Similarity search will operate without dense embeddings.",
                _modelPath);
            return;
        }

        try
        {
            var opts = new SessionOptions();
            // DirectML (GPU) preferred; falls back to CPU automatically when unavailable.
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
        // First 8 hex chars of the SHA-256 of the model file, for cache invalidation.
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
