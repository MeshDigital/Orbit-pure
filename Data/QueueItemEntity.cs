using System;

namespace SLSKDONET.Data;

/// <summary>
/// Entity for persisting the playback queue across app restarts.
/// Stores the order and track references for the player queue.
/// </summary>
public class QueueItemEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Reference to the PlaylistTrack in the queue
    /// </summary>
    public Guid PlaylistTrackId { get; set; }

    /// <summary>
    /// Position in the queue (0-based index)
    /// </summary>
    public int QueuePosition { get; set; }

    /// <summary>
    /// When this item was added to the queue
    /// </summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this is the currently playing track
    /// </summary>
    public bool IsCurrentTrack { get; set; }
}
