using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Audio;

public interface IEdmFormerService
{
    /// <summary>
    /// Returns true when the EDMFormer microservice is reachable and ready.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Runs EDMFormer phrase detection on an audio file.
    /// Returns null (and logs a warning) if the service is unavailable or the call fails.
    /// </summary>
    Task<IReadOnlyList<PhraseSegment>?> AnalyzeAsync(string audioFilePath, CancellationToken ct = default);

    /// <summary>
    /// Pings the service to refresh availability status.
    /// Called automatically on first use and every 60 s thereafter.
    /// </summary>
    Task RefreshAvailabilityAsync();
}
