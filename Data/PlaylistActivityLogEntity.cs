using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data;

public class PlaylistActivityLogEntity
{
    [Key]
    public Guid Id { get; set; }

    [ForeignKey(nameof(Job))]
    public Guid PlaylistId { get; set; }
    
    public string Action { get; set; } = string.Empty; // "Add", "Remove", "Reorder"
    public string Details { get; set; } = string.Empty; // e.g. "Added track 'Artist - Title'"
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public PlaylistJobEntity? Job { get; set; }
}
