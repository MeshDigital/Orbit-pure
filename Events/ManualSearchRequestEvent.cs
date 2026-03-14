using SLSKDONET.Models;

namespace SLSKDONET.Events;

public class ManualSearchRequestEvent
{
    public PlaylistTrack Track { get; }

    public ManualSearchRequestEvent(PlaylistTrack track)
    {
        Track = track;
    }
}
