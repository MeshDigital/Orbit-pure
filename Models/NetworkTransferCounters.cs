namespace SLSKDONET.Models;

/// <summary>
/// Aggregate counters for transfer outcomes by terminal state.
/// Covers E1 of the Connection/Search Hardening plan: transfer outcomes by state.
/// </summary>
public sealed record NetworkTransferCounters(
    int Succeeded,
    int RemoteQueueDenied,
    int RemoteAccessDenied,
    int NetworkError,
    int Timeout,
    int PeerRejected,
    int Cancelled,
    int OtherFailure
)
{
    /// <summary>Total terminal outcomes recorded.</summary>
    public int Total => Succeeded + RemoteQueueDenied + RemoteAccessDenied
                        + NetworkError + Timeout + PeerRejected + Cancelled + OtherFailure;

    /// <summary>Total failed outcomes (all non-Succeeded terminal states).</summary>
    public int TotalFailed => Total - Succeeded;
}
