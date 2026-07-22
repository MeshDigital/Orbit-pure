namespace SLSKDONET.Events;

/// <summary>
/// Event published when the user requests similar tracks for a single track (e.g. the
/// "Find Similar" button on a library row). Opens the Similar Tracks sidebar panel and seeds
/// it with this track, reusing the same real similarity engine as the Bridge Finder.
/// </summary>
public sealed class FindSimilarTrackRequestEvent
{
    public string TrackHash { get; }

    /// <summary>Optional: "Artist - Title" for display/logging purposes.</summary>
    public string? TrackLabel { get; }

    public FindSimilarTrackRequestEvent(string trackHash, string? trackLabel = null)
    {
        TrackHash = trackHash;
        TrackLabel = trackLabel;
    }
}
