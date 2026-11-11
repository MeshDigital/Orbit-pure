using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>The metadata fields a maintenance patch can set or clear on a file.</summary>
public enum TagField { Title, Artist, AlbumArtist, Album, Genre, Label, Remixer }

/// <summary>One field change in a patch. <see cref="Value"/> == null means "clear the field".</summary>
public sealed record TagPatchField(TagField Field, string? Value);

/// <summary>A set of tag changes to apply to a single file in one open.</summary>
public sealed record TagPatch(IReadOnlyList<TagPatchField> Changes);

/// <summary>
/// Result of <see cref="ITaggerService.ApplyTagPatchAsync"/>. <see cref="PreviousValues"/> holds the
/// value of each patched field as it was on disk before the write (null = empty/absent), read inside
/// the same TagLib open so the revert journal is accurate.
/// </summary>
public sealed record TagPatchResult(
    bool Success,
    string? Error,
    IReadOnlyDictionary<TagField, string?> PreviousValues);

/// <summary>
/// Service for writing metadata tags to audio files.
/// Supports ID3v2 (MP3), Vorbis (OGG/FLAC), and other formats via TagLibSharp.
/// </summary>
public interface ITaggerService
{
    /// <summary>
    /// Tags an audio file with metadata from a Track model.
    /// Writes common tags: Title, Artist, Album, Track Number, Year, and Album Art if available.
    /// </summary>
    /// <param name="track">Source track with metadata to write</param>
    /// <param name="filePath">Path to the audio file to tag</param>
    /// <returns>True if tagging succeeded, false if file not found or tagging failed</returns>
    Task<bool> TagFileAsync(Track track, string filePath);

    /// <summary>
    /// Applies a patch of set-or-clear field changes to an audio file atomically (temp copy → TagLib
    /// write → format verify → safe swap), returning the previous value of each patched field.
    /// <see cref="TagLib.CorruptFileException"/> is returned as <c>Success = false</c>, not swallowed.
    /// </summary>
    Task<TagPatchResult> ApplyTagPatchAsync(string filePath, TagPatch patch, CancellationToken ct = default);

    /// <summary>
    /// Reads the current value of a single tag field from an audio file (null = empty/absent).
    /// </summary>
    Task<string?> ReadFieldValueAsync(string filePath, TagField field, CancellationToken ct = default);

    /// <summary>
    /// Reads metadata tags from an audio file.
    /// </summary>
    /// <param name="filePath">Path to the audio file</param>
    /// <returns>A Track model populated with the file's tags, or null if reading fails</returns>
    Task<Track?> ReadTagsAsync(string filePath);

    /// <summary>
    /// Tags multiple audio files asynchronously.
    /// </summary>
    /// <param name="tracks">List of tracks with metadata</param>
    /// <param name="filePaths">Corresponding list of file paths (must match track count)</param>
    /// <returns>Number of files successfully tagged</returns>
    Task<int> TagFilesAsync(IEnumerable<Track> tracks, IEnumerable<string> filePaths);
}
