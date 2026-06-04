using SLSKDONET.Models;

namespace SLSKDONET.Events;

/// <summary>
/// Request to insert a selected bridge candidate between two tracks in Flow Builder.
/// </summary>
public sealed class InsertBridgeTrackBetweenEvent
{
    public string FromTrackHash { get; }
    public string ToTrackHash { get; }
    public PlaylistTrack BridgeTrack { get; }

    public InsertBridgeTrackBetweenEvent(string fromTrackHash, string toTrackHash, PlaylistTrack bridgeTrack)
    {
        FromTrackHash = fromTrackHash;
        ToTrackHash = toTrackHash;
        BridgeTrack = bridgeTrack;
    }
}
