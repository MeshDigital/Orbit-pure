using System.IO;
using System.Text.RegularExpressions;
using SLSKDONET.Models;
using SLSKDONET.Utils;

namespace SLSKDONET.Services;

/// <summary>
/// Formats track filenames using template expressions.
/// Supports variables, conditionals, and sanitization.
/// 
/// Examples:
/// - "{artist} - {title}" → "Artist - Title"
/// - "{artist|filename}" → Artist if available, otherwise filename
/// - "{albumartist}/{album}/{track}. {title}"
/// </summary>
public class FileNameFormatter
{
    private const string VariablePattern = @"\{([^}]+)\}";

    /// <summary>
    /// Formats a filename using the provided template.
    /// </summary>
    public string Format(string template, Track track)
    {
        var result = template;

        // Replace variables
        result = Regex.Replace(result, VariablePattern, match =>
        {
            var expr = match.Groups[1].Value;
            return GetVariable(expr, track);
        });

        // Global Fallback Strategy:
        // If the resulting name has no actual alphanumeric content (only separators/spaces)
        // or contains "Unknown - Unknown", we fallback to the uploader's original filename.
        var hasContent = Regex.IsMatch(result, @"[a-zA-Z0-9]");
        if (!hasContent || result.Contains("Unknown - Unknown"))
        {
            if (!string.IsNullOrEmpty(track.Filename))
            {
                return Path.GetFileNameWithoutExtension(track.Filename);
            }
        }

        // Sanitize filename
        result = FileFormattingUtils.SanitizeFilename(result);
        return result;
    }

    /// <summary>
    /// Gets a variable value from track, supporting:
    /// - Simple: {artist}, {title}
    /// - Fallback: {artist|filename}
    /// - Nested: {artist|{filename}}
    /// - Conditional: {artist( - )title} (only add literal if artist is present)
    /// </summary>
    private string GetVariable(string expr, Track track)
    {
        // Handle fallback: var1|var2|var3
        if (expr.Contains('|'))
        {
            var alternatives = expr.Split('|');
            foreach (var alt in alternatives)
            {
                var value = GetSingleVariable(alt.Trim(), track);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
            return "";
        }

        // Handle literal wrapping: tag(literal)
        var match = Regex.Match(expr, @"^(\w+)\(([^)]*)\)$");
        if (match.Success)
        {
            var varName = match.Groups[1].Value;
            var literal = match.Groups[2].Value;
            var value = GetSingleVariable(varName, track);
            return string.IsNullOrEmpty(value) ? "" : value + literal;
        }

        return GetSingleVariable(expr, track);
    }

    /// <summary>
    /// Gets a single variable value.
    /// </summary>
    private string GetSingleVariable(string varName, Track track)
    {
        return varName.ToLower() switch
        {
            "artist" => track.Artist ?? "",
            "title" => track.Title ?? "",
            "album" => track.Album ?? "",
            "filename" => Path.GetFileNameWithoutExtension(track.Filename) ?? "",
            "bitrate" => track.Bitrate.ToString(),
            "bpm" => track.BPM?.ToString("0") ?? "",
            "key" => track.MusicalKey ?? "",
            "energy" => (track.Energy * 100)?.ToString("0") ?? "",
            "format" => track.GetExtension(),
            "length" => track.Length?.ToString() ?? "",
            "user" or "username" => track.Username ?? "",
            "size" => track.GetFormattedSize(),
            _ => ""
        };
    }

    /// <summary>
    /// Generates a default filename if format fails.
    /// </summary>
    public string GetFallbackName(Track track)
    {
        var artist = track.Artist ?? "Unknown";
        var title = track.Title ?? "Unknown";
        return $"{artist} - {title}";
    }
}

/// <summary>
/// Performs search query normalization.
/// Removes unwanted text patterns that could hurt search results.
/// </summary>
public class SearchQueryNormalizer
{
    /// <summary>
    /// Removes "feat." artists and variations.
    /// </summary>
    public string RemoveFeatArtists(string query)
    {
        return Regex.Replace(query, @"\s*(?:feat\.?|ft\.?|featuring)\s+.*$", "", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Applies regex replacement to query.
    /// Format: "pattern" or "pattern;replacement"
    /// </summary>
    public string ApplyRegex(string query, string regexPattern)
    {
        var parts = regexPattern.Split(';', 2);
        var pattern = parts[0];
        var replacement = parts.Length > 1 ? parts[1] : "";

        try
        {
            return Regex.Replace(query, pattern, replacement, RegexOptions.IgnoreCase);
        }
        catch (RegexParseException ex)
        {
            throw new InvalidOperationException($"Invalid regex pattern: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Removes common YouTube video markers.
    /// </summary>
    public string RemoveYoutubeMarkers(string query)
    {
        // Remove things like (Video), [Official], Lyrics, etc.
        query = Regex.Replace(query, @"\s*[\[\(].*?[\]\)]", "");
        query = Regex.Replace(query, @"\s+(?:official|lyrics|visualizer|audio|clip).*$", "", RegexOptions.IgnoreCase);
        return query.Trim();
    }
}
