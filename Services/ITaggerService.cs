using System.Collections.Generic;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

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
