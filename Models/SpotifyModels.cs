namespace SLSKDONET.Models;

public class SpotifyPlaylistViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public int TrackCount { get; set; }
    public string Owner { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description => $"{TrackCount} tracks • by {Owner}";
}
