namespace SLSKDONET.Models;

/// <summary>Well-known values for <see cref="Data.Entities.EngineDiagnosticEventEntity.EventType"/>.</summary>
public static class EngineDiagnosticEventType
{
    /// <summary>One line of a pasted tracklist and what it became (kept/dropped/why).</summary>
    public const string ImportLine = "ImportLine";
    /// <summary>Tracks were committed to a playlist (drag-drop, Smart Insert, paste-import, Flow Builder, batch add).</summary>
    public const string TracksAddedToPlaylist = "TracksAddedToPlaylist";
    /// <summary>A search query was dispatched to Soulseek.</summary>
    public const string SearchDispatched = "SearchDispatched";
    /// <summary>One candidate result was evaluated (accepted/rejected/evaluated) for a search.</summary>
    public const string SearchCandidateEvaluated = "SearchCandidateEvaluated";
    /// <summary>A search reached a final outcome — matched, or no match and why.</summary>
    public const string SearchResolved = "SearchResolved";
}
