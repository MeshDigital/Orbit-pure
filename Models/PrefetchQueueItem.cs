using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Models;

/// <summary>
/// Local staging queue item for opt-in prefetching before fingerprinting.
/// </summary>
[Table("PrefetchQueueItems")]
public sealed class PrefetchQueueItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string SourceUsername { get; set; } = string.Empty;

    [Required]
    [MaxLength(1024)]
    public string RemotePath { get; set; } = string.Empty;

    [Required]
    [MaxLength(1024)]
    public string LocalStagingPath { get; set; } = string.Empty;

    public PrefetchQueueStatus Status { get; set; } = PrefetchQueueStatus.Queued;

    public DateTime EnqueuedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public long BytesDownloaded { get; set; }
}

public enum PrefetchQueueStatus
{
    Queued = 0,
    Downloading = 1,
    Ready = 2,
    Failed = 3,
}