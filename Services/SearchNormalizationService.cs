using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;

namespace SLSKDONET.Services;

/// <summary>
/// Phase 4.6 Hotfix: Search String Normalization Service.
/// Fixes critical bug where musical identity (VIP, Remix, feat) was stripped or truncated.
/// Prevents search strings like "Break Wait for You (" from breaking Soulseek queries.
/// </summary>
public class SearchNormalizationService
{
    private readonly ILogger<SearchNormalizationService> _logger;

    private static readonly HashSet<string> ArtistStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the",
        "a",
        "an",
        "unknown",
        "unknown artist",
        "various",
        "various artists",
        "va"
    };

    private static readonly string[] RelaxedTitleNoisePatterns =
    {
        @"\boriginal mix\b",
        @"\bextended mix\b",
        @"\bradio edit\b",
        @"\bclub mix\b",
        @"\boriginal version\b",
        @"\bfeat\.?\s+[^\-\(\)\[\]]+",
        @"\bft\.?\s+[^\-\(\)\[\]]+",
        @"\bfeaturing\s+[^\-\(\)\[\]]+",
        @"\bremix\b",
        @"\bbootleg\b",
        @"\bdub\b",
        @"\bedit\b"
    };

    // Musical Identity Patterns (KEEP these - they define the track version)
    private static readonly string[] MusicalIdentityPatterns = new[]
    {
        @"\(.*?VIP.*?\)",
        @"\(.*?Remix.*?\)",
        @"\(.*?Original Mix.*?\)",
        @"\(.*?Extended Mix.*?\)",
        @"\(.*?Radio Edit.*?\)",
        @"\(.*?Instrumental.*?\)",
        @"\(.*?Acapella.*?\)",
        @"\(.*?Bootleg.*?\)",
        @"\(.*?Dub.*?\)",
        @"\(.*?Edit.*?\)",
        @"\(feat\.?\s+.*?\)",
        @"\(ft\.?\s+.*?\)",
        @"\(with\s+.*?\)"
    };

    // Junk Patterns (REMOVE these - they add no musical value)
    private static readonly string[] JunkPatterns = new[]
    {
        @"\(Official\s+Video.*?\)",
        @"\(Official\s+Audio.*?\)",
        @"\(Official\s+Music\s+Video.*?\)",
        @"\(Audio.*?\)",
        @"\(Video.*?\)",
        @"\(HQ.*?\)",
        @"\(HD.*?\)",
        @"\(1080p.*?\)",
        @"\(720p.*?\)",
        @"\(4K.*?\)",
        @"\[320\]",
        @"\[FLAC\]",
        @"\[WEB\]",
        @"\[VINYL\]",
        @"\[MIX\]",
        @"\[PROMO\]",
        @"\{20\d{2}\}", // Year in curly braces
        @"\(\d+\)$" // Trailing (1), (2) etc.
    };

    public SearchNormalizationService(ILogger<SearchNormalizationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Normalizes artist and title for Soulseek search.
    /// CRITICAL: Preserves musical identity (VIP, Remix, feat) while removing junk.
    /// </summary>
    public (string NormalizedArtist, string NormalizedTitle) NormalizeForSoulseek(string artist, string title)
    {
        var originalTitle = title;
        
        try
        {
            // Step 1: Mark musical identity patterns for protection
            var protectedSegments = new System.Collections.Generic.List<string>();
            var titleWithPlaceholders = title;
            
            for (int i = 0; i < MusicalIdentityPatterns.Length; i++)
            {
                var matches = Regex.Matches(titleWithPlaceholders, MusicalIdentityPatterns[i], RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    var placeholder = $"__PROTECTED_{i}_{protectedSegments.Count}__";
                    protectedSegments.Add(match.Value);
                    titleWithPlaceholders = titleWithPlaceholders.Replace(match.Value, placeholder);
                }
            }

            // Step 2: Remove junk patterns
            foreach (var junkPattern in JunkPatterns)
            {
                titleWithPlaceholders = Regex.Replace(titleWithPlaceholders, junkPattern, "", RegexOptions.IgnoreCase);
            }

            // Step 3: Restore protected segments
            for (int i = 0; i < protectedSegments.Count; i++)
            {
                var placeholder = $"__PROTECTED_{Array.FindIndex(MusicalIdentityPatterns, p => Regex.IsMatch(protectedSegments[i], p, RegexOptions.IgnoreCase))}_{i}__";
                titleWithPlaceholders = titleWithPlaceholders.Replace(placeholder, protectedSegments[i]);
            }

            // Step 4: Clean up spacing and punctuation
            titleWithPlaceholders = Regex.Replace(titleWithPlaceholders, @"\s+", " "); // Collapse spaces
            titleWithPlaceholders = titleWithPlaceholders.Trim(' ', '-', '_', '.', ',');

            // Step 5: CRITICAL - Remove dangling parentheses/brackets
            // This is the bug fix: prevent "Break Wait for You ("
            titleWithPlaceholders = RemoveDanglingParentheses(titleWithPlaceholders);

            // Clean artist similarly (simpler - usually less junk)
            var normalizedArtist = artist.Trim();

            if (titleWithPlaceholders != originalTitle)
            {
                _logger.LogDebug("Normalized title: '{Original}' → '{Normalized}'", originalTitle, titleWithPlaceholders);
            }

            return (normalizedArtist, titleWithPlaceholders);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to normalize search string, using original: '{Title}'", originalTitle);
            return (artist, title); // Fallback to original
        }
    }

    /// <summary>
    /// CRITICAL BUG FIX: Removes dangling opening/closing parentheses or brackets.
    /// Prevents search strings like "Break Wait for You (" which break Soulseek.
    /// </summary>
    private string RemoveDanglingParentheses(string input)
    {
        // Count parentheses
        int openParen = input.Count(c => c == '(');
        int closeParen = input.Count(c => c == ')');
        int openBracket = input.Count(c => c == '[');
        int closeBracket = input.Count(c => c == ']');

        // Remove unmatched opening parentheses at the end
        if (openParen > closeParen)
        {
            // Find and remove trailing opening parentheses
            input = Regex.Replace(input, @"\(\s*$", "").Trim();
        }

        // Remove unmatched closing parentheses at the start
        if (closeParen > openParen)
        {
            input = Regex.Replace(input, @"^\s*\)", "").Trim();
        }

        // Same for brackets
        if (openBracket > closeBracket)
        {
            input = Regex.Replace(input, @"\[\s*$", "").Trim();
        }

        if (closeBracket > openBracket)
        {
            input = Regex.Replace(input, @"^\s*\]", "").Trim();
        }

        return input;
    }

    /// <summary>
    /// Normalizes a filename for display in logs/UI (more aggressive cleanup).
    /// </summary>
    public string NormalizeForLogging(string filename)
    {
        // Remove file extension
        var withoutExt = System.IO.Path.GetFileNameWithoutExtension(filename);
        
        // Remove track numbers
        withoutExt = Regex.Replace(withoutExt, @"^\d+[\.\-_\s]+", "");
        
        // Replace separators with spaces
        withoutExt = withoutExt.Replace('_', ' ').Replace('.', ' ');
        
        // Collapse spaces
        withoutExt = Regex.Replace(withoutExt, @"\s+", " ");
        
        return withoutExt.Trim();
    }

    public SearchPlan BuildSearchPlan(string rawInput)
    {
        var target = ExtractTargetMetadata(rawInput);

        return BuildSearchPlan(target, strictQueryOverride: null);
    }

    public SearchPlan BuildSearchPlan(SearchQuery query)
    {
        var target = ExtractTargetMetadata(query);
        return BuildSearchPlan(target, strictQueryOverride: null);
    }

    public SearchPlan BuildSearchPlan(PlaylistTrack track, string? strictQueryOverride = null)
    {
        var target = ExtractTargetMetadata(track);
        return BuildSearchPlan(target, strictQueryOverride);
    }

    public SearchPlan BuildSearchPlan(TargetMetadata target, string? strictQueryOverride = null)
    {
        var strictQuery = !string.IsNullOrWhiteSpace(strictQueryOverride)
            ? strictQueryOverride.Trim()
            : JoinNonEmpty(target.NormalizedArtist, target.NormalizedTitle);

        var relaxedTitle = BuildRelaxedTitle(target.NormalizedTitle);
        var standardQuery = JoinNonEmpty(target.NormalizedArtist, relaxedTitle);

        if (string.IsNullOrWhiteSpace(standardQuery) && !string.IsNullOrWhiteSpace(target.Album))
        {
            standardQuery = JoinNonEmpty(target.NormalizedArtist, target.Album!);
        }

        if (string.IsNullOrWhiteSpace(standardQuery))
        {
            standardQuery = strictQuery;
        }

        var desperateQuery = BuildDesperateQuery(target, standardQuery);

        return new SearchPlan(target, strictQuery, standardQuery, desperateQuery);
    }

    public TargetMetadata ExtractTargetMetadata(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new TargetMetadata(null, null);
        }

        var clean = rawInput.Trim();

        string? artist = null;
        string? title = null;

        var dashedParts = clean.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (dashedParts.Length == 2)
        {
            artist = dashedParts[0];
            title = dashedParts[1];
        }
        else
        {
            var hyphenParts = clean.Split('-', 2, StringSplitOptions.TrimEntries);
            if (hyphenParts.Length == 2)
            {
                artist = hyphenParts[0];
                title = hyphenParts[1];
            }
            else
            {
                title = clean;
            }
        }

        var normalized = NormalizeForSoulseek(artist ?? string.Empty, title ?? string.Empty);
        return new TargetMetadata(
            string.IsNullOrWhiteSpace(normalized.NormalizedArtist) ? null : normalized.NormalizedArtist,
            string.IsNullOrWhiteSpace(normalized.NormalizedTitle) ? null : normalized.NormalizedTitle);
    }

    public TargetMetadata ExtractTargetMetadata(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var normalized = NormalizeForSoulseek(query.Artist ?? string.Empty, query.Title ?? string.Empty);
        return new TargetMetadata(
            string.IsNullOrWhiteSpace(normalized.NormalizedArtist) ? null : normalized.NormalizedArtist,
            string.IsNullOrWhiteSpace(normalized.NormalizedTitle) ? null : normalized.NormalizedTitle,
            string.IsNullOrWhiteSpace(query.Album) ? null : query.Album.Trim(),
            query.CanonicalDuration.HasValue ? Math.Max(0, query.CanonicalDuration.Value / 1000) : query.Length);
    }

    public TargetMetadata ExtractTargetMetadata(PlaylistTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var normalized = NormalizeForSoulseek(track.Artist ?? string.Empty, track.Title ?? string.Empty);
        return new TargetMetadata(
            string.IsNullOrWhiteSpace(normalized.NormalizedArtist) ? null : normalized.NormalizedArtist,
            string.IsNullOrWhiteSpace(normalized.NormalizedTitle) ? null : normalized.NormalizedTitle,
            string.IsNullOrWhiteSpace(track.Album) ? null : track.Album.Trim(),
            track.CanonicalDuration.HasValue ? Math.Max(0, track.CanonicalDuration.Value / 1000) : null);
    }

    /// <summary>
    /// Generates a prioritized list of search queries to maximize P2P hits.
    /// Solves the "Artist - Title" (hyphen) issue and handles common "garbage" terms.
    /// </summary>
    public System.Collections.Generic.List<string> GenerateSearchVariations(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new System.Collections.Generic.List<string>();
        }

        var plan = BuildSearchPlan(rawInput);
        var variations = plan.EnumerateQueries().ToList();

        var alphaNumeric = Regex.Replace(rawInput.Trim(), @"[^a-zA-Z0-9\s]", " ");
        alphaNumeric = Regex.Replace(alphaNumeric, @"\s+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(alphaNumeric))
        {
            variations.Add(alphaNumeric);
        }

        return variations
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildRelaxedTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var relaxed = title;
        foreach (var pattern in RelaxedTitleNoisePatterns)
        {
            relaxed = Regex.Replace(relaxed, pattern, " ", RegexOptions.IgnoreCase);
        }

        relaxed = Regex.Replace(relaxed, @"[\(\)\[\]\{\}]", " ");
        relaxed = Regex.Replace(relaxed, @"\s+", " ").Trim(' ', '-', '_', '.', ',');
        return relaxed;
    }

    private static string BuildDesperateQuery(TargetMetadata target, string fallback)
    {
        if (target.HasArtist && !IsCommonArtistStopWord(target.NormalizedArtist))
        {
            return target.NormalizedArtist;
        }

        if (target.HasTitle)
        {
            return target.NormalizedTitle;
        }

        if (!string.IsNullOrWhiteSpace(target.Album))
        {
            return target.Album.Trim();
        }

        return fallback;
    }

    private static bool IsCommonArtistStopWord(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return true;
        }

        return ArtistStopWords.Contains(artist.Trim());
    }

    private static string JoinNonEmpty(params string[] parts)
        => string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim()));
}
