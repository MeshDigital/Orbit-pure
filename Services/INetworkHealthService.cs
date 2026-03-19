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
}
