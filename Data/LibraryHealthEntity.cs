using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data;

public class LibraryHealthEntity
{
    [Key]
    public int Id { get; set; } // We only ever keep one record (Id=1)
    
    public int TotalTracks { get; set; }
    public int HqTracks { get; set; } // > 256kbps or FLAC
    public int UpgradableCount { get; set; } // Low bitrate tracks
    public int PendingUpdates { get; set; } // Tracks needing metadata/enrichment
    
    public int GoldCount { get; set; } // FLAC/Lossless
    public int SilverCount { get; set; } // 320kbps
    public int BronzeCount { get; set; } // < 320kbps
    
    public long TotalStorageBytes { get; set; }
    public long FreeStorageBytes { get; set; }
    
    public DateTime LastScanDate { get; set; }
    public string? TopGenresJson { get; set; } // Serialized top genres
    
    // Phase 3A: Transparency Properties (Not persisted)
    [NotMapped]
    public int HealthScore { get; set; } = 100;
    
    [NotMapped]
    public string HealthStatus { get; set; } = "Healthy";
    
    [NotMapped]
    public int IssuesCount { get; set; } = 0;
}
