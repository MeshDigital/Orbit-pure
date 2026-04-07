using System;
using System.IO;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Abstraction over a source audio file that the ingestion pipeline can decode.
/// </summary>
public sealed class TrackAudioSource
{
    /// <summary>Absolute path to the source audio file (mp3, flac, wav, ogg, aac, …).</summary>
    public string FilePath { get; }

    /// <summary>Optional track identity hash (used for logging / cache keys).</summary>
    public string? TrackUniqueHash { get; init; }

    public TrackAudioSource(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio source file not found.", filePath);

        FilePath = filePath;
    }

    /// <summary>File extension in lower-case (e.g. ".mp3").</summary>
    public string Extension => Path.GetExtension(FilePath).ToLowerInvariant();
}
