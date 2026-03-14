using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities
{
    /// <summary>
    /// Stores heavy technical analysis data for a track.
    /// Separated from PlaylistTrackEntity to reduce memory usage when loading track lists.
    /// Loaded lazily only when needed (Inspector, Analysis, Playback visualizer).
    /// </summary>
    public class TrackTechnicalEntity
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid PlaylistTrackId { get; set; }

        // Navigation property back to parent (optional, but good for integrity)
        [ForeignKey(nameof(PlaylistTrackId))]
        public PlaylistTrackEntity? PlaylistTrack { get; set; }

        // --- Heavy Data ---

        public byte[]? WaveformData { get; set; }
        public byte[]? RmsData { get; set; }
        public byte[]? LowData { get; set; }
        public byte[]? MidData { get; set; }
        public byte[]? HighData { get; set; }

        public string? AiEmbeddingJson { get; set; } // Future: Vector embeddings
        public string? CuePointsJson { get; set; }   // JSON blob for cue points (can be large)
        
        public string? AudioFingerprint { get; set; }
        public string? SpectralHash { get; set; }
        public bool IsPrepared { get; set; } = false; // Phase 10
        public string? PrimaryGenre { get; set; } // Phase 10
        
        // Phase 10.5: Provenance
        public CurationConfidence CurationConfidence { get; set; } = CurationConfidence.None;
        public string? ProvenanceJson { get; set; }
        public bool IsReviewNeeded { get; set; } = false;
        
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
