namespace SLSKDONET.Models;

/// <summary>
/// Different download modes.
/// </summary>
public enum DownloadMode
{
    /// <summary>
    /// Download individual files (default).
    /// </summary>
    Normal,

    /// <summary>
    /// Download entire folder/album.
    /// </summary>
    Album,

    /// <summary>
    /// Find and download all distinct songs by artist/album.
    /// </summary>
    Aggregate,

    /// <summary>
    /// Combine Album and Aggregate modes.
    /// </summary>
    AlbumAggregate
}

/// <summary>
/// Represents different input source types.
/// </summary>
public enum InputType
{
    String,     // Direct search query
    CSV,        // CSV file path
    Spotify,    // Spotify URL
    YouTube,    // YouTube playlist URL
    Bandcamp,   // Bandcamp URL
    List,       // List file with multiple entries
    File        // Local file path
}

/// <summary>
/// Input source type for playlist/batch imports.
/// </summary>
public enum InputSourceType
{
    Spotify,
    CSV,
    YouTube,
    Bandcamp,
    Local
}

/// <summary>
/// Represents a search query with properties.
/// </summary>
public class SearchQuery
{
    /// <summary>
    /// Link back to the parent PlaylistJob (if this query came from an imported playlist).
    /// </summary>
    public PlaylistJob? SourceJob { get; set; }

    /// <summary>
    /// Reference to the track's hash in the parent PlaylistJob for status tracking.
    /// </summary>
    public string TrackHash { get; set; } = string.Empty;

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    /// <summary>
    /// Name of the originating source (e.g., Spotify playlist name).
    /// </summary>
    public string? SourceTitle { get; set; }

    /// <summary>
    /// Total number of tracks contained in the source list/playlist.
    /// </summary>
    public int TotalTracks { get; set; }
    public int? Length { get; set; } // in seconds
    public bool ArtistMaybeWrong { get; set; }
    public int? AlbumTrackCount { get; set; }
    public DownloadMode DownloadMode { get; set; } = DownloadMode.Normal;
    
    // Spotify Metadata (Phase 0: Metadata Gravity Well)
    public string? SpotifyTrackId { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; }
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; }
    public DateTime? ReleaseDate { get; set; }
    
    // Phase 1: Enrichment
    public bool IsEnriched { get; set; } = false;
    public bool ForceRefresh { get; set; } = false;
    public string? ISRC { get; set; }

    /// <summary>
    /// Parses a search string into a SearchQuery.
    /// Supports formats like:
    /// - "Artist - Title" (shorthand)
    /// - "title=Song,artist=Artist,length=180" (properties)
    /// - "Just a search string" (literal query)
    /// </summary>
    public static SearchQuery Parse(string input, DownloadMode mode = DownloadMode.Normal)
    {
        var query = new SearchQuery { DownloadMode = mode };

        // Check if it's property format: "title=X, artist=Y"
        if (input.Contains("="))
        {
            var parts = input.Split(',', StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;

                var key = kv[0].Trim().ToLower();
                var value = kv[1].Trim();

                switch (key)
                {
                    case "title":
                        query.Title = value;
                        break;
                    case "artist":
                        query.Artist = value;
                        break;
                    case "album":
                        query.Album = value;
                        break;
                    case "length":
                        if (int.TryParse(value, out var length))
                            query.Length = length;
                        break;
                    case "artist-maybe-wrong":
                        query.ArtistMaybeWrong = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "album-track-count":
                        if (int.TryParse(value, out var count))
                            query.AlbumTrackCount = count;
                        break;
                }
            }
        }
        // Check for shorthand: "Artist - Title"
        else if (input.Contains(" - "))
        {
            var parts = input.Split(" - ", 2);
            query.Artist = parts[0].Trim();
            query.Title = parts[1].Trim();
        }
        // Literal search string
        else
        {
            query.Title = input;
        }

        return query;
    }

    /// <summary>
    /// Converts query to a simple search string.
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Artist))
            parts.Add(Artist);
        if (!string.IsNullOrEmpty(Title))
            parts.Add(Title);
        if (!string.IsNullOrEmpty(Album))
            parts.Add(Album);

        return string.Join(" ", parts);
    }
}
