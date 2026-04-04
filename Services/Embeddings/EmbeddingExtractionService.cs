using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Services.Jobs;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.Services.Embeddings;

/// <summary>
/// Extracts audio embedding vectors from Essentia analysis output and persists them
/// to <see cref="AudioAnalysisEntity.VectorEmbeddingJson"/> so that
/// <see cref="SimilarityIndex"/> can power track-to-track similarity search.
///
/// Embedding source priority (per track):
///   1. 512-D discogs-effnet deep texture (<see cref="AudioFeaturesEntity.DeepTextureEmbeddingBytes"/>)
///   2. 128-D general vector            (<see cref="AudioFeaturesEntity.VectorEmbeddingBytes"/>)
///   3. Synthesised 8-D feature vector  (BPM, energy, arousal, valence, danceability, …)
///
/// A batch-sync job can be enqueued via <see cref="ScheduleBatchSyncAsync"/>, which
/// uses the injected <see cref="IBackgroundJobQueue"/> so heavy I/O never blocks the UI.
/// </summary>
public sealed class EmbeddingExtractionService : IEmbeddingExtractionService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SimilarityIndex _similarityIndex;
    private readonly IBackgroundJobQueue _jobQueue;
    private readonly ILogger<EmbeddingExtractionService> _logger;

    public EmbeddingExtractionService(
        IDbContextFactory<AppDbContext> dbFactory,
        SimilarityIndex similarityIndex,
        IBackgroundJobQueue jobQueue,
        ILogger<EmbeddingExtractionService> logger)
    {
        _dbFactory = dbFactory;
        _similarityIndex = similarityIndex;
        _jobQueue = jobQueue;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<bool> SyncEmbeddingAsync(string trackHash, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var features = await db.AudioFeatures
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.TrackUniqueHash == trackHash, ct);

        if (features == null)
        {
            _logger.LogDebug("[Embedding] No AudioFeatures row for {Hash} — skipping", trackHash);
            return false;
        }

        var embedding = PickBestEmbedding(features);
        if (embedding == null || embedding.Length == 0)
        {
            _logger.LogDebug("[Embedding] No usable embedding on {Hash}", trackHash);
            return false;
        }

        // Upsert into AudioAnalysis table
        var analysis = await db.AudioAnalysis
            .FirstOrDefaultAsync(a => a.TrackUniqueHash == trackHash, ct);

        if (analysis == null)
        {
            analysis = new AudioAnalysisEntity { TrackUniqueHash = trackHash };
            db.AudioAnalysis.Add(analysis);
        }

        analysis.VectorEmbedding = embedding;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("[Embedding] Synced {Dim}-D embedding for {Hash}", embedding.Length, trackHash);
        _similarityIndex.InvalidateIndex();
        return true;
    }

    /// <inheritdoc/>
    public float[]? ExtractFromEssentiaOutput(EssentiaOutput output)
    {
        // 1. Try to pull float array from HighLevel.ExtensionData
        //    Essentia's music_extractor may expose model embeddings under keys such as
        //    "discogs_effnet_embeddings" (array of floats) depending on the profile used.
        if (output.HighLevel?.ExtensionData != null)
        {
            foreach (var kvp in output.HighLevel.ExtensionData)
            {
                if (!IsEmbeddingKey(kvp.Key)) continue;

                var floats = TryParseFloatArray(kvp.Value);
                if (floats != null && floats.Length >= 16)
                {
                    _logger.LogDebug("[Embedding] Parsed {Dim}-D from Essentia highlevel.{Key}", floats.Length, kvp.Key);
                    return floats;
                }
            }
        }

        // 2. Synthesise a compact feature vector from low-level scalars that Essentia always provides
        return SynthesiseFromLowLevel(output);
    }

    /// <inheritdoc/>
    public void ScheduleBatchSync()
    {
        var job = new BackgroundJob
        {

            Description = "Sync audio embeddings → SimilarityIndex",
            Category = "Embeddings",
            Work = RunBatchSyncAsync
        };

        _jobQueue.Enqueue(job);
        _logger.LogInformation("[Embedding] Batch-sync job enqueued");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ──────────────────────────────────────────────────────────────────────

    private async Task RunBatchSyncAsync(IProgress<JobProgress> progress, CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var hashes = await db.AudioFeatures
            .AsNoTracking()
            .Select(f => f.TrackUniqueHash)
            .ToListAsync(ct);

        // Only process tracks that don't yet have a populated embedding
        var populated = await db.AudioAnalysis
            .AsNoTracking()
            .Where(a => a.VectorEmbeddingJson != null)
            .Select(a => a.TrackUniqueHash)
            .ToHashSetAsync(ct);

        var pending = hashes.Where(h => !populated.Contains(h)).ToList();

        _logger.LogInformation("[Embedding] Batch-sync: {Pending}/{Total} tracks need embedding", pending.Count, hashes.Count);

        int done = 0;
        int updated = 0;
        foreach (var hash in pending)
        {
            ct.ThrowIfCancellationRequested();

            bool result = await SyncEmbeddingAsync(hash, ct);
            if (result) updated++;
            done++;

            if (done % 50 == 0 || done == pending.Count)
            {
                progress.Report(new JobProgress
                {
                    JobId = jobId,
                    Description = $"Embedding sync: {done}/{pending.Count} ({updated} updated)",
                    Fraction = pending.Count == 0 ? 1.0 : (double)done / pending.Count
                });
            }
        }

        progress.Report(new JobProgress
        {
            JobId = jobId,
            Description = $"Embedding sync complete — {updated} embeddings synced",
            Fraction = 1.0,
            IsCompleted = true
        });

        if (updated > 0)
            _similarityIndex.InvalidateIndex();
    }

    /// <summary>
    /// Returns the richest available float[] embedding from an <see cref="AudioFeaturesEntity"/>.
    /// </summary>
    private static float[]? PickBestEmbedding(AudioFeaturesEntity f)
    {
        // 512-D discogs-effnet (highest quality)
        var deep = f.DeepTextureEmbedding;
        if (deep is { Length: > 0 }) return deep;

        // 128-D general vector
        var vec = f.VectorEmbedding;
        if (vec is { Length: > 0 }) return vec;

        // 8-D synthesised feature vector (last resort — enough for rough similarity)
        return SynthesisedVector(f);
    }

    /// <summary>
    /// Builds a normalised 8-dimensional feature vector from readily-available scalar fields.
    /// Sufficient for rough BPM/energy/mood similarity when no deep embedding exists.
    /// Dimensions: [bpm/200, energy, arousal/9, valence/9, danceability, instrumentalProb, bpmStability, intensity]
    /// </summary>
    private static float[] SynthesisedVector(AudioFeaturesEntity f) =>
    [
        Math.Clamp(f.Bpm / 200f, 0f, 1f),
        Math.Clamp(f.Energy, 0f, 1f),
        Math.Clamp(f.Arousal / 9f, 0f, 1f),
        Math.Clamp(f.Valence / 9f, 0f, 1f),
        Math.Clamp(f.Danceability, 0f, 1f),
        Math.Clamp(f.InstrumentalProbability, 0f, 1f),
        Math.Clamp(f.BpmStability, 0f, 1f),
        Math.Clamp(f.Intensity, 0f, 1f)
    ];

    private static bool IsEmbeddingKey(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.Contains("embedding") || lower.Contains("activations");
    }

    /// <summary>
    /// Tries to deserialise a JsonElement as a flat or nested float[].
    /// Handles both <c>[0.1, 0.2, …]</c> arrays and single-key objects like <c>{"embeddings": […]}</c>.
    /// </summary>
    private static float[]? TryParseFloatArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<float>(element.GetArrayLength());
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out float f))
                    list.Add(f);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            // Look for a single array-valued property named "embeddings" / "activations"
            foreach (var prop in element.EnumerateObject())
            {
                if (!IsEmbeddingKey(prop.Name)) continue;
                var result = TryParseFloatArray(prop.Value);
                if (result != null) return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds an 8-D vector from Essentia low-level scalars when no dedicated embedding is present.
    /// </summary>
    private static float[]? SynthesiseFromLowLevel(EssentiaOutput output)
    {
        var r = output.Rhythm;
        var l = output.LowLevel;
        if (r == null && l == null) return null;

        return
        [
            r == null ? 0.6f : Math.Clamp(r.Bpm / 200f, 0f, 1f),
            l == null ? 0.5f : Math.Clamp(l.AverageLoudness, 0f, 1f),
            r == null ? 0.5f : Math.Clamp(r.Danceability, 0f, 1f),
            l == null ? 0.5f : Math.Clamp(l.DynamicComplexity / 8f, 0f, 1f),
            l?.SpectralCentroid == null ? 0.5f : Math.Clamp(l.SpectralCentroid.Mean / 8000f, 0f, 1f),
            l?.SpectralComplexity == null ? 0.5f : Math.Clamp(l.SpectralComplexity.Mean / 30f, 0f, 1f),
            r == null ? 0.5f : Math.Clamp(r.OnsetRate / 10f, 0f, 1f),
            l?.Rms == null ? 0.5f : Math.Clamp(l.Rms.Mean * 5f, 0f, 1f)
        ];
    }
}
