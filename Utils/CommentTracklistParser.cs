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
    // Matches a timestamp prefix like: 0:00, 00:00, 1:00:00, (00:00), [00:00],
    // optionally followed by a separator commonly used in tracklists.
    private static readonly Regex LeadingTimestampPrefixRegex = new(@"^\s*[\[\(]?\d{1,2}:\d{2}(?::\d{2})?[\]\)]?\s*(?:[-–—|:•]\s*)?", RegexOptions.Compiled);
    private static readonly Regex TimestampOnlyRegex = new(@"^[\[\(]?\d{1,2}:\d{2}(:\d{2})?[\]\)]?$", RegexOptions.Compiled);

    // Matches a numbered-list prefix like "05. ", "12) ", "3. " on tracklist pastes that number
    // their entries instead of (or alongside) timestamping them, e.g. "05. Grafix ft. Nu-La -
    // Vital Signs". Requires "." or ")" directly after the digits so it never eats a genuine
    // artist name that merely starts with a number (e.g. "21 Savage", "50 Cent").
    private static readonly Regex LeadingTrackNumberPrefixRegex = new(@"^\s*\d{1,3}[.)]\s+", RegexOptions.Compiled);
    
    // Matches artist/title separator (supports: -, –, —, |, :, •). Dash-type separators require
    // real whitespace on both sides — without the lookarounds, a bare "-" inside a hyphenated
    // artist name (e.g. "Nu-La", "Jay-Z", "K-391") was itself treated as the split point, silently
    // truncating the artist and shoving the rest of the name onto the title.
    private static readonly Regex SeparatorRegex = new(@"\s*(?:(?<=\s)[-–—](?=\s)|[|:•]|(?<=\S)\s{2,}(?=\S))\s*", RegexOptions.Compiled);

    // 1001Tracklists often appends record label in ALL CAPS at end.
    private static readonly Regex TrailingLabelRegex = new(@"\s+[A-Z0-9][A-Z0-9 '&/().-]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex TrailingBracketLabelRegex = new(@"\s+\[[A-Z0-9][A-Z0-9 '&/().-]{1,40}\]$", RegexOptions.Compiled);
    private static readonly Regex LeadingMixMarkerRegex = new(@"^\s*w\/?\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 1001Tracklists (and similar sites) end the paste with a "keep this tracklist up-to-date"
    // backlink — capture it so it can be stored on the playlist for later reference.
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);
    
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
        "tracklist actions",
        "export to spotify",
        "add a (live) video",
        "like this tracklist",
        "please set a backlink",
        "keep the tracklist up-to-date"
    };

    /// <summary>
    /// Parse raw tracklist text into SearchQuery objects.
    /// </summary>
    /// <param name="rawText">Raw text containing tracklist (e.g., from YouTube comment)</param>
    /// <returns>List of parsed tracks</returns>
    public static List<SearchQuery> Parse(string rawText) => Parse(rawText, out _);

    /// <summary>
    /// Parse raw tracklist text into SearchQuery objects, also reporting a detected playlist title.
    /// </summary>
    /// <param name="rawText">Raw text containing tracklist (e.g., from a 1001Tracklists paste)</param>
    /// <param name="detectedTitle">
    /// The first line of the pasted text, if present and not itself parsed as a track — 1001Tracklists
    /// and similar sources conventionally open with a "DJ @ Event Name Date" header line above the
    /// track entries. Null if the input has no such leading non-track line.
    /// </param>
    public static List<SearchQuery> Parse(string rawText, out string? detectedTitle) =>
        Parse(rawText, out detectedTitle, out _);

    /// <summary>
    /// Parse raw tracklist text into SearchQuery objects, also reporting a detected playlist title
    /// and a detected source URL (e.g. a 1001Tracklists backlink).
    /// </summary>
    /// <param name="rawText">Raw text containing tracklist (e.g., from a 1001Tracklists paste)</param>
    /// <param name="detectedTitle">
    /// The first line of the pasted text, if present and not itself parsed as a track — 1001Tracklists
    /// and similar sources conventionally open with a "DJ @ Event Name Date" header line above the
    /// track entries. Null if the input has no such leading non-track line.
    /// </param>
    /// <param name="detectedSourceUrl">
    /// The first URL found anywhere in the pasted text (e.g. 1001Tracklists' own "keep this
    /// tracklist up-to-date" backlink) so the caller can persist it for later reference. Null if
    /// the input contains no URL.
    /// </param>
    /// <param name="onLine">
    /// Optional per-line audit callback, invoked once for every non-blank input line with what it
    /// became (or why it was dropped) — the Engine Diagnostics import audit trail. Not invoked for
    /// CSV-shortcut input (a different, much simpler column-mapping path) or blank lines.
    /// </param>
    public static List<SearchQuery> Parse(string rawText, out string? detectedTitle, out string? detectedSourceUrl, Action<ImportLineAuditEntry>? onLine = null)
    {
        detectedTitle = null;
        detectedSourceUrl = null;

        if (string.IsNullOrWhiteSpace(rawText))
            return new List<SearchQuery>();

        // Try CSV detection first — if the input has a recognizable header row, map columns directly.
        var csvResult = TryCsvParse(rawText);
        if (csvResult != null)
            return csvResult;

        var tracks = new List<SearchQuery>();
        var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // A paste that uses timestamps anywhere is almost certainly a timed tracklist (1001Tracklists,
        // YouTube comment, etc.) where every real track line carries a timestamp. That lets us tell a
        // header line like "Friction - Elevate: Live 005 2026-07-16" (no timestamp, but still contains
        // a "-" separator) apart from an actual track — see the first-line check below.
        bool inputHasAnyTimestampSignal = lines.Any(l =>
        {
            var trimmed = (l ?? string.Empty).Trim();
            return LeadingTimestampPrefixRegex.IsMatch(trimmed) || TimestampOnlyRegex.IsMatch(trimmed);
        });

        var previousTrackKey = string.Empty;
        bool previousLineWasTimestamp = false;
        bool sawAnyNonBlankLine = false;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var original = (lines[lineIndex] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(original))
                continue;

            if (detectedSourceUrl is null)
            {
                var urlMatch = UrlRegex.Match(original);
                if (urlMatch.Success)
                    detectedSourceUrl = urlMatch.Value.TrimEnd('.', ',', ')', ']');
            }

            // In 1001Tracklists blocks, a timestamp line is often followed by "Artist - Title ...".
            if (IsTimestampOnly(original))
            {
                previousLineWasTimestamp = true;
                sawAnyNonBlankLine = true;
                onLine?.Invoke(new ImportLineAuditEntry(lineIndex + 1, original, ImportLineOutcome.TimestampMarker));
                continue;
            }

            var (cleaned, hadLeadingTimestamp) = StripLeadingTimestamp(original);
            cleaned = cleaned.Trim();
            cleaned = StripLeadingTrackNumber(cleaned);

            if (IsJunkLine(cleaned) || string.IsNullOrWhiteSpace(cleaned))
            {
                previousLineWasTimestamp = false;
                sawAnyNonBlankLine = true;
                onLine?.Invoke(new ImportLineAuditEntry(lineIndex + 1, original, ImportLineOutcome.DroppedJunk));
                continue;
            }

            // Strong signal: explicit artist/title separator.
            // Also accept title-only lines when they carry a leading timestamp prefix.
            bool hasSeparator = HasArtistTitleSeparator(cleaned);
            bool hasTimestampSignal = hadLeadingTimestamp || previousLineWasTimestamp;
            bool isTrackCandidate = hasSeparator || hasTimestampSignal;

            // The very first content line of a timestamped paste is the header/title even when it
            // contains a "-" separator (e.g. "Friction - Elevate: Live 005 2026-07-16") — real tracks
            // in this format always carry a timestamp, so a timestamp-less first line never is one.
            if (isTrackCandidate && !sawAnyNonBlankLine && inputHasAnyTimestampSignal && !hasTimestampSignal)
                isTrackCandidate = false;

            previousLineWasTimestamp = false;

            if (!isTrackCandidate)
            {
                // The very first non-blank, non-junk, non-track line is almost always the
                // pasted source's own title/header (e.g. "Kanine @ Summer Essentials Vol. 8 2026-06-29").
                if (detectedTitle is null && !sawAnyNonBlankLine)
                {
                    detectedTitle = original;
                    onLine?.Invoke(new ImportLineAuditEntry(lineIndex + 1, original, ImportLineOutcome.DetectedAsTitle));
                }
                else
                {
                    onLine?.Invoke(new ImportLineAuditEntry(lineIndex + 1, original, ImportLineOutcome.DroppedNotATrack));
                }

                sawAnyNonBlankLine = true;
                continue;
            }

            sawAnyNonBlankLine = true;

            var (artist, title) = hasSeparator
                ? SplitArtistTitle(cleaned)
                : ("Unknown Artist", NormalizeTitleOnly(cleaned));

            var (rawArtist, rawTitle) = hasSeparator
                ? SplitRaw(cleaned)
                : (string.Empty, cleaned);

            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                onLine?.Invoke(new ImportLineAuditEntry(lineIndex + 1, original, ImportLineOutcome.DroppedEmptyAfterSplit));
                continue;
            }

            // "ID" is DJ-tracklist shorthand for an unidentified track — there's nothing to search
            // for, so drop it rather than queuing a doomed download. Covers "Artist - ID",
            // "ID - ID", "Artist - ID (Deeper)"/"ID (VIP)" style qualified variants, AND a real
            // song title with an unidentified-remixer qualifier like "Song (ID Remix)" — see
            // IsUnidentifiedTitle for why that last case is unfindable too, not just the first two.
            if (IsUnidentifiedTitle(title))
            {
                onLine?.Invoke(new ImportLineAuditEntry(lineIndex + 1, original, ImportLineOutcome.DroppedId, artist, title));
                continue;
            }

            var key = $"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}";
            if (key == previousTrackKey)
            {
                onLine?.Invoke(new ImportLineAuditEntry(lineIndex + 1, original, ImportLineOutcome.DroppedDuplicate, artist, title));
                continue;
            }

            previousTrackKey = key;
            var finalArtist = artist.Trim();
            var finalTitle = title.Trim();
            var finalOriginalArtist = string.IsNullOrWhiteSpace(rawArtist) ? null : rawArtist.Trim();
            var finalOriginalTitle = string.IsNullOrWhiteSpace(rawTitle) ? null : rawTitle.Trim();

            tracks.Add(new SearchQuery
            {
                Artist = finalArtist,
                Title = finalTitle,
                // Store raw values so the preview UI can show a "cleaned" badge when transforms changed content.
                OriginalArtist = finalOriginalArtist,
                OriginalTitle = finalOriginalTitle,
                Album = null // No album info from pasted tracklist blocks
            });

            onLine?.Invoke(new ImportLineAuditEntry(lineIndex + 1, original, ImportLineOutcome.Kept, finalArtist, finalTitle, finalOriginalArtist, finalOriginalTitle));
        }

        return tracks;
    }

    private static readonly Regex TrailingQualifierGroupRegex = new(@"\s*[\(\[]([^\(\)\[\]]*)[\)\]]\s*$", RegexOptions.Compiled);
    private static readonly Regex StandaloneIdTokenRegex = new(@"(?<![A-Za-z0-9])ID(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// True if a title is DJ-tracklist / 1001Tracklists shorthand for "unidentified" — either bare
    /// "ID" ("ID (Deeper)", "ID (VIP)"), or a real song title whose version qualifier says the
    /// remixer/editor is unidentified, e.g. "Born Slippy (ID Remix)", "The Weekend (ID Remix)".
    /// There is no producer literally named "ID" — on 1001Tracklists "ID" always means "identity
    /// pending," so an "(ID Remix)"/"(ID Edit)"/"(ID Flip)" is an unofficial edit that was never
    /// released under a findable name and is "damn sure not the original track" if searched for
    /// under the base title. Contrast with "(SKIYE Remix)" or "(Wilkinson Remix)" — a real, named
    /// producer, which stays a legitimate, findable release.
    /// </summary>
    public static bool IsUnidentifiedTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        var trimmed = title.Trim();
        if (trimmed.Equals("ID", StringComparison.OrdinalIgnoreCase)) return true;

        var match = TrailingQualifierGroupRegex.Match(trimmed);
        if (!match.Success) return false;

        // "ID" anywhere inside the trailing qualifier ("(ID Remix)", "(Some Artist & ID Flip)")
        // means the edit itself is unidentified, regardless of how well-known the base title is.
        if (StandaloneIdTokenRegex.IsMatch(match.Groups[1].Value)) return true;

        // Or the base title, once the qualifier is stripped, is itself just "ID" — e.g. "ID (Deeper)".
        var withoutQualifier = trimmed[..match.Index].Trim();
        return withoutQualifier.Equals("ID", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Cleaned, bool HadLeadingTimestamp) StripLeadingTimestamp(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return (line, false);

        var match = LeadingTimestampPrefixRegex.Match(line);
        if (!match.Success)
            return (line, false);

        var cleaned = line[match.Length..];
        return (cleaned, true);
    }

    private static string StripLeadingTrackNumber(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;

        var match = LeadingTrackNumberPrefixRegex.Match(line);
        return match.Success ? line[match.Length..].TrimStart() : line;
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

        // A bare URL (e.g. a "keep this tracklist up-to-date" backlink) is never a track title.
        if (lowerLine.Contains("http://") || lowerLine.Contains("https://"))
            return true;
        
        // Filter lines that are just track numbers or tiny counters.
        if (Regex.IsMatch(lowerLine.Trim(), @"^\d{1,3}$"))
            return true;

        // Filter common UI tokens from copied webpages.
        if (lowerLine.Trim() == "w/" || lowerLine.Trim() == "w")
            return true;

        // A bare "id" line (no separator at all) is junk on its own; "Artist - ID" /
        // "ID - ID" lines are filtered later, once split, since they still need to run
        // through the artist/title separator logic first.
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
        if (TrySplitFirstSeparator(normalized, out var rawArtist, out var rawTitle))
            return (rawArtist, rawTitle);

        if (!string.IsNullOrWhiteSpace(normalized))
            return ("Unknown Artist", normalized.Trim());

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

        if (TrySplitFirstSeparator(cleaned, out var artist, out var title))
        {
            return (artist, StripTrailingLabel(title));
        }

        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            // No separator found - assume it's just a title
            return ("Unknown Artist", StripTrailingLabel(cleaned.Trim()));
        }

        return (string.Empty, string.Empty);
    }

    private static bool TrySplitFirstSeparator(string value, out string artist, out string title)
    {
        artist = string.Empty;
        title = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = SeparatorRegex.Match(value);
        if (!match.Success)
            return false;

        artist = value[..match.Index].Trim();
        title = value[(match.Index + match.Length)..].Trim();
        return true;
    }

    private static string NormalizeTitleOnly(string line)
    {
        var normalized = StripLeadingMixMarker(line);
        var withoutEmojis = RemoveEmojis(normalized);
        return StripTrailingLabel(withoutEmojis.Trim());
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

    // Matches "Artist: Title" but not pure timestamps like "0:00" or numbered items like "1:"
    private static readonly Regex ColonSeparatorRegex = new(@"(?<!\d)\s*:\s+(?!\d)", RegexOptions.Compiled);

    private static bool HasArtistTitleSeparator(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.Contains(" - ", StringComparison.Ordinal) ||
               line.Contains(" – ", StringComparison.Ordinal) ||
               line.Contains(" — ", StringComparison.Ordinal) ||
               line.Contains("|", StringComparison.Ordinal) ||
               ColonSeparatorRegex.IsMatch(line);
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

    /// <summary>
    /// Attempts to parse CSV-formatted input with an auto-detected artist/title header.
    /// Returns null if the input does not look like CSV.
    /// </summary>
    private static List<SearchQuery>? TryCsvParse(string rawText)
    {
        var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;

        // Detect delimiter: tab or comma
        var firstLine = lines[0];
        char delimiter = firstLine.Contains('\t') ? '\t' : ',';

        // Need at least 2 columns and the first row must look like a header (non-numeric columns)
        var headerCols = SplitCsvLine(firstLine, delimiter);
        if (headerCols.Length < 2) return null;

        // Must have at least one numeric data row to be CSV (not just a separator line)
        var secondCols = SplitCsvLine(lines[1], delimiter);
        if (secondCols.Length < 2) return null;

        // Map header column names to artist/title indices
        int artistIdx = -1, titleIdx = -1;
        for (int i = 0; i < headerCols.Length; i++)
        {
            var h = headerCols[i].Trim().ToLowerInvariant().Trim('"', '\'');
            if (artistIdx < 0 && (h == "artist" || h == "artist name" || h == "artists"))
                artistIdx = i;
            if (titleIdx < 0 && (h is "title" or "song" or "track" or "track name" or "song name" or "name"))
                titleIdx = i;
        }

        if (artistIdx < 0 || titleIdx < 0) return null;

        var results = new List<SearchQuery>();
        for (int r = 1; r < lines.Length; r++)
        {
            var cols = SplitCsvLine(lines[r], delimiter);
            if (cols.Length <= Math.Max(artistIdx, titleIdx)) continue;

            var artist = cols[artistIdx].Trim().Trim('"', '\'');
            var title = cols[titleIdx].Trim().Trim('"', '\'');

            if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) continue;

            results.Add(new SearchQuery
            {
                Artist = artist,
                Title = title
            });
        }

        return results.Count > 0 ? results : null;
    }

    private static string[] SplitCsvLine(string line, char delimiter)
    {
        // Simple CSV split respecting quoted fields
        var fields = new List<string>();
        bool inQuote = false;
        var current = new System.Text.StringBuilder();

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (ch == delimiter && !inQuote)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
