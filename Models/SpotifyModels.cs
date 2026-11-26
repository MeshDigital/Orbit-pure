namespace SLSKDONET.Models;

public class SpotifyPlaylistViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public int TrackCount { get; set; }
    public string Owner { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>
    /// True when the signed-in user owns or collaborates on this playlist (Spotify's Feb 2026
    /// /playlists/{id}/items endpoint serves these directly; followed third-party playlists fall
    /// back to the public page).
    /// </summary>
    public bool IsOwnedOrCollaborative { get; set; }
    public string AccessBadge => IsOwnedOrCollaborative ? "Yours" : "Followed";
    public string Description => $"{TrackCount} tracks • by {Owner}";
}
