using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SLSKDONET.Data;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Represents one atomic playlist mutation that can be undone/redone.
/// Uses a diff/delta approach (DeltaJson) rather than full snapshots to stay compact.
/// Max 50 entries per playlist — oldest entries are pruned automatically on Push.
/// </summary>
public class PlaylistHistoryEntryEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Job))]
    public Guid PlaylistId { get; set; }

    public PlaylistOperationType OperationType { get; set; }

    /// <summary>
    /// Track ids and positions before/after, serialised as JSON.
    /// Format: { "before": ["id1","id2"], "after": ["id3","id1","id2"] }
    /// </summary>
    public string DeltaJson { get; set; } = string.Empty;

    /// <summary>Human-readable description, e.g. "Added 5 tracks via Enhance".</summary>
    public string Description { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>True when this entry has been undone (available for Redo).</summary>
    public bool IsUndone { get; set; } = false;

    public PlaylistJobEntity? Job { get; set; }
}

/// <summary>Type of playlist mutation recorded in history.</summary>
public enum PlaylistOperationType
{
    Add,
    Remove,
    Move,
    EnhanceBatch,
    AutoSort,
    Clear
}
