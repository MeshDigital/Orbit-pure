using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>Where an Intake session is (epic #632).</summary>
public enum IntakeSessionStatus
{
    /// <summary>Files found on the source drive are being checked.</summary>
    Scanning,
    /// <summary>Every file has a lane; the user reviews and applies.</summary>
    Ready,
    /// <summary>The user's decisions are being carried out.</summary>
    Applying,
    /// <summary>Every decision was applied (or the user closed the session).</summary>
    Done,
}

/// <summary>Where one file is in its Intake session.</summary>
public enum IntakeItemStatus
{
    Pending,
    Scanned,
    /// <summary>The checks themselves failed (not the file): the row says why.</summary>
    Failed,
    Applied,
    Skipped,
}

/// <summary>The review lane a file lands in after its checks.</summary>
public enum IntakeLane
{
    Pending,
    /// <summary>Real quality, not in the library.</summary>
    Good,
    /// <summary>A better copy of a track already in the library.</summary>
    BetterCopy,
    /// <summary>Already in the library at the same or better quality.</summary>
    Duplicate,
    /// <summary>Fake lossless or low bitrate: worth finding a better copy.</summary>
    Suspicious,
    /// <summary>Unreadable, corrupt, or a preview.</summary>
    Reject,
}

/// <summary>What Apply does with a file.</summary>
public enum IntakeAction
{
    None,
    /// <summary>Copy into the library at the user's path template.</summary>
    Keep,
    /// <summary>Replace the library's copy with this one (cues and grid are kept and shifted).</summary>
    Replace,
    /// <summary>Leave it on the source drive; nothing is copied.</summary>
    Skip,
    /// <summary>Search Soulseek for a better copy of this track.</summary>
    Upgrade,
}

/// <summary>How a file was matched to a library track.</summary>
public enum IntakeMatchKind
{
    None,
    /// <summary>Byte-identical audio (tags ignored).</summary>
    ContentHash,
    /// <summary>Same artist, title and version, duration within 2 seconds.</summary>
    Metadata,
}

/// <summary>
/// One Intake session: a drive or folder whose music is checked before any of it joins the
/// library (epic #632). The source is only read; files are copied into the library on Keep.
/// </summary>
[Table("IntakeSessions")]
public class IntakeSessionEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Absolute path of the folder or drive being taken in.</summary>
    public string SourceRoot { get; set; } = string.Empty;

    /// <summary>Display name (the folder or volume name).</summary>
    public string Label { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ScannedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public IntakeSessionStatus Status { get; set; } = IntakeSessionStatus.Scanning;

    /// <summary>Decode every file in full to find corruption (slow on large drives).</summary>
    public bool DeepDecodeCheck { get; set; } = true;

    /// <summary>Files skipped while listing the source (non-audio, macOS "._" files, system files).</summary>
    public int JunkCount { get; set; }
}

/// <summary>One audio file in an Intake session, with what its checks found and what Apply will do.</summary>
[Table("IntakeItems")]
public class IntakeItemEntity
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string SessionId { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Path inside the session's source root, with forward slashes.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Lower-case extension with its dot (".flac").</summary>
    public string Extension { get; set; } = string.Empty;

    public IntakeItemStatus Status { get; set; } = IntakeItemStatus.Pending;
    public IntakeLane Lane { get; set; } = IntakeLane.Pending;

    /// <summary>The pre-ticked action; the user may change it before Apply.</summary>
    public IntakeAction Action { get; set; } = IntakeAction.None;

    /// <summary>One plain-English line on why the file is in its lane.</summary>
    public string? Reason { get; set; }

    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public int BitrateKbps { get; set; }
    public int SampleRate { get; set; }
    public int BitsPerSample { get; set; }

    /// <summary>Tag-independent audio hash, same form as <c>LibraryEntryEntity.AudioContentHash</c>.</summary>
    public string? ContentHash { get; set; }

    public string? SpectralVerdict { get; set; }
    public double? SpectralCutoffHz { get; set; }
    public double? SpectralConfidence { get; set; }

    public int DecodeErrorCount { get; set; }
    public string? DecodeErrorPreview { get; set; }

    public IntakeMatchKind MatchKind { get; set; } = IntakeMatchKind.None;
    public string? MatchedTrackHash { get; set; }
    public string? MatchedFilePath { get; set; }
    public string? MatchedFormat { get; set; }
    public int? MatchedBitrate { get; set; }

    /// <summary>Where Apply put the file in the library.</summary>
    public string? AppliedPath { get; set; }

    /// <summary>Why the checks or the apply step failed.</summary>
    public string? Error { get; set; }

    public DateTime? ScannedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
}
