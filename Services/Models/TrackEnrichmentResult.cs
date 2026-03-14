namespace SLSKDONET.Services.Models;

public class TrackEnrichmentResult
{
    public string SpotifyId { get; set; } = string.Empty;
    public string OfficialArtist { get; set; } = string.Empty;
    public string OfficialTitle { get; set; } = string.Empty;
    public string AlbumArtUrl { get; set; } = string.Empty;
    public string SpotifyAlbumId { get; set; } = string.Empty;
    public string SpotifyArtistId { get; set; } = string.Empty;
    public string? ISRC { get; set; }
    public string? MusicBrainzId { get; set; }
    public int? CanonicalDuration { get; set; }
    
    // Audio Features
    public double? Bpm { get; set; }
    public double? Energy { get; set; }
    public double? Valence { get; set; }
    public double? Danceability { get; set; }
    public string? MusicalKey { get; set; }
    
    // Genre Support (Stage 3)
    public System.Collections.Generic.List<string>? Genres { get; set; }
    
    // Style Classification (Stage 4)
    public string? DetectedSubGenre { get; set; }
    public float? SubGenreConfidence { get; set; }

    public bool Success { get; set; }
    public string? Error { get; set; }
}
