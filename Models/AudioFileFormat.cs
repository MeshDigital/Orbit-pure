using System;
using System.Collections.Generic;
using System.IO;

namespace SLSKDONET.Models;

/// <summary>
/// One answer to "what format is this track?" for every reader. Stored formats are unreliable:
/// some rows have none (PlaylistTracks created before Bitrate/Format were inherited from
/// LibraryEntries), and some code paths wrote the literal "Unknown", which the track list showed as
/// an "UNKNOWN" badge on analysed, downloaded files. The file's extension is the truth for a
/// downloaded file, so it is used whenever the stored value says nothing.
/// </summary>
public static class AudioFileFormat
{
    private static IReadOnlySet<string> KnownAudioFormats => SLSKDONET.Services.Audio.AudioFormats.AllTokens;

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "—", "-", "n/a", "na", "null", "none", "?",
    };

    /// <summary>
    /// Lower-case format ("flac", "mp3"…), or null when neither the stored value nor the file's
    /// extension says. A stored placeholder ("Unknown", "—") counts as nothing.
    /// </summary>
    public static string? Resolve(string? storedFormat, string? filePath)
    {
        var stored = Normalize(storedFormat);
        if (stored is not null) return stored;

        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var fromExtension = Normalize(Path.GetExtension(filePath.Trim())); // .NET Core: never throws on odd characters
        return fromExtension is not null && KnownAudioFormats.Contains(fromExtension) ? fromExtension : null;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimStart('.').Trim();
        if (trimmed.Length == 0 || Placeholders.Contains(trimmed)) return null;
        return trimmed.ToLowerInvariant();
    }
}
