using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Models;
using SLSKDONET.Services.Library.Files;

namespace SLSKDONET.Services;

/// <summary>
/// Formats track filenames using template expressions.
/// Token resolution and sanitisation are delegated to <see cref="PathTemplateEngine"/> (#343, LF1).
/// </summary>
public class FileNameFormatter
{
    /// <summary>
    /// Renders a template to a single sanitised filename (folder separators are flattened out).
    /// </summary>
    public string Format(string template, Track track)
    {
        var rendered = PathTemplateEngine.Render(template, ToFacts(track));
        return ApplyFallback(rendered.Leaf, track);
    }

    /// <summary>
    /// Renders a template to a sanitised relative path ('/' joined). Callers that build sub-folders
    /// must split on '/' / '\' and use the segments directly — they are already sanitised (#341).
    /// </summary>
    internal string FormatTemplate(string template, Track track)
    {
        var rendered = PathTemplateEngine.Render(template, ToFacts(track));
        return ApplyFallback(rendered.RelativePath, track);
    }

    private static TrackFacts ToFacts(Track track) => new(
        Artist: track.Artist,
        Title: track.Title,
        Album: track.Album,
        Bpm: track.BPM,
        Key: track.MusicalKey,
        Energy: track.Energy,
        Format: track.GetExtension(),
        Bitrate: track.Bitrate,
        User: track.Username,
        Filename: string.IsNullOrEmpty(track.Filename) ? null : Path.GetFileNameWithoutExtension(track.Filename),
        LengthSeconds: track.Length,
        SizeFormatted: track.GetFormattedSize());

    /// <summary>
    /// Legacy fallback: when the template yields no usable content, use the uploader's original
    /// filename instead of an empty or separator-only name.
    /// </summary>
    private static string ApplyFallback(string rendered, Track track)
    {
        if (!rendered.Any(char.IsLetterOrDigit) || rendered.Contains("Unknown - Unknown"))
        {
            if (!string.IsNullOrEmpty(track.Filename))
                return Path.GetFileNameWithoutExtension(track.Filename);
        }
        return rendered;
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
