using System.IO;
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

/// <summary>
/// Parses CSV files containing track information.
/// Automatically detects common column names or uses specified names.
/// </summary>
public class CsvInputSource : IInputSource
{
    public InputType InputType => InputType.CSV;

    private string? _artistCol = "artist";
    private string? _titleCol = "title";
    private string? _albumCol = "album";
    private string? _lengthCol = "length";
    private string? _albumTrackCountCol = "album-track-count";

    /// <summary>
    /// Parses a CSV file into search queries.
    /// Rows without a title are treated as album downloads.
    /// </summary>
    public async Task<List<SearchQuery>> ParseAsync(string filePath)
    {
        var queries = new List<SearchQuery>();

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"CSV file not found: {filePath}");

        try
        {
            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true
            });

            // Read header and auto-detect column names
            await csv.ReadAsync();
            csv.ReadHeader();
            var headers = csv.HeaderRecord?.Select(h => h?.ToLower() ?? "").Where(h => !string.IsNullOrEmpty(h)) ?? new List<string>();
            DetectColumnNames(headers);

            // Read data rows
            while (await csv.ReadAsync())
            {
                var record = csv.GetRecord<dynamic>();
                var query = ParseRow(record);
                if (query != null)
                    queries.Add(query);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error parsing CSV file: {ex.Message}", ex);
        }

        return queries;
    }

    /// <summary>
    /// Auto-detects column names from CSV header.
    /// </summary>
    private void DetectColumnNames(IEnumerable<string> headers)
    {
        var headerList = headers.ToList();

        _artistCol = FindColumn(headerList, "artist", "artist", "performer");
        _titleCol = FindColumn(headerList, "title", "track", "song", "name");
        _albumCol = FindColumn(headerList, "album", "album name");
        _lengthCol = FindColumn(headerList, "length", "duration", "time");
        _albumTrackCountCol = FindColumn(headerList, "album-track-count", "track-count", "album-tracks");
    }

    /// <summary>
    /// Finds a column by trying multiple possible names.
    /// </summary>
    private string? FindColumn(List<string> headers, params string[] possibleNames)
    {
        foreach (var name in possibleNames)
        {
            var match = headers.FirstOrDefault(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Parses a CSV row into a SearchQuery.
    /// </summary>
    private SearchQuery? ParseRow(dynamic record)
    {
        try
        {
            // Normalize to case-insensitive lookup so headers like "Song"/"Artist" work.
            var original = (IDictionary<string, object>)record;
            var dict = new Dictionary<string, object>(original, StringComparer.OrdinalIgnoreCase);

            var title = GetValue(dict, _titleCol);
            var artist = GetValue(dict, _artistCol);
            var album = GetValue(dict, _albumCol);
            var length = GetIntValue(dict, _lengthCol);
            var trackCount = GetIntValue(dict, _albumTrackCountCol);

            // Skip empty rows
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(album))
                return null;

            // Rows without title are album downloads
            var mode = string.IsNullOrEmpty(title) ? DownloadMode.Album : DownloadMode.Normal;

            return new SearchQuery
            {
                Title = title,
                Artist = artist,
                Album = album,
                Length = length,
                AlbumTrackCount = trackCount,
                DownloadMode = mode,
                IsEnriched = false
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error parsing CSV row: {ex.Message}", ex);
        }
    }

    private string? GetValue(IDictionary<string, object> dict, string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        if (dict.TryGetValue(key, out var value) && value != null)
            return value.ToString()?.Trim();

        return null;
    }

    private int? GetIntValue(IDictionary<string, object> dict, string? key)
    {
        var value = GetValue(dict, key);
        if (int.TryParse(value, out var result))
            return result;
        return null;
    }
}

/// <summary>
/// Input source for direct search strings.
/// </summary>
public class StringInputSource : IInputSource
{
    public InputType InputType => InputType.String;

    public Task<List<SearchQuery>> ParseAsync(string input)
    {
        var query = SearchQuery.Parse(input);
        return Task.FromResult(new List<SearchQuery> { query });
    }
}

/// <summary>
/// Input source for list files (multiple queries, one per line).
/// Each line can optionally include conditions.
/// </summary>
public class ListInputSource : IInputSource
{
    public InputType InputType => InputType.List;

    /// <summary>
    /// Parses a list file with format:
    /// "Artist - Title" [conditions] [pref-conditions]
    /// a:"Album Name" [conditions]
    /// </summary>
    public async Task<List<SearchQuery>> ParseAsync(string filePath)
    {
        var queries = new List<SearchQuery>();

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"List file not found: {filePath}");

        var lines = await File.ReadAllLinesAsync(filePath);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;

            // Parse line: input [conditions] [pref-conditions]
            var parts = trimmed.Split(new[] { '"' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
                continue;

            var input = parts[0].Trim();
            var mode = input.StartsWith("a:") ? DownloadMode.Album : DownloadMode.Normal;

            if (input.StartsWith("a:"))
                input = input.Substring(2).Trim();

            var query = SearchQuery.Parse(input, mode);
            queries.Add(query);
        }

        return queries;
    }
}
