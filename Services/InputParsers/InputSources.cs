using System.IO;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using SLSKDONET.Models;

namespace SLSKDONET.Services.InputParsers;

/// <summary>
/// Interface for input sources.
/// </summary>
public interface IInputSource
{
    InputType InputType { get; }
    Task<List<SearchQuery>> ParseAsync(string input);
}

/// <summary>A CSV column ORBIT understands, and the file's header it was read from.</summary>
public enum CsvField { Title, Artist, Album, Duration, DurationMs, Isrc, SpotifyId, AlbumTrackCount, Playlist }

/// <summary>
/// The result of reading a CSV: which headers were understood, the tracks, and what was wrong.
/// <see cref="Errors"/> make the file unusable (import must not start); <see cref="Warnings"/>
/// describe rows that were skipped or will match less well.
/// </summary>
public sealed class CsvInspection
{
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<CsvField, string> Mapping { get; init; } = new Dictionary<CsvField, string>();
    public char Delimiter { get; init; } = ',';
    public List<SearchQuery> Tracks { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>A playlist name found inside the file (TuneMyMusic / Soundiiz exports), if all rows agree.</summary>
    public string? PlaylistName { get; set; }
    /// <summary>True when the file has artist + album but no title column: each row is a whole album.</summary>
    public bool IsAlbumList { get; init; }

    public bool IsValid => Errors.Count == 0;

    /// <summary>One line for the import preview: which columns were used and what was skipped.</summary>
    public string Summary
    {
        get
        {
            var used = string.Join(", ", Mapping
                .Where(m => m.Key is not CsvField.Playlist)
                .Select(m => $"{Label(m.Key)} ← \"{m.Value}\""));
            var text = $"Read with columns {used}.";
            return Warnings.Count == 0 ? text : text + " " + string.Join(" ", Warnings);
        }
    }

    private static string Label(CsvField field) => field switch
    {
        CsvField.DurationMs => "Duration",
        CsvField.Isrc => "ISRC",
        CsvField.SpotifyId => "Spotify ID",
        CsvField.AlbumTrackCount => "Album tracks",
        _ => field.ToString(),
    };
}

/// <summary>
/// Parses CSV (or tab/semicolon separated) track lists: hand-made sheets, Excel saves, and exports
/// from Exportify, TuneMyMusic, Soundiiz and Rekordbox. Headers are matched by alias, so
/// "Track Name", "Song" and "Title" all mean the title. The file is validated before any track is
/// produced, because a wrong column guess fills the preview with garbage rows.
/// </summary>
public class CsvInputSource : IInputSource
{
    public InputType InputType => InputType.CSV;

    /// <summary>What the Import page offers as "Download template".</summary>
    public const string TemplateCsv =
        "Artist,Title,Album,Duration,ISRC\r\n" +
        "Daft Punk,One More Time,Discovery,5:20,\r\n" +
        "Bicep,Glue,Bicep,,\r\n" +
        "Aphex Twin,Xtal,Selected Ambient Works 85-92,,\r\n";

    public const string ExpectedColumnsText =
        "ORBIT needs a Title column (also read as Track, Track Name or Song) and preferably an Artist column. " +
        "Album, Duration and ISRC are optional.";

    // Matched after lower-casing and dropping everything but letters and digits,
    // so "Artist Name(s)" → "artistnames" and "Track Duration (ms)" → "trackdurationms".
    // Dutch, German and French headers are included for sheets typed by hand.
    private static readonly (CsvField Field, string[] Aliases)[] Aliases =
    {
        (CsvField.Title, new[] { "title", "tracktitle", "trackname", "track", "song", "songname", "songtitle", "name", "titel", "titre", "liedtitel" }),
        (CsvField.Artist, new[] { "artist", "artists", "artistname", "artistnames", "trackartist", "trackartists", "performer", "creator", "artiest", "artiesten", "kunstenaar", "künstler", "kuenstler", "interpret", "artiste" }),
        (CsvField.Album, new[] { "album", "albumname", "albumtitle", "release", "releasename", "albumtitel" }),
        (CsvField.DurationMs, new[] { "durationms", "trackdurationms", "lengthms", "timems" }),
        (CsvField.Duration, new[] { "duration", "length", "time", "trackduration", "duur", "lengte", "tijd", "dauer", "länge", "durée" }),
        (CsvField.Isrc, new[] { "isrc", "trackisrc" }),
        (CsvField.SpotifyId, new[] { "spotifyid", "spotifytrackid", "trackuri", "spotifyuri", "uri", "spotifyurl" }),
        (CsvField.AlbumTrackCount, new[] { "albumtrackcount", "trackcount", "albumtracks" }),
        (CsvField.Playlist, new[] { "playlist", "playlistname" }),
    };

    /// <summary>
    /// Parses a CSV file into search queries. Throws <see cref="InvalidOperationException"/> with a
    /// readable explanation when the file can't be used.
    /// </summary>
    public Task<List<SearchQuery>> ParseAsync(string filePath)
    {
        var inspection = Inspect(filePath);
        if (!inspection.IsValid)
            throw new InvalidOperationException(string.Join(" ", inspection.Errors));
        return Task.FromResult(inspection.Tracks);
    }

    /// <summary>Reads and validates the whole file without throwing for content problems.</summary>
    public CsvInspection Inspect(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"CSV file not found: {filePath}");

        var rows = ReadRows(filePath, out var delimiter);
        var headerIndex = rows.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (headerIndex < 0)
        {
            var empty = new CsvInspection();
            empty.Errors.Add("The file is empty.");
            return empty;
        }

        var headers = rows[headerIndex].Select(h => h.Trim().TrimStart('﻿')).ToList();
        var columns = MapColumns(headers);
        var mapping = columns.ToDictionary(c => c.Key, c => headers[c.Value]);
        var isAlbumList = !columns.ContainsKey(CsvField.Title)
                          && columns.ContainsKey(CsvField.Artist) && columns.ContainsKey(CsvField.Album);

        var result = new CsvInspection { Headers = headers, Mapping = mapping, Delimiter = delimiter, IsAlbumList = isAlbumList };

        if (!columns.ContainsKey(CsvField.Title) && !isAlbumList)
        {
            var found = headers.Count(h => h.Length > 0) == 0 ? "none" : string.Join(", ", headers.Where(h => h.Length > 0).Select(h => $"\"{h}\""));
            result.Errors.Add($"This file has no title column, so ORBIT can't tell which tracks to look for. {ExpectedColumnsText} Columns found: {found}.");
            return result;
        }

        var missingTitleRows = new List<int>();
        var missingArtist = 0;
        var duplicates = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var playlistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var lineNumber = i + 1;

            string? Get(CsvField field) =>
                columns.TryGetValue(field, out var idx) && idx < row.Length && !string.IsNullOrWhiteSpace(row[idx])
                    ? row[idx].Trim()
                    : null;

            var title = Get(CsvField.Title);
            var artist = Get(CsvField.Artist);
            var album = Get(CsvField.Album);

            if (Get(CsvField.Playlist) is { } playlist) playlistNames.Add(playlist);

            if (isAlbumList)
            {
                if (album == null) { missingTitleRows.Add(lineNumber); continue; }
            }
            else if (title == null)
            {
                missingTitleRows.Add(lineNumber);
                continue;
            }

            if (artist == null) missingArtist++;

            var key = $"{artist}\u0001{title}\u0001{(isAlbumList ? album : null)}";
            if (!seen.Add(key)) { duplicates++; continue; }

            int? length = null;
            if (Get(CsvField.DurationMs) is { } ms && TryParseDuration(ms, isMilliseconds: true, out var fromMs)) length = fromMs;
            else if (Get(CsvField.Duration) is { } dur && TryParseDuration(dur, isMilliseconds: false, out var seconds)) length = seconds;

            result.Tracks.Add(new SearchQuery
            {
                Title = isAlbumList ? null : title,
                Artist = artist,
                Album = album,
                Length = length,
                AlbumTrackCount = int.TryParse(Get(CsvField.AlbumTrackCount), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : null,
                ISRC = Get(CsvField.Isrc)?.ToUpperInvariant(),
                SpotifyTrackId = ParseSpotifyTrackId(Get(CsvField.SpotifyId)),
                DownloadMode = isAlbumList ? DownloadMode.Album : DownloadMode.Normal,
                IsEnriched = false
            });
        }

        if (playlistNames.Count == 1) result.PlaylistName = playlistNames.First();

        if (result.Tracks.Count == 0)
        {
            result.Errors.Add(isAlbumList
                ? "No row has an album name."
                : $"No row has a title in the \"{mapping[CsvField.Title]}\" column. {ExpectedColumnsText}");
            return result;
        }

        var inv = CultureInfo.InvariantCulture;
        if (missingTitleRows.Count > 0)
            result.Warnings.Add(string.Create(inv,
                $"Skipped {Rows(missingTitleRows.Count)} without {(isAlbumList ? "an album" : "a title")} ({LineList(missingTitleRows)})."));
        if (duplicates > 0)
            result.Warnings.Add(string.Create(inv, $"Skipped {Rows(duplicates)} listed twice."));
        if (!columns.ContainsKey(CsvField.Artist))
            result.Warnings.Add("There's no artist column, so matches will be less accurate.");
        else if (missingArtist > 0)
            result.Warnings.Add(string.Create(inv, $"{Rows(missingArtist, capitalize: true)} no artist, so {(missingArtist == 1 ? "it" : "they")} may match less accurately."));

        return result;
    }

    private static string Rows(int n, bool capitalize = false) => n == 1
        ? (capitalize ? "1 row has" : "1 row")
        : string.Create(CultureInfo.InvariantCulture, $"{n} rows{(capitalize ? " have" : "")}");

    private static string LineList(List<int> lines)
    {
        var shown = string.Join(", ", lines.Take(5).Select(l => l.ToString(CultureInfo.InvariantCulture)));
        return (lines.Count == 1 ? "line " : "lines ") + shown + (lines.Count > 5 ? ", …" : "");
    }

    /// <summary>Header index per field; the first alias in each list wins over later ones.</summary>
    internal static Dictionary<CsvField, int> MapColumns(IReadOnlyList<string> headers)
    {
        var normalized = headers.Select(Normalize).ToList();
        var result = new Dictionary<CsvField, int>();
        var taken = new HashSet<int>();
        foreach (var (field, aliases) in Aliases)
        {
            foreach (var alias in aliases)
            {
                var idx = normalized.FindIndex(h => h == alias);
                if (idx >= 0 && taken.Add(idx))
                {
                    result[field] = idx;
                    break;
                }
            }
        }
        return result;
    }

    private static string Normalize(string header) =>
        new(header.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// "3:45", "1:02:45", "225", "225.4" → seconds; values in milliseconds (or bare numbers too big
    /// to be seconds) are divided by 1000.
    /// </summary>
    internal static bool TryParseDuration(string value, bool isMilliseconds, out int seconds)
    {
        seconds = 0;
        value = value.Trim();
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            var total = 0.0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p < 0) return false;
                total = total * 60 + p;
            }
            seconds = (int)Math.Round(total);
            return seconds > 0;
        }

        if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return false;
        if (isMilliseconds || number > 3 * 3600) number /= 1000.0;
        seconds = (int)Math.Round(number);
        return seconds > 0;
    }

    /// <summary>Accepts a bare 22-character id, a spotify:track: URI or an open.spotify.com link.</summary>
    internal static string? ParseSpotifyTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        const string uriPrefix = "spotify:track:";
        if (value.StartsWith(uriPrefix, StringComparison.OrdinalIgnoreCase)) return value[uriPrefix.Length..];
        var marker = value.IndexOf("/track/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var id = value[(marker + "/track/".Length)..];
            var end = id.IndexOfAny(new[] { '?', '/', '#' });
            return end >= 0 ? id[..end] : id;
        }
        return value.Length == 22 && value.All(char.IsLetterOrDigit) ? value : null;
    }

    private static List<string[]> ReadRows(string filePath, out char delimiter)
    {
        var text = DecodeText(File.ReadAllBytes(filePath));
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            DetectDelimiter = true,
            DetectDelimiterValues = new[] { ",", ";", "\t", "|" },
            BadDataFound = null,
            MissingFieldFound = null,
            IgnoreBlankLines = false,
        };

        var rows = new List<string[]>();
        using var reader = new StringReader(text);
        using var parser = new CsvParser(reader, config);
        while (parser.Read())
            rows.Add(parser.Record ?? Array.Empty<string>());
        delimiter = parser.Delimiter is { Length: > 0 } d ? d[0] : ',';
        return rows;
    }

    /// <summary>
    /// UTF-8 (with or without BOM) and UTF-16 as declared; otherwise Windows-1252, which is what
    /// Excel's "CSV (comma delimited)" writes and what breaks accents when read as UTF-8.
    /// </summary>
    internal static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }
}
