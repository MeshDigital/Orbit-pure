namespace SLSKDONET.Models;

/// <summary>
/// Playlist-level scheduling priority. Coarse tier that controls which playlist gets
/// download slots first. Track-level Priority within a playlist is unaffected.
///
/// Slot allocation order: Critical → High → Normal → Low
/// Low only receives slots when no higher-tier playlists have pending tracks.
/// </summary>
public enum PlaylistPriority
{
    Critical = 0, // Fills all available slots first — use for urgent/time-sensitive playlists
    High     = 1, // Gets slots immediately after Critical is saturated
    Normal   = 2, // Default; balanced alongside other Normal playlists (FIFO by AddedAt)
    Low      = 3, // Background tier; only scheduled when higher tiers are empty
}
