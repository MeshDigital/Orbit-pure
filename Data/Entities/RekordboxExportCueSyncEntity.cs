using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Records the cue set ORBIT most recently confirmed as in-sync for one (export target file,
/// track) pairing, enabling a three-way merge on re-export: if the file's on-disk cues for a
/// track still match this snapshot, Rekordbox hasn't touched them since ORBIT last wrote them,
/// so a fresh cue edit made in Cue Forge is safe to write through. If they differ, the DJ
/// hand-edited cues directly in Rekordbox since then, so those edits are preserved instead.
/// See <see cref="SLSKDONET.Services.Library.Rekordbox.RekordboxXmlMerger"/>.
/// </summary>
[Table("RekordboxExportCueSync")]
public class RekordboxExportCueSyncEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Normalized absolute path of the exported Rekordbox XML file.</summary>
    [Required]
    public string TargetPath { get; set; } = string.Empty;

    [Required]
    public string TrackUniqueHash { get; set; } = string.Empty;

    /// <summary>Canonicalized POSITION_MARK set last written/confirmed for this pairing.</summary>
    public string CueSnapshot { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
