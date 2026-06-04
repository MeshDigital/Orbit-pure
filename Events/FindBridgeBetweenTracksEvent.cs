namespace SLSKDONET.Events;

/// <summary>
/// Event published when user requests to find bridge tracks between two specific tracks.
/// Triggered from Flow Builder or other playlist views.
/// </summary>
public sealed class FindBridgeBetweenTracksEvent
{
    /// <summary>Hash of the first track (the "from" or "previous" track).</summary>
    public string FromTrackHash { get; }

    /// <summary>Hash of the second track (the "to" or "next" track).</summary>
    public string ToTrackHash { get; }

    /// <summary>Optional: metadata about the first track for display purposes.</summary>
    public string? FromTrackTitle { get; }

    /// <summary>Optional: metadata about the second track for display purposes.</summary>
    public string? ToTrackTitle { get; }

    public FindBridgeBetweenTracksEvent(
        string fromTrackHash,
        string toTrackHash,
        string? fromTrackTitle = null,
        string? toTrackTitle = null)
    {
        FromTrackHash = fromTrackHash;
        ToTrackHash = toTrackHash;
        FromTrackTitle = fromTrackTitle;
        ToTrackTitle = toTrackTitle;
    }
}
