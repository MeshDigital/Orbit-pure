using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// One on-demand Library Maintenance run (choose tasks → dry-run → review → apply → summary/undo).
/// The run owns every <see cref="TagChangeLogEntity"/> and <see cref="MaintenancePlaylistLogEntity"/>
/// row written during it, which is what makes a whole-run undo possible. The relationship is
/// enforced in code (connections run with <c>PRAGMA foreign_keys=OFF</c>), not by an FK clause.
/// </summary>
[Table("MaintenanceRuns")]
public class MaintenanceRunEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? OptionsJson { get; set; }
    public string? SummaryJson { get; set; }

    /// <summary>Scanning | Applying | Completed | Failed | Reverted | PartiallyReverted.</summary>
    public string Status { get; set; } = "Scanning";

    public DateTime? RevertedAt { get; set; }
}

/// <summary>
/// One field change a maintenance run made (or proposed) for one track. Written before the write
/// (<c>Applied = 0</c>) so a crash mid-run still leaves a revertable journal.
/// </summary>
[Table("TagChangeLog")]
public class TagChangeLogEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string RunId { get; set; } = string.Empty;
    public string TrackUniqueHash { get; set; } = string.Empty;
    public string? FilePath { get; set; }

    /// <summary>Title | Artist | AlbumArtist | Album | Genre | Label | Remixer.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Null = the field was empty/absent before the run.</summary>
    public string? OldValue { get; set; }

    /// <summary>Null = the field was cleared.</summary>
    public string? NewValue { get; set; }

    /// <summary>Detector rule name, or "UseAsGenre" / "GenreFill".</summary>
    public string Rule { get; set; } = string.Empty;

    public int Applied { get; set; }
    public int FileWritten { get; set; }
    public string? Error { get; set; }
    public DateTime? RevertedAt { get; set; }

    /// <summary>e.g. "edited since" when the user changed the field after the run.</summary>
    public string? RevertSkipReason { get; set; }
}

/// <summary>
/// What a maintenance run did to a playlist: either created it (undo deletes it) or appended to an
/// existing one (undo removes just the added track ids).
/// </summary>
[Table("MaintenancePlaylistLog")]
public class MaintenancePlaylistLogEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string RunId { get; set; } = string.Empty;
    public string PlaylistId { get; set; } = string.Empty;

    /// <summary>1 = created by the run (undo deletes it); 0 = existing playlist we appended to.</summary>
    public int Created { get; set; }

    public string AddedTrackIdsJson { get; set; } = "[]";
}
