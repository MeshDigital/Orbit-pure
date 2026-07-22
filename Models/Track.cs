using System.IO;

namespace SLSKDONET.Models;

public enum TrackAvailabilityState
{
    Ghost = 0,
    QueuedForDownload = 1,
    Downloading = 2,
    LocalUnanalyzed = 3,
    Ready = 4
}

/// <summary>
/// Represents a music track found on Soulseek.
/// </summary>
public class Track
{
    private TrackAvailabilityState _availabilityState = TrackAvailabilityState.Ghost;
    
    public TrackAvailabilityState AvailabilityState
    {
        get
        {
            if (string.IsNullOrEmpty(FilePath) || !System.IO.File.Exists(FilePath))
            {
                if (_availabilityState == TrackAvailabilityState.QueuedForDownload ||
                    _availabilityState == TrackAvailabilityState.Downloading)
                {
                    return _availabilityState;
                }
                return TrackAvailabilityState.Ghost;
            }
            return _availabilityState == TrackAvailabilityState.Ghost ? TrackAvailabilityState.LocalUnanalyzed : _availabilityState;
        }
        set => _availabilityState = value;
    }

    public string? SpotifyPlaylistId { get; set; }
    public string? SpotifyUri { get; set; }

    public string? Filename { get; set; }
    public string? Directory { get; set; } // Added for Album Grouping
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Album { get; set; }

    /// <summary>
    /// Raw artist string before any sanitization. Null when no cleaning was performed.
    /// Used by ImportPreviewPage to show a "⚠️ Cleaned" badge and diff tooltip.
    /// </summary>
    public string? OriginalArtist { get; set; }

    /// <summary>
    /// Raw title string before any sanitization. Null when no cleaning was performed.
    /// Used by ImportPreviewPage to show a "⚠️ Cleaned" badge and diff tooltip.
    /// </summary>
    public string? OriginalTitle { get; set; }
    public long? Size { get; set; }
    public string? Username { get; set; }
    public string? Format { get; set; }
    public int? Length { get; set; } // in seconds
    public int Bitrate { get; set; } // in kbps
    public int? SampleRate { get; set; } // in Hz
    public int? BitDepth { get; set; }   // in bits (e.g., 16, 24)
    public List<string>? PathSegments { get; set; } // Phase 1.1: Folder names for context scoring
    public Dictionary<string, object>? Metadata { get; set; }
    public string? Label { get; set; }
    
    // Spotify Metadata (Phase 0: Metadata Gravity Well)
    public string? SpotifyTrackId { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; }
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; }
    public DateTime? ReleaseDate { get; set; } // Use DateTime? instead of string for better type safety where possible
    
    // Phase 1: Musical Intelligence (from Spotify Audio Features)
    public double? BPM { get; set; }           // Tempo from Spotify (e.g., 128.005)
    public string? MusicalKey { get; set; }    // Camelot notation (e.g., "8A")
    public double? Energy { get; set; }        // 0.0 - 1.0 (Spotify Audio Feature)
    public double? Valence { get; set; }       // 0.0 - 1.0 (Spotify Audio Feature - happiness/positivity)
    public double? Danceability { get; set; }  // 0.0 - 1.0 (Spotify Audio Feature)
    
    // Intelligence Metrics
    public bool HasFreeUploadSlot { get; set; }
    public int QueueLength { get; set; }
    public int UploadSpeed { get; set; } // Bytes per second

    // Phase 21: AI Brain Upgrade
    public double? Sadness { get; set; }
    public byte[]? VectorEmbedding { get; set; }

    /// <summary>
    /// Local filesystem path where the track was stored (if known).
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// Absolute file path of the downloaded file, or expected final path if not yet downloaded.
    /// Used for library tracking and Rekordbox export.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Name of the source playlist (e.g., Spotify playlist name or CSV filename).
    /// Temporary field used during parsing before tracks are added to PlaylistJob.
    /// </summary>
    public string? SourceTitle { get; set; }

    public bool IsSelected { get; set; } = false;
    public Soulseek.File? SoulseekFile { get; set; }
    
    /// <summary>
    /// Original index from the search results (before sorting/filtering).
    /// Allows user to reset view to original search order.
    /// </summary>
    public int OriginalIndex { get; set; } = -1;
    
    /// <summary>
    /// Current ranking score for this result.
    /// Higher = better match. Used for sorting display.
    /// </summary>
    public double CurrentRank { get; set; } = 0.0;

    /// <summary>
    /// Phase 1.3: Detailed explanation of the ranking score.
    /// Used for transparency tooltips in search results.
    /// </summary>
    public string? ScoreBreakdown { get; set; }

    /// <summary>
    /// How well this search result's artist/title text matches the search query (0.0-1.0),
    /// set by <see cref="SLSKDONET.Services.ResultSorter.CalculateRank"/>. Distinct from
    /// <see cref="CurrentRank"/>, which blends this together with file quality and peer
    /// availability — this raw signal exists so relevance-sensitive decisions (like the
    /// discovery fast-lane short-circuit) can require a minimum match confidence on its own,
    /// rather than letting a great-quality file for the wrong song satisfy a blended threshold.
    /// </summary>
    public double MetadataMatchScore { get; set; } = 0.0;

    /// <summary>
    /// Indicates whether this track already exists in the user's library.
    /// Used by ImportPreview to show duplicate status.
    /// </summary>
    public bool IsInLibrary { get; set; } = false;

    /// <summary>
    /// True when a fuzzy (near-match) library duplicate was found but confidence was below
    /// the auto-dedup threshold.  Shows a warning in the import preview so the user can decide.
    /// </summary>
    public bool IsPossibleDuplicate { get; set; } = false;

    /// <summary>
    /// The library entry's Artist value that triggered the possible-duplicate warning.
    /// </summary>
    public string? FuzzyMatchArtist { get; set; }

    /// <summary>
    /// The library entry's Title value that triggered the possible-duplicate warning.
    /// </summary>
    public string? FuzzyMatchTitle { get; set; }

    /// <summary>
    /// The fuzzy similarity score (0–1) that produced the possible-duplicate warning.
    /// </summary>
    public double FuzzyMatchScore { get; set; }

    /// <summary>
    /// Unique hash for deduplication: artist-title combination (lowercase, no spaces).
    /// </summary>
    public string UniqueHash => $"{Artist?.ToLower().Replace(" ", "")}-{Title?.ToLower().Replace(" ", "")}".TrimStart('-').TrimEnd('-');

    /// <summary>
    /// True if the "Bouncer" (SafetyFilter) flagged this track (e.g., Fake FLAC, suspicion).
    /// </summary>
    public bool IsFlagged { get; set; }

    /// <summary>
    /// The reason why the track was flagged (e.g., "Suspicious Size", "Banned User").
    /// </summary>
    public string? FlagReason { get; set; }

    /// <summary>
    /// Phase 18.2: Human-readable explanation for why this track matched in a sonic search (e.g. "Twin Vibe").
    /// </summary>
    public string? MatchReason { get; set; }

    /// <summary>
    /// Gets the file extension from the filename.
    /// </summary>
    public string GetExtension()
    {
        if (string.IsNullOrEmpty(Filename))
            return "";
        return Path.GetExtension(Filename).TrimStart('.');
    }

    /// <summary>
    /// Gets a user-friendly size representation.
    /// </summary>
    public string GetFormattedSize()
    {
        if (Size == null) return "Unknown";
        
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        return Size.Value switch
        {
            >= gb => $"{Size.Value / (double)gb:F2} GB",
            >= mb => $"{Size.Value / (double)mb:F2} MB",
            >= kb => $"{Size.Value / (double)kb:F2} KB",
            _ => $"{Size.Value} B"
        };
    }

    public override string ToString()
    {
        return $"{Artist} - {Title} ({Filename})";
    }
    
    /// <summary>
    /// Phase 2.8: Null Object Pattern - represents a missing/unknown track.
    /// Eliminates need for null checks throughout the codebase.
    /// Use this instead of returning null to provide safe default values.
    /// </summary>
    public static readonly Track Null = new Track
    {
        Filename = "",
        Directory = "",
        Artist = "Unknown Artist",
        Title = "Unknown Track",
        Album = "Unknown Album",
        Size = 0,
        Username = "Unknown",
        Format = "",
        Length = 0,
        Bitrate = 0,
        Metadata = new Dictionary<string, object>(),
        
        // Spotify Metadata - all null
        SpotifyTrackId = null,
        SpotifyAlbumId = null,
        SpotifyArtistId = null,
        AlbumArtUrl = null,
        ArtistImageUrl = null,
        Genres = null,
        Popularity = null,
        CanonicalDuration = null,
        ReleaseDate = null,
        
        // Musical Intelligence - all null
        BPM = null,
        MusicalKey = null,
        Energy = null,
        Valence = null,
        Danceability = null,
        
        // Intelligence Metrics - worst case values
        HasFreeUploadSlot = false,
        QueueLength = int.MaxValue,
        UploadSpeed = 0,
        
        // Paths
        LocalPath = null,
        FilePath = null,
        SourceTitle = null,
        
        // State
        IsSelected = false,
        SoulseekFile = null,
        OriginalIndex = -1,
        CurrentRank = double.NegativeInfinity,
        IsInLibrary = false
    };
    
    /// <summary>
    /// Checks if this track is the Null object.
    /// </summary>
    public bool IsNull => ReferenceEquals(this, Null);
}
