using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Models;

namespace SLSKDONET.Utils;

/// <summary>
/// Utility for parsing tracklists from YouTube comments, SoundCloud descriptions, etc.
/// Removes timestamps, filters junk lines, and extracts artist/title pairs.
/// </summary>
public static class CommentTracklistParser
{
    // Matches timestamps like: 0:00, 00:00, 1:00:00, (00:00), [00:00]
    private static readonly Regex TimestampRegex = new(@"\s*[\[\(]?\d{1,2}:\d{2}(:\d{2})?[\]\)]?\s*", RegexOptions.Compiled);
    private static readonly Regex TimestampOnlyRegex = new(@"^[\[\(]?\d{1,2}:\d{2}(:\d{2})?[\]\)]?$", RegexOptions.Compiled);
    
    // Matches artist/title separator (supports: -, –, —, |, :, •)
    private static readonly Regex SeparatorRegex = new(@"\s*([-–—|:•]|(?<=\S)\s{2,}(?=\S))\s*", RegexOptions.Compiled);

    // 1001Tracklists often appends record label in ALL CAPS at end.
    private static readonly Regex TrailingLabelRegex = new(@"\s+[A-Z0-9][A-Z0-9 '&/().-]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex TrailingBracketLabelRegex = new(@"\s+\[[A-Z0-9][A-Z0-9 '&/().-]{1,40}\]$", RegexOptions.Compiled);
    private static readonly Regex LeadingMixMarkerRegex = new(@"^\s*w\/?\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Keywords that indicate junk lines
    private static readonly string[] JunkKeywords = 
    { 
        "tracklist", 
        "setlist", 
        "playlist", 
        "📈", 
        "🎵", 
        "🎶",
        "track list",
        "timestamps",
        "artwork",
        "artwork placeholder",
        "pre-save",
        "save ",
        "tracklist actions",
        "export to spotify",
        "add a (live) video",
        "like this tracklist"
    };

    /// <summary>
    /// Parse raw tracklist text into SearchQuery objects.
    /// </summary>
    /// <param name="rawText">Raw text containing tracklist (e.g., from YouTube comment)</param>
    /// <returns>List of parsed tracks</returns>
    public static List<SearchQuery> Parse(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new List<SearchQuery>();

        var tracks = new List<SearchQuery>();
        var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var previousTrackKey = string.Empty;
        bool previousLineWasTimestamp = false;

        foreach (var line in lines)
        {
            var original = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(original))
                continue;

            // In 1001Tracklists blocks, a timestamp line is often followed by "Artist - Title ...".
            if (IsTimestampOnly(original))
            {
                previousLineWasTimestamp = true;
                continue;
            }

            var cleaned = RemoveTimestamp(original).Trim();

            if (IsJunkLine(cleaned) || string.IsNullOrWhiteSpace(cleaned))
            {
                previousLineWasTimestamp = false;
                continue;
            }

            // Strong signal: explicit artist/title separator.
            // Secondary signal: line follows a timestamp and still contains separator.
            bool isTrackCandidate = HasArtistTitleSeparator(cleaned) || (previousLineWasTimestamp && HasArtistTitleSeparator(cleaned));
            previousLineWasTimestamp = false;
            if (!isTrackCandidate)
                continue;

            var (artist, title) = SplitArtistTitle(cleaned);
            var (rawArtist, rawTitle) = SplitRaw(cleaned);

            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
                continue;

            var key = $"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}";
            if (key == previousTrackKey)
                continue;

            previousTrackKey = key;
            tracks.Add(new SearchQuery
            {
                Artist = artist.Trim(),
                Title = title.Trim(),
                // Store raw values so the preview UI can show a "cleaned" badge when transforms changed content.
                OriginalArtist = string.IsNullOrWhiteSpace(rawArtist) ? null : rawArtist.Trim(),
                OriginalTitle = string.IsNullOrWhiteSpace(rawTitle) ? null : rawTitle.Trim(),
                Album = null // No album info from pasted tracklist blocks
            });
        }

        return tracks;
    }

    /// <summary>
    /// Remove leading timestamp from a line.
    /// Handles formats: 0:00, 00:00, 1:00:00, with optional dash
    /// </summary>
    private static string RemoveTimestamp(string line)
    {
        return TimestampRegex.Replace(line, string.Empty);
    }

    /// <summary>
    /// Check if a line is junk (header, metadata, etc.)
    /// </summary>
    private static bool IsJunkLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        var lowerLine = line.ToLowerInvariant();
        
        // Check for junk keywords
        if (JunkKeywords.Any(keyword => lowerLine.Contains(keyword.ToLowerInvariant())))
            return true;
        
        // Filter lines that are just track numbers or tiny counters.
        if (Regex.IsMatch(lowerLine.Trim(), @"^\d{1,3}$"))
            return true;

        // Filter common UI tokens from copied webpages.
        if (lowerLine.Trim() == "w/" || lowerLine.Trim() == "w")
            return true;

        // Keep "ID - ID" track placeholders (they are valid unknown entries),
        // but filter standalone "id" tags.
        if (lowerLine.Trim() == "id")
            return true;
        
        // Filter lines that are too short (likely not a track)
        if (line.Length < 3)
            return true;

        return false;
    }

    /// <summary>
    /// Split a line into artist and title without applying emoji/symbol removal.
    /// Used to capture the raw original values before sanitization.
    /// </summary>
    private static (string Artist, string Title) SplitRaw(string line)
    {
        var normalized = StripLeadingMixMarker(line);
        var parts = SeparatorRegex.Split(normalized, 2);
        if (parts.Length == 2)
            return (parts[0].Trim(), parts[1].Trim());
        if (parts.Length == 1)
            return ("Unknown Artist", parts[0].Trim());
        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// Split a cleaned line into artist and title.
    /// Handles edge cases like multiple hyphens in the title.
    /// </summary>
    private static (string Artist, string Title) SplitArtistTitle(string line)
    {
        var normalized = StripLeadingMixMarker(line);

        // Remove emojis and special icons (❎, ❌, ‼, ❗, etc.)
        var cleaned = RemoveEmojis(normalized);
        
        // Split on first separator only (to handle titles with hyphens)
        var parts = SeparatorRegex.Split(cleaned, 2);
        
        if (parts.Length == 2)
        {
            var artist = parts[0].Trim();
            var title = StripTrailingLabel(parts[1].Trim());
            return (artist, title);
        }
        else if (parts.Length == 1)
        {
            // No separator found - assume it's just a title
            return ("Unknown Artist", StripTrailingLabel(parts[0].Trim()));
        }
        
        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// Remove emojis and special Unicode icons from text using Regex.
    /// </summary>
    private static string RemoveEmojis(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Common symbols and emojis used in tracklists
        // This regex covers a wide range of symbols, emojis, and math operators used as bullets
        var cleaned = Regex.Replace(text, @"[\u2700-\u27BF]|[\uE000-\uF8FF]|\uD83C[\uDF00-\uDFFF]|\uD83D[\uDC00-\uDDFF]|[\u2011-\u26FF]|\uD83E[\uDD10-\uDDFF]", string.Empty);
        
        // Also remove specific common markers
        string[] markers = { "✅", "❌", "❎", "✓", "✔", "✗", "✘", "⭐", "❗", "‼", "▶", "⏸" };
        foreach (var marker in markers)
        {
            cleaned = cleaned.Replace(marker, string.Empty);
        }

        return cleaned.Trim();
    }

    private static bool IsTimestampOnly(string line)
    {
        return TimestampOnlyRegex.IsMatch(line.Trim());
    }

    private static bool HasArtistTitleSeparator(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.Contains(" - ", StringComparison.Ordinal) ||
               line.Contains(" – ", StringComparison.Ordinal) ||
               line.Contains(" — ", StringComparison.Ordinal) ||
               line.Contains("|", StringComparison.Ordinal);
    }

    private static string StripTrailingLabel(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title;

        var trimmed = title.Trim();

        // Remove bracketed labels like [BLACKOUT MUSIC], [VERTIGO (UMG)].
        var bracketMatch = TrailingBracketLabelRegex.Match(trimmed);
        if (bracketMatch.Success)
        {
            trimmed = trimmed[..^bracketMatch.Value.Length].Trim();
        }

        // Don't over-clean very short titles.
        if (trimmed.Length < 8)
            return trimmed;

        var match = TrailingLabelRegex.Match(trimmed);
        if (!match.Success)
            return trimmed;

        // Keep title suffixes that are only a short parenthetical, e.g. "(VIP)", "(Remix)".
        // But remove tails like "VERTIGO (UMG)" where an uppercase label name is present.
        var suffix = match.Value.Trim();
        if (suffix.StartsWith("(", StringComparison.Ordinal) &&
            suffix.EndsWith(")", StringComparison.Ordinal) &&
            suffix.Length <= 16)
            return trimmed;

        return trimmed[..^match.Value.Length].Trim();
    }

    private static string StripLeadingMixMarker(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return LeadingMixMarkerRegex.Replace(value.Trim(), string.Empty);
    }
}
