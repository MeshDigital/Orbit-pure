namespace SLSKDONET.Models;

/// <summary>What CommentTracklistParser did with one line of pasted input.</summary>
public enum ImportLineOutcome
{
    /// <summary>Line became a track, added to the result list.</summary>
    Kept,
    /// <summary>A bare timestamp marker (e.g. "0:00") — consumed as a signal for the next line, not a track itself.</summary>
    TimestampMarker,
    /// <summary>Matched a junk keyword/pattern (site chrome, bare URL, track-number-only, etc.).</summary>
    DroppedJunk,
    /// <summary>Didn't look like a track line and wasn't the first line (so not a title/header either) — e.g. a footer line.</summary>
    DroppedNotATrack,
    /// <summary>The first non-blank, non-junk, non-track line — consumed as the detected playlist title/header.</summary>
    DetectedAsTitle,
    /// <summary>Artist or Title was empty after splitting — nothing usable to search for.</summary>
    DroppedEmptyAfterSplit,
    /// <summary>Title resolved to "ID" — DJ-tracklist shorthand for an unidentified track.</summary>
    DroppedId,
    /// <summary>Same Artist+Title as the immediately preceding kept track.</summary>
    DroppedDuplicate,
}

/// <summary>
/// One row of the per-line paste-import audit trail — lets a user see exactly what each line of a
/// pasted tracklist became (or why it was dropped), the literal first ask behind the Engine
/// Diagnostics feature. Emitted by <see cref="Utils.CommentTracklistParser.Parse"/>'s optional
/// audit callback; not used by the CSV-shortcut path (a different, much simpler column mapping).
/// </summary>
public record ImportLineAuditEntry(
    int LineNumber,
    string RawLine,
    ImportLineOutcome Outcome,
    string? Artist = null,
    string? Title = null,
    string? OriginalArtist = null,
    string? OriginalTitle = null);
