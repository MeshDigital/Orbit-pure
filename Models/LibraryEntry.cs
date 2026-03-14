using System;

using SLSKDONET.Models.Musical;

namespace SLSKDONET.Models;

/// <summary>
/// The main global index for unique, downloaded files.
/// This is the single source of truth for all files in the library.
/// Primary Key: UniqueHash (Artist-Title, case-insensitive)
/// </summary>
public class LibraryEntry
{
    /// <summary>
    /// Unique identifier from the database.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Unique hash of the track (Artist-Title, case-insensitive).
    /// Acts as the primary key for the global library index.
    /// </summary>
    public string UniqueHash { get; set; } = string.Empty;

    /// <summary>
    /// Artist name.
    /// </summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>
    /// Track title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Album name.
    /// </summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>
    /// Absolute file path on disk where the track is stored.
    /// Used by Rekordbox exporter and deduplication logic.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional: Audio metadata (bitrate, duration, etc).
    /// Populated during library sync.
    /// </summary>
    public int Bitrate { get; set; }
    public int? DurationSeconds { get; set; }
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when this entry was added to the library.
    /// </summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // Spotify Metadata
    public string? SpotifyTrackId { get; set; }
    public string? ISRC { get; set; }
    public string? MusicBrainzId { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; }
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Label { get; set; }
    public string? Comments { get; set; }
    
    public byte[] WaveformData { get; set; } = Array.Empty<byte>();
    public byte[] RmsData { get; set; } = Array.Empty<byte>();
    public byte[] LowData { get; set; } = Array.Empty<byte>();
    public byte[] MidData { get; set; } = Array.Empty<byte>();
    public byte[] HighData { get; set; } = Array.Empty<byte>();

    // Musical Intelligence
    public string? MusicalKey { get; set; }
    public double? BPM { get; set; }
    public double? Energy { get; set; }
    public double? Danceability { get; set; }
    public double? Valence { get; set; }
    public double? InstrumentalProbability { get; set; } // Phase 18.2
    
    // Dual-Truth Metadata
    public double? SpotifyBPM { get; set; }
    public string? SpotifyKey { get; set; }
    public double? ManualBPM { get; set; }
    public string? ManualKey { get; set; }
    
    public bool IsEnriched { get; set; } = false;

    // Phase 8: Sonic Integrity & Spectral Analysis
    public string? SpectralHash { get; set; }
    public double? QualityConfidence { get; set; }
    public int? FrequencyCutoff { get; set; }
    public bool? IsTrustworthy { get; set; }
    public string? QualityDetails { get; set; }
    public SLSKDONET.Data.IntegrityLevel Integrity { get; set; } = SLSKDONET.Data.IntegrityLevel.None;

    // Phase 17: Technical Audio Analysis
    public double? Loudness { get; set; }
    public double? TruePeak { get; set; }
    public double? DynamicRange { get; set; }

    // Phase 15
    public string? DetectedSubGenre { get; set; }
    public bool IsPrepared { get; set; } = false; // Phase 10
    public string? PrimaryGenre { get; set; } // Phase 10
    public string? CuePointsJson { get; set; } // Phase 10
    public string? MoodTag { get; set; }
    
    // Phase 21: AI Brain Upgrade
    public double? Sadness { get; set; }
    public float[]? VectorEmbedding { get; set; } // Effnet Embeddings

    // Phase 3.5: Vocal Intelligence
    public VocalType VocalType { get; set; } = VocalType.Instrumental;
    public double? VocalIntensity { get; set; }
    public double? VocalStartSeconds { get; set; }
    public double? VocalEndSeconds { get; set; }
}
