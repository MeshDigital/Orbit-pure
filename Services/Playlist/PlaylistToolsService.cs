using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SLSKDONET.Models;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Playlist;

/// <summary>
/// Result of comparing two playlists by track identity (<see cref="PlaylistTrack.TrackUniqueHash"/>) (LP3 #554).
/// </summary>
public sealed record PlaylistComparisonResult(
    string PlaylistATitle,
    string PlaylistBTitle,
    IReadOnlyList<PlaylistTrack> InBoth,
    IReadOnlyList<PlaylistTrack> OnlyInA,
    IReadOnlyList<PlaylistTrack> OnlyInB);

/// <summary>
/// Pure daily-use playlist utilities for LP3 (#554):
/// - Compare two playlists (in both / only in A / only in B) by track hash.
/// - Format plain-text tracklist ("01. Artist - Title (174 BPM · 8A)") for sharing.
/// - Format RFC 4180 CSV export in playlist order.
/// </summary>
public static class PlaylistToolsService
{
    public static PlaylistComparisonResult Compare(
        string playlistATitle,
        IReadOnlyList<PlaylistTrack> tracksA,
        string playlistBTitle,
        IReadOnlyList<PlaylistTrack> tracksB)
    {
        static string IdentityKey(PlaylistTrack t) =>
            !string.IsNullOrWhiteSpace(t.TrackUniqueHash)
                ? t.TrackUniqueHash.Trim()
                : $"{t.Artist?.Trim()}|{t.Title?.Trim()}";

        var seenA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueA = new List<(string Key, PlaylistTrack Track)>();
        foreach (var t in tracksA)
        {
            var key = IdentityKey(t);
            if (seenA.Add(key))
                uniqueA.Add((key, t));
        }

        var seenB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueB = new List<(string Key, PlaylistTrack Track)>();
        foreach (var t in tracksB)
        {
            var key = IdentityKey(t);
            if (seenB.Add(key))
                uniqueB.Add((key, t));
        }

        var inBoth = new List<PlaylistTrack>();
        var onlyInA = new List<PlaylistTrack>();
        foreach (var (key, track) in uniqueA)
        {
            if (seenB.Contains(key))
                inBoth.Add(track);
            else
                onlyInA.Add(track);
        }

        var onlyInB = new List<PlaylistTrack>();
        foreach (var (key, track) in uniqueB)
        {
            if (!seenA.Contains(key))
                onlyInB.Add(track);
        }

        return new PlaylistComparisonResult(playlistATitle, playlistBTitle, inBoth, onlyInA, onlyInB);
    }

    /// <summary>
    /// Formats a playlist as a plain-text tracklist (<c>01. Artist - Title</c>), optionally appending
    /// BPM and key when present and omitting missing values cleanly.
    /// </summary>
    public static string FormatTracklist(IReadOnlyList<PlaylistTrack> tracks, bool includeBpmAndKey = true)
    {
        if (tracks.Count == 0)
            return string.Empty;

        var width = Math.Max(2, tracks.Count.ToString(CultureInfo.InvariantCulture).Length);
        var sb = new StringBuilder();

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var num = (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            var artist = string.IsNullOrWhiteSpace(t.Artist) ? "Unknown Artist" : t.Artist.Trim();
            var title = string.IsNullOrWhiteSpace(t.Title) ? "Untitled" : t.Title.Trim();

            sb.Append(num).Append(". ").Append(artist).Append(" - ").Append(title);

            if (includeBpmAndKey)
            {
                var meta = new List<string>(2);
                if (t.BPM.HasValue && t.BPM.Value > 0)
                    meta.Add($"{Math.Round(t.BPM.Value).ToString(CultureInfo.InvariantCulture)} BPM");
                if (!string.IsNullOrWhiteSpace(t.MusicalKey))
                    meta.Add(t.MusicalKey.Trim());

                if (meta.Count > 0)
                    sb.Append(" (").Append(string.Join(" \u00B7 ", meta)).Append(')');
            }

            if (i < tracks.Count - 1)
                sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a playlist as RFC 4180 CSV in playlist order, reusing standard quote-doubling escaping.
    /// </summary>
    public static string FormatCsv(IReadOnlyList<PlaylistTrack> tracks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#,Artist,Title,Album,BPM,Key,Duration,Format,Bitrate,FilePath");

        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var bpm = t.BPM.HasValue && t.BPM.Value > 0
                ? t.BPM.Value.ToString("F2", NumFormat.Culture)
                : string.Empty;
            var duration = t.CanonicalDuration.HasValue && t.CanonicalDuration.Value > 0
                ? TimeSpan.FromMilliseconds(t.CanonicalDuration.Value).ToString(@"m\:ss", CultureInfo.InvariantCulture)
                : string.Empty;
            var bitrate = t.Bitrate.HasValue && t.Bitrate.Value > 0 ? t.Bitrate.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

            sb.AppendLine(string.Join(",", new[]
            {
                EscapeCsvField((i + 1).ToString(CultureInfo.InvariantCulture)),
                EscapeCsvField(t.Artist),
                EscapeCsvField(t.Title),
                EscapeCsvField(t.Album),
                EscapeCsvField(bpm),
                EscapeCsvField(t.MusicalKey),
                EscapeCsvField(duration),
                EscapeCsvField(t.Format),
                EscapeCsvField(bitrate),
                EscapeCsvField(t.ResolvedFilePath)
            }));
        }

        return sb.ToString();
    }

    public static string EscapeCsvField(string? field) =>
        $"\"{field?.Replace("\"", "\"\"") ?? string.Empty}\"";
}
