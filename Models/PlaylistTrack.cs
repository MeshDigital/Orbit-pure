using System;

namespace SLSKDONET.Models;

/// <summary>
/// Represents a single track within a playlist.
/// This is the relational index linking playlists to the main library.
/// Foreign Keys: PlaylistId (to PlaylistJob), TrackUniqueHash (to LibraryEntry)
/// </summary>
public class PlaylistTrack
{
    /// <summary>
    /// Unique identifier for this playlist track entry.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key: References the parent PlaylistJob.
    /// </summary>
    public Guid PlaylistId { get; set; }

    /// <summary>
    /// Foreign key: References the LibraryEntry by its UniqueHash.
    /// Used to find the actual downloaded file (if it exists).
    /// </summary>
    public string TrackUniqueHash { get; set; } = string.Empty;

    /// <summary>
    /// Original track metadata as imported from the source.
    /// </summary>
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    
    /// <summary>
    /// File extension or format (e.g. "mp3", "flac").
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// Track status within this playlist's context.
    /// </summary>
    public TrackStatus Status { get; set; } = TrackStatus.Missing;

    /// <summary>
    /// The resolved file path for this track.
    /// - If Status = Downloaded: Points to LibraryEntry.FilePath (the actual downloaded file)
    /// - If Status = Missing: Points to the expected path (calculated by FileNameFormatter)
    /// Used by Rekordbox exporter to locate files.
    /// </summary>
    public string ResolvedFilePath { get; set; } = string.Empty;

    /// <summary>
    /// User rating (1-5 stars, 0 = not rated).
    /// </summary>
    public int Rating { get; set; } = 0;

    /// <summary>
    /// Whether the user has liked this track.
    /// Liked tracks are automatically added to "Liked Songs" smart playlist.
    /// </summary>
    public bool IsLiked { get; set; } = false;

    /// <summary>
    /// Number of times this track has been played.
    /// </summary>
    public int PlayCount { get; set; } = 0;

    /// <summary>
    /// Last time this track was played.
    /// </summary>
    public DateTime? LastPlayedAt { get; set; }

    /// <summary>
    /// Position within the original playlist (1-based index).
    /// </summary>
    public int TrackNumber { get; set; }

    /// <summary>
    /// The specific sub-genre detected by the Style Classifier (Phase 12.7).
    /// </summary>
    public string? DetectedSubGenre { get; set; }

    /// <summary>
    /// Confidence score for the detected sub-genre (0.0 - 1.0).
    /// </summary>
    public float? SubGenreConfidence { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when this track finished downloading or failed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Phase 10: Timestamp when the initial search for this track started.
    /// Used to calculate total download duration.
    /// </summary>
    public DateTime? SearchStartedAt { get; set; }

    /// <summary>
    /// Custom sort order for track reordering (Rekordbox style).
    /// </summary>
    public int SortOrder { get; set; }
    
    // Spotify Metadata (Phase 0: Metadata Gravity Well)
    public string? SpotifyTrackId { get; set; }
    public string? ISRC { get; set; }
    public string? MusicBrainzId { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public byte[] WaveformData { get; set; } = Array.Empty<byte>(); // Peak Data
    public byte[] RmsData { get; set; } = Array.Empty<byte>();
    public byte[] LowData { get; set; } = Array.Empty<byte>();
    public byte[] MidData { get; set; } = Array.Empty<byte>();
    public byte[] HighData { get; set; } = Array.Empty<byte>();
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; }
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Label { get; set; }
    public string? Comments { get; set; }

    // Phase 0.1: Musical Intelligence & Antigravity
    public string? MusicalKey { get; set; }
    public string? Key => MusicalKey; // Alias for UI binding
    public string? Tonality => MusicalKey; // Alias for PlaylistTrackViewModel
    public double? BPM { get; set; }
    public string? CuePointsJson { get; set; }
    public string? AudioFingerprint { get; set; }
    public int? BitrateScore { get; set; }
    public int? Bitrate { get; set; }
    public double? AnalysisOffset { get; set; }
    public double? Energy { get; set; }
    public double? Danceability { get; set; }
    public double? Valence { get; set; }
    public string? MoodTag { get; set; }
    public double? InstrumentalProbability { get; set; } // Phase 18.2
    
    // Phase 21: AI Brain
    public double? Sadness { get; set; }
    public float[]? VectorEmbedding { get; set; } // Effnet Embeddings

    // Phase 3A: Dual-Truth Metadata
    public double? SpotifyBPM { get; set; }
    public string? SpotifyKey { get; set; }
    public double? ManualBPM { get; set; }
    public string? ManualKey { get; set; }

    // Phase 8: Sonic Integrity & Spectral Analysis
    public string? SpectralHash { get; set; }
    public double? QualityConfidence { get; set; }
    public int? FrequencyCutoff { get; set; }
    public bool? IsTrustworthy { get; set; }
    public string? QualityDetails { get; set; }

    public SLSKDONET.Data.IntegrityLevel Integrity { get; set; } = SLSKDONET.Data.IntegrityLevel.None;
    
    /// <summary>
    /// Phase 2.1: Flagged by Safety Filter (High Risk)
    /// </summary>
    public bool IsFlagged { get; set; }
    public string? FlagReason { get; set; }
    
    /// <summary>
    /// Phase 1: Engine Overhaul - Force download ignoring quality/safety filters.
    /// </summary>
    public bool IgnoreSafetyGuards { get; set; } = false;
    
    // Phase 17: Technical Audio Analysis
    public double? Loudness { get; set; }
    public double? TruePeak { get; set; }
    public double? DynamicRange { get; set; }
    
    // Phase 3C: Advanced Queue Orchestration
    /// <summary>
    /// Download priority: 0=High (manual/bump-to-top), 1=Standard (playlist), 10=Background (large imports).
    /// </summary>
    public int Priority { get; set; } = 1;
    
    /// <summary>
    /// Source playlist ID for origin tracking and project grouping.
    /// </summary>
    public Guid? SourcePlaylistId { get; set; }
    
    /// <summary>
    /// Cached source playlist name for UI origin tags.
    /// </summary>
    public string? SourcePlaylistName { get; set; }
    
    public bool IsEnriched { get; set; } = false;
    public bool IsUserPaused { get; set; } = false; // Phase 13 Hardening
    public string? StalledReason { get; set; } // [NEW] Overhaul Phase
    
    /// <summary>
    /// Soft-clear flag: hides from Download Center without deleting from library.
    /// Persisted to DB so tracks don't reappear on restart.
    /// </summary>
    public bool IsClearedFromDownloadCenter { get; set; } = false;
    public bool IsPrepared { get; set; } = false; // Phase 10
    public bool IsReviewNeeded { get; set; } = false; // Phase 10.4
    public string? PrimaryGenre { get; set; } // Phase 10
    public string? DiscoveryReason { get; set; } // [NEW] Phase 7: Match Reasoning

    // Phase 10.5: Reliability & Transparency
    public SLSKDONET.Data.Entities.CurationConfidence CurationConfidence { get; set; } = SLSKDONET.Data.Entities.CurationConfidence.None;
    public SLSKDONET.Data.Entities.DataSource Source { get; set; } = SLSKDONET.Data.Entities.DataSource.Unknown;

    // Phase 13: Per-Track Filter Overrides
    public string? PreferredFormats { get; set; }
    public int? MinBitrateOverride { get; set; }

    // Phase 5: Ultimate Track View
    public double? DropTimestamp { get; set; }
    public int? ManualEnergy { get; set; }
    public string? SourceProvenance { get; set; }
    
    // Phase 21: Failure Escalation
    public int SearchRetryCount { get; set; } = 0;
    public int NotFoundRestartCount { get; set; } = 0;

    public WaveformAnalysisData WaveformDataObj => new WaveformAnalysisData
    {
        PeakData = WaveformData ?? Array.Empty<byte>(),
        RmsData = RmsData ?? Array.Empty<byte>(),
        LowData = LowData ?? Array.Empty<byte>(),
        MidData = MidData ?? Array.Empty<byte>(),
        HighData = HighData ?? Array.Empty<byte>(),
        DurationSeconds = (CanonicalDuration ?? 0) / 1000.0
    };

    public float[] VocalDensityCurve { get; set; } = Array.Empty<float>();

    public double Duration => (CanonicalDuration ?? 0) / 1000.0;
}

/// <summary>
/// Track status within a playlist context.
/// </summary>
public enum TrackStatus
{
    Missing = 0,      // Track not yet downloaded, queued for search
    Downloaded = 1,   // Track found in library (either just downloaded or previously)
    Failed = 2,       // Download was attempted but failed
    Skipped = 3,      // Track was skipped during import
    Pending = 4,      // Track accepted for download, waiting for queue
    OnHold = 5        // Escalate to manual MP3 search after multiple failures
}

public enum PlaylistTrackState
{
    Pending,
    Searching,
    Queued,
    Downloading,
    Converting,
    Paused,
    Deferred,
    Completed,
    Failed,
    Cancelled,
    Stalled,
    WaitingForConnection
}
