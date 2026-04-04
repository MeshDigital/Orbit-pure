using System.Threading;
using System.Threading.Tasks;
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
    /// Enqueues a background job that batch-syncs embeddings for all library tracks
    /// that do not yet have a <c>VectorEmbeddingJson</c> row.
    /// Progress is reported through the <c>BackgroundJobQueue.JobProgressChanged</c> event.
    /// </summary>
    void ScheduleBatchSync();
}
