using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Diagnostic service for network health monitoring
/// Detects throttling, banning, and connection issues
/// </summary>
public class NetworkHealthService : INetworkHealthService
{
    private readonly ILogger<NetworkHealthService> _logger;
    
    // Configuration thresholds
    private const int SuspectedThrottlePercentage = 80;  // >80% zero-result = suspected throttle
    private const int ConfirmedThrottlePercentage = 95;  // >95% zero-result = confirmed throttle
    private const int RecentWindowSeconds = 300;         // 5-minute window for analysis
    private const int ConfirmedThrottleDurationSeconds = 300;  // Must persist for 5 minutes
    
    // History tracking
    private readonly LinkedList<NetworkHealthDataPoint> _history = new();
    private readonly LinkedList<(DateTime, ConnectionFailureStatus, string?)> _connectionFailures = new();
    private string _currentConnectionState = "Disconnected";
    private ConnectionFailureStatus _lastFailureStatus = ConnectionFailureStatus.Healthy;
    private string? _lastFailureMessage;
    
    // Throttle confirmation tracking
    private DateTime? _throttleSuspectedSince;
    private DateTime? _banSuspectedSince;

    public NetworkHealthService(ILogger<NetworkHealthService> logger)
    {
        _logger = logger;
    }

    public void RecordSearch(string query, int rawResultCount, int acceptedResultCount, bool searchCompleted, string? errorMessage = null)
    {
        var point = new NetworkHealthDataPoint(
            TimestampUtc: DateTime.UtcNow,
            Query: query,
            RawResultCount: rawResultCount,
            AcceptedResultCount: acceptedResultCount,
            SearchCompleted: searchCompleted,
            ErrorMessage: errorMessage
        );
        
        _history.AddLast(point);
        
        // Keep only recent history
        CleanOldHistory();
        
        // If this search had results, reset throttle suspicion
        if (acceptedResultCount > 0)
        {
            _throttleSuspectedSince = null;
        }
    }

    public void RecordConnectionStateChange(string newState)
    {
        _currentConnectionState = newState;
        
        // If we successfully logged in, reset failure status
        if (newState.Contains("LoggedIn", StringComparison.OrdinalIgnoreCase) || 
            newState.Contains("Connected", StringComparison.OrdinalIgnoreCase))
        {
            _lastFailureStatus = ConnectionFailureStatus.Healthy;
            _lastFailureMessage = null;
            _banSuspectedSince = null;
            _throttleSuspectedSince = null;
        }
    }

    public void RecordConnectionFailure(ConnectionFailureStatus status, string? message = null)
    {
        _lastFailureStatus = status;
        _lastFailureMessage = message;
        
        _connectionFailures.AddLast((DateTime.UtcNow, status, message));
        CleanOldHistory();
        
        // Track ban suspicion
        if (status == ConnectionFailureStatus.ConnectionRefused)
        {
            _banSuspectedSince ??= DateTime.UtcNow;
        }
        
        // Track timeout (potential throttle indicator)
        if (status == ConnectionFailureStatus.AuthenticationTimeout)
        {
            _throttleSuspectedSince ??= DateTime.UtcNow;
        }
    }

    public void ResetDiagnostics()
    {
        _history.Clear();
        _connectionFailures.Clear();
        _lastFailureStatus = ConnectionFailureStatus.Healthy;
        _lastFailureMessage = null;
        _throttleSuspectedSince = null;
        _banSuspectedSince = null;
    }

    public NetworkHealthSignal GetCurrentHealth()
    {
        var now = DateTime.UtcNow;
        var recentWindow = now.AddSeconds(-RecentWindowSeconds);
        
        // Collect recent searches
        var recentSearches = _history
            .Where(h => h.TimestampUtc > recentWindow)
            .ToList();
        
        // Collect recent connection failures
        var recentFailures = _connectionFailures
            .Where(f => f.Item1 > recentWindow)
            .ToList();
        
        int totalSearches = recentSearches.Count;
        int zeroResultSearches = recentSearches.Count(s => s.AcceptedResultCount == 0);
        int successfulSearches = recentSearches.Count(s => s.AcceptedResultCount > 0);
        int zeroResultPercentage = totalSearches > 0 ? (zeroResultSearches * 100) / totalSearches : 0;
        
        var lastSuccess = recentSearches
            .Where(s => s.AcceptedResultCount > 0)
            .OrderByDescending(s => s.TimestampUtc)
            .FirstOrDefault();
        
        TimeSpan? timeSinceLastSuccess = lastSuccess != null 
            ? (TimeSpan?)(now - lastSuccess.TimestampUtc)
            : null;
        
        bool isConnected = _currentConnectionState.Contains("Connected", StringComparison.OrdinalIgnoreCase) &&
                          !_currentConnectionState.Contains("Disconnecting", StringComparison.OrdinalIgnoreCase) &&
                          _lastFailureStatus == ConnectionFailureStatus.Healthy;
        
        // Detect throttle status
        var throttleStatus = DetectThrottleStatus(now, zeroResultPercentage, totalSearches);
        
        // Detect ban status
        var banStatus = DetectBanStatus(now, recentFailures);
        
        int timeoutCount = recentFailures.Count(f => f.Item2 == ConnectionFailureStatus.AuthenticationTimeout);
        int refusedCount = recentFailures.Count(f => f.Item2 == ConnectionFailureStatus.ConnectionRefused);
        
        bool isHealthy = isConnected && 
                        throttleStatus == ThrottleStatus.None && 
                        banStatus == BanStatus.None && 
                        timeoutCount <= 2;
        
        string diagnosticMessage = BuildDiagnosticMessage(
            isConnected, isHealthy, throttleStatus, banStatus, 
            zeroResultPercentage, totalSearches, timeoutCount, refusedCount,
            timeSinceLastSuccess, _lastFailureMessage);
        
        return new NetworkHealthSignal(
            IsConnected: isConnected,
            ConnectionState: _currentConnectionState,
            LastFailureStatus: _lastFailureStatus,
            LastFailureMessage: _lastFailureMessage,
            RecentTimeoutCount: timeoutCount,
            RecentConnectionRefusedCount: refusedCount,
            ZeroResultSearchCount: zeroResultSearches,
            TotalSearchCount: totalSearches,
            ZeroResultPercentage: zeroResultPercentage,
            SuccessfulSearchCount: successfulSearches,
            LastSuccessfulSearch: lastSuccess?.TimestampUtc,
            TimeSinceLastSuccess: timeSinceLastSuccess,
            ThrottleStatus: throttleStatus,
            BanStatus: banStatus,
            IsHealthy: isHealthy,
            DiagnosticMessage: diagnosticMessage
        );
    }

    public IReadOnlyList<NetworkHealthDataPoint> GetRecentHistory(int maxEntries = 100)
    {
        return _history
            .OrderByDescending(h => h.TimestampUtc)
            .Take(maxEntries)
            .OrderBy(h => h.TimestampUtc)
            .ToList()
            .AsReadOnly();
    }

    // Private helpers
    
    private ThrottleStatus DetectThrottleStatus(DateTime now, int zeroResultPercentage, int totalSearches)
    {
        if (totalSearches < 3)
            return ThrottleStatus.None;  // Need minimum sample size
        
        // Check for suspected throttle
        if (zeroResultPercentage >= SuspectedThrottlePercentage)
        {
            _throttleSuspectedSince ??= now;
            
            // Check if suspected throttle has persisted long enough to be confirmed
            var suspectedDuration = now - _throttleSuspectedSince.Value;
            if (suspectedDuration.TotalSeconds >= ConfirmedThrottleDurationSeconds && 
                zeroResultPercentage >= ConfirmedThrottlePercentage)
            {
                return ThrottleStatus.Confirmed;
            }
            
            return ThrottleStatus.Suspected;
        }
        else
        {
            // Reset throttle suspicion if results improve
            _throttleSuspectedSince = null;
            return ThrottleStatus.None;
        }
    }

    private BanStatus DetectBanStatus(DateTime now, List<(DateTime, ConnectionFailureStatus, string?)> recentFailures)
    {
        int refusedCount = recentFailures.Count(f => f.Item2 == ConnectionFailureStatus.ConnectionRefused);
        
        if (refusedCount >= 2)
        {
            _banSuspectedSince ??= now;
            
            // Check if ban suspicion has persisted
            var suspectedDuration = now - _banSuspectedSince.Value;
            if (suspectedDuration.TotalSeconds >= 60 && refusedCount >= 3)
            {
                return BanStatus.Confirmed;
            }
            
            return BanStatus.Suspected;
        }
        else
        {
            _banSuspectedSince = null;
            return BanStatus.None;
        }
    }

    private string BuildDiagnosticMessage(
        bool isConnected, bool isHealthy, ThrottleStatus throttleStatus, BanStatus banStatus,
        int zeroResultPercentage, int totalSearches, int timeoutCount, int refusedCount,
        TimeSpan? timeSinceLastSuccess, string? lastFailureMessage)
    {
        if (!isConnected)
        {
            if (timeoutCount > 0)
                return $"❌ Connection timeout ({timeoutCount} in last 5min). Auth may be failing. Check credentials.";
            if (refusedCount > 0)
                return $"❌ Connection refused ({refusedCount} attempts). Server may reject this IP.";
            if (!string.IsNullOrEmpty(lastFailureMessage))
                return $"❌ Disconnected: {lastFailureMessage}";
            return "❌ Not connected to Soulseek";
        }
        
        if (banStatus == BanStatus.Confirmed)
            return "🚫 IP appears BANNED - connection refused repeatedly by server";
        
        if (banStatus == BanStatus.Suspected)
            return "⚠️ Suspected IP BAN - connection refused 2+ times. Monitor next attempts.";
        
        if (throttleStatus == ThrottleStatus.Confirmed)
            return $"🐢 Network THROTTLED - {zeroResultPercentage}% of searches return no results for 5+ minutes";
        
        if (throttleStatus == ThrottleStatus.Suspected)
            return $"⚠️ Suspected throttle - {zeroResultPercentage}% of recent searches ({totalSearches} total) return no results";
        
        if (timeoutCount > 2)
            return $"⚠️ Multiple timeouts ({timeoutCount}). Network may be congested or unstable.";
        
        if (timeSinceLastSuccess.HasValue && timeSinceLastSuccess.Value.TotalMinutes > 10)
            return $"⚠️ No successful searches in {timeSinceLastSuccess.Value.TotalMinutes:F0}+ minutes. Peers may be offline.";
        
        if (isHealthy && totalSearches > 0)
            return "✅ Network healthy - searches returning results";
        
        if (totalSearches == 0)
            return "⏳ No search activity yet - waiting for data...";
        
        return "✅ Connected";
    }

    private void CleanOldHistory()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-RecentWindowSeconds * 2);  // Keep 2x window for safety
        
        // Clean searches
        while (_history.First != null && _history.First.Value.TimestampUtc < cutoff)
        {
            _history.RemoveFirst();
        }
        
        // Clean failures
        while (_connectionFailures.First != null && _connectionFailures.First.Value.Item1 < cutoff)
        {
            _connectionFailures.RemoveFirst();
        }
    }
}
