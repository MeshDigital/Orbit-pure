using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Phase 4.6 Hotfix: Search String Normalization Service.
/// Fixes critical bug where musical identity (VIP, Remix, feat) was stripped or truncated.
/// Prevents search strings like "Break Wait for You (" from breaking Soulseek queries.
/// </summary>
public class SearchNormalizationService
{
    private readonly ILogger<SearchNormalizationService> _logger;

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

    /// <summary>
    /// Generates a prioritized list of search queries to maximize P2P hits.
    /// Solves the "Artist - Title" (hyphen) issue and handles common "garbage" terms.
    /// </summary>
    public System.Collections.Generic.List<string> GenerateSearchVariations(string rawInput)
    {
        var variations = new System.Collections.Generic.List<string>();
        if (string.IsNullOrWhiteSpace(rawInput)) return variations;

        // 1. Exact Input (Cleaned)
        var clean = rawInput.Trim();
        variations.Add(clean);

        // 2. Hyphen Handling (The "Basstripper" Fix)
        if (clean.Contains("-"))
        {
            // Replace hyphen with space
            var noHyphen = clean.Replace("-", " ");
            variations.Add(Regex.Replace(noHyphen, @"\s+", " ").Trim());

            // Split and Swap (Artist - Title -> Title Artist)
            var parts = clean.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
            if (parts.Count == 2)
            {
                variations.Add($"{parts[0]} {parts[1]}"); // Artist Title
                variations.Add($"{parts[1]} {parts[0]}"); // Title Artist
            }
        }

        // 3. Remove "Noise" Terms (Mix names, feats, etc.)
        var noisePatterns = new[] 
        { 
            @"\boriginal mix\b", 
            @"\bextended mix\b", 
            @"\bfeat\.?\b", 
            @"\bft\.?\b", 
            @"\bofficial video\b", 
            @"\blyrics\b",
            @"\bremix\b",
            @"\(\)" // Empty brackets
        };

        var stripped = clean;
        foreach (var pattern in noisePatterns)
        {
            stripped = Regex.Replace(stripped, pattern, "", RegexOptions.IgnoreCase);
        }
        
        // Clean up double spaces
        stripped = Regex.Replace(stripped, @"\s+", " ").Trim();

        if (!string.Equals(stripped, clean, StringComparison.OrdinalIgnoreCase) && stripped.Length > 3)
        {
            variations.Add(stripped);
        }

        // 4. Alpha-Numeric Only (Nuclear Option)
        var alphaNumeric = Regex.Replace(clean, @"[^a-zA-Z0-9\s]", "");
        if (alphaNumeric.Length > 0 && !variations.Contains(alphaNumeric))
        {
            variations.Add(alphaNumeric);
        }

        return variations.Distinct().ToList();
    }
}
