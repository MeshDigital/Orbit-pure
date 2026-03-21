using System;

namespace SLSKDONET.Models;

/// <summary>
/// Enum for throttle detection status
/// </summary>
public enum ThrottleStatus
{
    /// <summary>Network is healthy, no throttle detected</summary>
    None,
    
    /// <summary>Suspected throttle: >80% of recent searches return 0 results</summary>
    Suspected,
    
    /// <summary>Confirmed throttle: >95% of searches return 0 results for extended period (5+ min)</summary>
    Confirmed
}

/// <summary>
/// Enum for IP ban detection status
/// </summary>
public enum BanStatus
{
    /// <summary>Network is healthy, no ban detected</summary>
    None,
    
    /// <summary>Suspected ban: connection refused 2+ times in rapid succession</summary>
    Suspected,
    
    /// <summary>Confirmed ban: connection refused consistently with indication from server</summary>
    Confirmed
}

/// <summary>
/// Enum for connection failure status
/// </summary>
public enum ConnectionFailureStatus
{
    /// <summary>Connection is healthy</summary>
    Healthy,
    
    /// <summary>Login timeout or authentication failure</summary>
    AuthenticationTimeout,

    /// <summary>Login was explicitly rejected by the server (e.g. bad credentials)</summary>
    LoginRejected,
    
    /// <summary>Connection refused by server</summary>
    ConnectionRefused,
    
    /// <summary>Network timeout (no response from server)</summary>
    NetworkTimeout,
    
    /// <summary>Unexpected disconnection</summary>
    UnexpectedDisconnection,
    
    /// <summary>Other connection error</summary>
    Other
}

/// <summary>
/// Real-time network health diagnostic signal
/// </summary>
public sealed record NetworkHealthSignal(
    /// <summary>Is successfully logged in and connected</summary>
    bool IsConnected,
    
    /// <summary>Current Soulseek client state (e.g., "Connected", "LoggingIn", "Disconnected")</summary>
    string ConnectionState,
    
    /// <summary>Most recent connection failure reason</summary>
    ConnectionFailureStatus LastFailureStatus,
    
    /// <summary>Exception message from most recent connection attempt, if any</summary>
    string? LastFailureMessage,
    
    /// <summary>Number of connection timeouts in the last 5 minutes</summary>
    int RecentTimeoutCount,
    
    /// <summary>Number of connection refused errors in the last 5 minutes</summary>
    int RecentConnectionRefusedCount,
    
    /// <summary>Number of searches that returned 0 results in recent window</summary>
    int ZeroResultSearchCount,
    
    /// <summary>Total searches in the recent evaluation window</summary>
    int TotalSearchCount,
    
    /// <summary>Percentage of recent searches that returned 0 results (0-100)</summary>
    int ZeroResultPercentage,
    
    /// <summary>Number of successful searches (non-zero results) in recent window</summary>
    int SuccessfulSearchCount,
    
    /// <summary>Timestamp of the last successful search that returned results</summary>
    DateTime? LastSuccessfulSearch,
    
    /// <summary>Time elapsed since last successful search</summary>
    TimeSpan? TimeSinceLastSuccess,
    
    /// <summary>Throttle detection status</summary>
    ThrottleStatus ThrottleStatus,
    
    /// <summary>Ban detection status</summary>
    BanStatus BanStatus,
    
    /// <summary>Combined diagnostic: is network in a working state?</summary>
    bool IsHealthy,
    
    /// <summary>Diagnostic message explaining current health state</summary>
    string DiagnosticMessage
)
{
    /// <summary>
    /// Is the network suspected or confirmed throttled?
    /// </summary>
    public bool IsThrottled => ThrottleStatus != ThrottleStatus.None;
    
    /// <summary>
    /// Is the network suspected or confirmed banned?
    /// </summary>
    public bool IsBanned => BanStatus != BanStatus.None;
    
    /// <summary>
    /// Is the network in any kind of degraded state?
    /// </summary>
    public bool IsDegraded => !IsHealthy || IsThrottled || IsBanned || RecentTimeoutCount > 2;
}

/// <summary>
/// Historical data point for network health analysis
/// </summary>
public sealed record NetworkHealthDataPoint(
    /// <summary>When this sample was recorded</summary>
    DateTime TimestampUtc,
    
    /// <summary>Search query that was executed</summary>
    string Query,
    
    /// <summary>Number of results returned by peers (before filtering)</summary>
    int RawResultCount,
    
    /// <summary>Number of results accepted after filtering</summary>
    int AcceptedResultCount,
    
    /// <summary>False if connection dropped during search</summary>
    bool SearchCompleted,
    
    /// <summary>Exception if search failed</summary>
    string? ErrorMessage
);
