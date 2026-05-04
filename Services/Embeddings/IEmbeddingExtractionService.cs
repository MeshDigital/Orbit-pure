using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services.Embeddings;

/// <summary>
/// Extracts and synchronises audio embedding vectors from Essentia analysis results
/// into <c>AudioAnalysisEntity.VectorEmbeddingJson</c> for use by <c>SimilarityIndex</c>.
/// </summary>
public interface IEmbeddingExtractionService
{
    /// <summary>
    /// Syncs the best available embedding for a single track from
    /// <c>AudioFeaturesEntity</c> into <c>AudioAnalysisEntity.VectorEmbeddingJson</c>.
    /// Returns <c>true</c> if an embedding was written, <c>false</c> if no source data exists.
    /// </summary>
    Task<bool> SyncEmbeddingAsync(string trackHash, CancellationToken ct = default);

    /// <summary>
    /// Parses an <see cref="EssentiaOutput"/> DTO and returns the best available
    /// float[] embedding — discogs-effnet activation array if present, otherwise
    /// a synthesised low-level feature vector.  Returns <c>null</c> if nothing useful
    /// can be extracted.
    /// </summary>
    float[]? ExtractFromEssentiaOutput(EssentiaOutput output);

    /// <summary>
    /// Builds a section-specific embedding from the best available track embedding plus
    /// local section dynamics. This is the reliability-first fallback used when full
    /// PCM-slice model inference is not available in the current pipeline.
    /// </summary>
    Task<float[]?> ExtractSectionEmbeddingAsync(
        AudioFeaturesEntity? features,
        PhraseType sectionType,
        double startSeconds,
        double endSeconds,
        IReadOnlyList<float>? localEnergyWindows = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enqueues a background job that batch-syncs embeddings for all library tracks
    /// that do not yet have a <c>VectorEmbeddingJson</c> row.
    /// Progress is reported through the <c>BackgroundJobQueue.JobProgressChanged</c> event.
    /// </summary>
    void ScheduleBatchSync();
}
