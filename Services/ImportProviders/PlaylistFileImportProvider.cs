using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// #425: Universal playlist file import provider supporting <c>.m3u</c>, <c>.m3u8</c>, <c>.txt</c>,
/// and <c>.json</c> exports (from Rekordbox, TuneMyMusic, Soundiiz, Apple Music, etc.).
/// </summary>
public sealed class PlaylistFileImportProvider : IStreamingImportProvider
{
    private readonly ILogger<PlaylistFileImportProvider> _logger;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".txt", ".json",
    };

    public string Name => "Playlist File";
    public string IconGlyph => "file";

    public PlaylistFileImportProvider(ILogger<PlaylistFileImportProvider> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim().Trim('"');
        var ext = Path.GetExtension(trimmed);
        return SupportedExtensions.Contains(ext) && File.Exists(trimmed);
    }

    public async Task<ImportResult> ImportAsync(string input)
    {
        var tracks = new List<SearchQuery>();
        var title = string.Empty;
        try
        {
            await foreach (var batch in ImportStreamAsync(input))
            {
                if (string.IsNullOrEmpty(title)) title = batch.SourceTitle;
                tracks.AddRange(batch.Tracks);
            }

            if (tracks.Count == 0)
                return new ImportResult { Success = false, ErrorMessage = "No tracks found in this playlist file." };

            return new ImportResult
            {
                Success = true,
                SourceTitle = title,
                SourceType = Name,
                SourceUrl = input.Trim().Trim('"'),
                Tracks = tracks,
            };
        }
        catch (ImportSourceException ex)
        {
            return new ImportResult { Success = false, ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist file import failed for {Path}", input);
            return new ImportResult { Success = false, ErrorMessage = $"Could not read playlist file: {ex.Message}" };
        }
    }

    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        var path = input.Trim().Trim('"');
        if (!File.Exists(path))
            throw new ImportSourceException($"File not found: {path}");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var title = Path.GetFileNameWithoutExtension(path);

        var tracks = ext switch
        {
            ".m3u" or ".m3u8" => ParseM3u(content),
            ".txt" => CommentTracklistParser.Parse(content, out var detected, out _),
            ".json" => ParseJson(content),
            _ => throw new ImportSourceException($"Unsupported playlist file format: {ext}"),
        };

        if (tracks.Count == 0)
            throw new ImportSourceException($"No tracks could be parsed from '{Path.GetFileName(path)}'.");

        yield return new ImportBatchResult
        {
            Tracks = tracks,
            SourceTitle = string.IsNullOrWhiteSpace(title) ? "Imported Playlist" : title,
            SourceType = ext switch
            {
                ".m3u" or ".m3u8" => "M3U Playlist",
                ".json" => "JSON Playlist",
                _ => "Text Tracklist",
            },
            SourceUrl = path,
            TotalEstimated = tracks.Count,
        };
    }

    public static List<SearchQuery> ParseM3u(string content)
    {
        var tracks = new List<SearchQuery>();
        if (string.IsNullOrWhiteSpace(content)) return tracks;

        var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        string? pendingExtInf = null;
        int? pendingDuration = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = line["#EXTINF:".Length..];
                var comma = rest.IndexOf(',');
                if (comma >= 0)
                {
                    var durPart = rest[..comma].Trim().Split(' ')[0];
                    pendingDuration = int.TryParse(durPart, out var secs) && secs > 0 ? secs : null;
                    pendingExtInf = rest[(comma + 1)..].Trim();
                }
                continue;
            }

            if (line.StartsWith('#')) continue;

            // Track entry line (path or URL)
            var label = !string.IsNullOrWhiteSpace(pendingExtInf)
                ? pendingExtInf
                : Path.GetFileNameWithoutExtension(line);

            var (artist, title) = SplitArtistTitle(label);
            if (!string.IsNullOrWhiteSpace(title))
            {
                tracks.Add(new SearchQuery
                {
                    Artist = artist,
                    Title = title,
                    Length = pendingDuration,
                });
            }

            pendingExtInf = null;
            pendingDuration = null;
        }

        // Handle #EXTINF-only files with no path lines
        if (!string.IsNullOrWhiteSpace(pendingExtInf))
        {
            var (artist, title) = SplitArtistTitle(pendingExtInf);
            if (!string.IsNullOrWhiteSpace(title))
                tracks.Add(new SearchQuery { Artist = artist, Title = title, Length = pendingDuration });
        }

        return tracks;
    }

    public static List<SearchQuery> ParseJson(string content)
    {
        var tracks = new List<SearchQuery>();
        if (string.IsNullOrWhiteSpace(content)) return tracks;

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.ValueKind == JsonValueKind.Object &&
                 (root.TryGetProperty("tracks", out array) || root.TryGetProperty("items", out array)) &&
                 array.ValueKind == JsonValueKind.Array)
        {
            // found wrapper array
        }
        else
        {
            return tracks;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = ReadString(item, "title", "name", "track", "trackName");
            var artist = ReadString(item, "artist", "artistName", "artists");
            var album = ReadString(item, "album", "albumName");
            var duration = ReadInt(item, "duration", "durationSeconds", "length");

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                continue;

            tracks.Add(new SearchQuery
            {
                Artist = artist ?? "Unknown Artist",
                Title = title ?? "Unknown Track",
                Album = album,
                Length = duration > 0 ? duration : null,
            });
        }

        return tracks;
    }

    private static (string Artist, string Title) SplitArtistTitle(string label)
    {
        string[] seps = { " - ", " – ", " — " };
        foreach (var sep in seps)
        {
            var idx = label.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0 && idx + sep.Length < label.Length)
                return (label[..idx].Trim(), label[(idx + sep.Length)..].Trim());
        }
        return ("Unknown Artist", label.Trim());
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (names.Any(n => prop.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            }
        }
        return null;
    }

    private static int ReadInt(JsonElement obj, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (names.Any(n => prop.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var v))
                    return v;
                if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out var s))
                    return s;
            }
        }
        return 0;
    }
}
