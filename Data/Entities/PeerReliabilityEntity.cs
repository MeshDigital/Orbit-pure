using System;
using System.ComponentModel.DataAnnotations;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Persists peer reliability statistics to the database.
/// </summary>
public class PeerReliabilityEntity
{
    [Key]
    public string Username { get; set; } = string.Empty;

    public long SearchCandidates { get; set; }
    public long DownloadStarts { get; set; }
    public long DownloadCompletions { get; set; }
    public long DownloadFailures { get; set; }
    public long StallFailures { get; set; }
    public long BytesTransferred { get; set; }
    public long LastSeenTicks { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}