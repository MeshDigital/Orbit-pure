using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Service for monitoring and diagnosing network health (throttle/ban/connection issues)
/// </summary>
public interface INetworkHealthService
{
    /// <summary>
    /// Get the current network health signal
    /// </summary>
    NetworkHealthSignal GetCurrentHealth();
    
    /// <summary>
    /// Record a search attempt with results
    /// </summary>
    void RecordSearch(string query, int rawResultCount, int acceptedResultCount, bool searchCompleted, string? errorMessage = null);
    
    /// <summary>
    /// Record a connection state change
    /// </summary>
    void RecordConnectionStateChange(string newState);
    
    /// <summary>
    /// Record a connection failure
    /// </summary>
    void RecordConnectionFailure(ConnectionFailureStatus status, string? message = null);
    
    /// <summary>
    /// Reset all diagnostics (typically called on successful login)
    /// </summary>
    void ResetDiagnostics();
    
    /// <summary>
    /// Get detailed history for debugging
    /// </summary>
    IReadOnlyList<NetworkHealthDataPoint> GetRecentHistory(int maxEntries = 100);

    /// <summary>
    /// Record aggregate search filtering statistics.
    /// </summary>
    void RecordSearchFiltering(
        int filteredByFormat,
        int filteredByBitrate,
        int filteredBySampleRate,
        int filteredByQueue,
        int filteredByDedup,
        int filteredByExcludedPhrase);

    /// <summary>
    /// Record a server kick event.
    /// </summary>
    void RecordConnectionKick(string? message = null);

    /// <summary>
    /// Record a search query blocked by excluded phrase policy before dispatch.
    /// </summary>
    void RecordExcludedPhraseQueryBlock();

    /// <summary>
    /// Get aggregate reliability counters.
    /// </summary>
    NetworkReliabilityCounters GetReliabilityCounters();

    /// <summary>
    /// Record the terminal outcome of a single transfer attempt.
    /// Pass null reason to indicate a successful completion.
    /// </summary>
    void RecordTransferOutcome(DownloadFailureReason? reason);

    /// <summary>
    /// Get aggregate transfer outcome counters.
    /// </summary>
    NetworkTransferCounters GetTransferCounters();
}
