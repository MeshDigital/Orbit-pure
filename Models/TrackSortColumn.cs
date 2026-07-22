namespace SLSKDONET.Models;

/// <summary>
/// Which column the track grid is currently sorted by. Threaded from TrackListViewModel down
/// through the DB-backed paging queries (VirtualizedTrackCollection) and applied in-memory for
/// the smart-playlist path, so both track-list surfaces sort consistently.
/// </summary>
public enum TrackSortColumn
{
    /// <summary>Manual/import order (PlaylistTrackEntity.SortOrder) or most-recently-added
    /// (LibraryEntryEntity.AddedAt) — whatever each path's natural default order already was.</summary>
    Default,
    Artist,
    Title,
    Bpm,
    Duration
}
