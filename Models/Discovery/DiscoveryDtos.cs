using System;

namespace SLSKDONET.Models.Discovery;

/// <summary>
/// A safe DTO for Spotify playlists that avoids runtime type resolution issues.
/// Used to bind the Discovery Hub Spotify sidebar to concrete types.
/// </summary>
public record SpotifyPlaylistDto
{
    /// <summary>Spotify playlist ID</summary>
    public string Id { get; init; } = string.Empty;
    
    /// <summary>Playlist name</summary>
    public string Name { get; init; } = string.Empty;
    
    /// <summary>URL to the playlist cover image</summary>
    public string? ImageUrl { get; init; }
    
    /// <summary>Number of tracks in the playlist</summary>
    public int TrackCount { get; init; }
    
    /// <summary>Owner display name</summary>
    public string? OwnerName { get; init; }
}

/// <summary>
/// A safe DTO for Spotify saved tracks.
/// </summary>
public record SpotifySavedTrackDto
{
    /// <summary>Track ID</summary>
    public string Id { get; init; } = string.Empty;
    
    /// <summary>Track name</summary>
    public string Name { get; init; } = string.Empty;
    
    /// <summary>Primary artist name</summary>
    public string Artist { get; init; } = string.Empty;
    
    /// <summary>Album name</summary>
    public string? Album { get; init; }
    
    /// <summary>Date the track was added to library</summary>
    public DateTime? AddedAt { get; init; }
}

/// <summary>
/// Represents a track in the Discovery Hub workbench.
/// Used for batch processing YouTube tracklists and Spotify imports.
/// </summary>
public record BatchTrackItem
{
    /// <summary>The artist name (parsed or provided)</summary>
    public string Artist { get; init; } = string.Empty;
    
    /// <summary>The track title (parsed or provided)</summary>
    public string Title { get; init; } = string.Empty;
    
    /// <summary>Original line text (for display/debugging)</summary>
    public string? OriginalLine { get; init; }
    
    /// <summary>Whether this item has been searched</summary>
    public bool IsSearched { get; set; }
    
    /// <summary>Number of P2P results found</summary>
    public int ResultCount { get; set; }
    
    /// <summary>The search query built from this item</summary>
    public string SearchQuery => $"{Artist} - {Title}";
}

/// <summary>
/// Enum for the current view mode in the Discovery Hub.
/// </summary>
public enum DiscoveryViewMode
{
    /// <summary>Standard search mode</summary>
    Search,
    
    /// <summary>Batch workbench mode (multiline tracklist)</summary>
    Workbench
}

/// <summary>
/// Simple DTO for displaying search results in the Discovery Hub.
/// Decoupled from Soulseek.SearchResult to avoid tight coupling.
/// </summary>
public record DiscoverySearchResultDto
{
    /// <summary>Display title</summary>
    public string DisplayTitle { get; init; } = string.Empty;
    
    /// <summary>Display artist</summary>
    public string DisplayArtist { get; init; } = string.Empty;
    
    /// <summary>Bitrate in kbps</summary>
    public int Bitrate { get; init; }
    
    /// <summary>File format extension</summary>
    public string Format { get; init; } = string.Empty;
    
    /// <summary>File size in bytes</summary>
    public long FileSize { get; init; }
    
    /// <summary>File size for display</summary>
    public string FileSizeDisplay => FileSize > 0 ? $"{FileSize / 1024.0 / 1024.0:F1} MB" : "N/A";
    
    /// <summary>P2P username/host</summary>
    public string Username { get; init; } = string.Empty;
    
    /// <summary>Full file path on P2P</summary>
    public string FullPath { get; init; } = string.Empty;
    
    /// <summary>Quality score (for heatmap visualization)</summary>
    public int QualityScore { get; init; }
    
    /// <summary>The underlying Track object for download operations</summary>
    public Track? Track { get; init; }

    /// <summary>Match reasoning / discovery logic</summary>
    public string? MatchReason { get; init; }
}

/// <summary>
/// Represents a track discovered from an external source (Spotify, MusicBrainz).
/// </summary>
public record DiscoveryTrack
{
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? SpotifyId { get; init; }
    public string? MusicBrainzId { get; init; }
    public string? MatchReason { get; init; }
    public string? ImageUrl { get; init; }
}
