using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

/// <summary>
/// States in the deterministic connection lifecycle state machine.
/// </summary>
public enum ConnectionLifecycleState
{
    Disconnected,
    Connecting,
    LoggingIn,
    LoggedIn,
    CoolingDown,
    Disconnecting
}

/// <summary>
/// Authoritative owner of connection state and the auto-reconnect policy.
/// All connect/disconnect calls should go through this service to guarantee
/// at-most-one active attempt and deterministic state transitions.
/// </summary>
public interface IConnectionLifecycleService
{
    /// <summary>Current lifecycle state (read-only snapshot; subscribe to the event bus for changes).</summary>
    ConnectionLifecycleState CurrentState { get; }

    /// <summary>When true, an unexpected disconnect triggers the auto-reconnect loop.</summary>
    bool AutoReconnectEnabled { get; set; }

    /// <summary>
    /// Request a connect+login. Ignored when already LoggedIn or when a connect
    /// attempt is already in flight. Blocked (returns immediately without retrying)
    /// when state is CoolingDown.
    /// </summary>
    Task RequestConnectAsync(
        string? password,
        string? correlationId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Request a graceful disconnect. Sets manual-disconnect flag to suppress
    /// the auto-reconnect loop.
    /// </summary>
    Task RequestDisconnectAsync(string reason, string? correlationId = null);

    /// <summary>
    /// Mark the next disconnect as intentional (called by UI Shutdown / manual
    /// Disconnect button) so the reconnect loop is not started.
    /// </summary>
    void NotifyManualDisconnect();
}
