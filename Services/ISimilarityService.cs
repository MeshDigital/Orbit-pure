using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.Services;

public interface ISimilarityService
{
    int IndexSize { get; }

    Task<IReadOnlyList<SimilarTrack>> GetSimilarTracksAsync(
        string queryHash,
        int topN = 10,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? excludeHashes = null);

    void InvalidateIndex();
}
