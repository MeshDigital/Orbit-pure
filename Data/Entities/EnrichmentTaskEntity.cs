using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

[Table("EnrichmentTasks")]
public class EnrichmentTaskEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string TrackId { get; set; } = string.Empty;

    public Guid? AlbumId { get; set; }

    [Required]
    public EnrichmentStatus Status { get; set; }

    public int RetryCount { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}

public enum EnrichmentStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}
