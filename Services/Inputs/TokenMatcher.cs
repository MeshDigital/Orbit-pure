using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SLSKDONET.Services.Inputs;

/// <summary>
/// Provides advanced token matching logic for the Search Gatekeeper.
/// Determines if a candidate string (filename) is a valid match for a query.
/// </summary>
public static class TokenMatcher
{
    private static readonly Regex DelimiterRegex = new(@"[\s\-_,\.\(\)\[\]]+", RegexOptions.Compiled);
    private static readonly Regex FeatRegex = new(@"\b(feat|ft|featuring|vs|with|prod)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Checks if the candidate string contains ALL significant tokens from the query.
    /// </summary>
    /// <param name="query">The user's search query.</param>
    /// <param name="candidate">The filename or metadata string to check.</param>
    /// <param name="fuzzyNormalization">If true, ignores "feat.", special chars, and case.</param>
    /// <returns>True if all query tokens are found in the candidate.</returns>
    public static bool MatchesAllTokens(string query, string candidate, bool fuzzyNormalization = true)
    {
        if (string.IsNullOrWhiteSpace(query)) return true; // Empty query matches all (technically)
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var queryTokens = Tokenize(query, fuzzyNormalization);
        var candidateTokens = Tokenize(candidate, fuzzyNormalization);

        // All query tokens must exist in candidate tokens
        // We use a HashSet for candidate tokens for O(1) lookups
        var candidateSet = new HashSet<string>(candidateTokens, StringComparer.OrdinalIgnoreCase);

        return queryTokens.All(qt => candidateSet.Contains(qt));
    }

    /// <summary>
    /// Splits a string into normalized tokens.
    /// </summary>
    public static string[] Tokenize(string input, bool fuzzyNormalization)
    {
        if (string.IsNullOrWhiteSpace(input)) return Array.Empty<string>();

        // 1. Lowercase
        var normalized = input.ToLowerInvariant();

        // 2. Remove file extension if it looks like a filename (simple heuristic)
        if (normalized.Contains('.'))
        {
            int lastDot = normalized.LastIndexOf('.');
            if (lastDot > 0 && normalized.Length - lastDot < 6) // .mp3, .flac
            {
                normalized = normalized.Substring(0, lastDot);
            }
        }

        // 3. Handle "feat." and other joining words if fuzzy
        if (fuzzyNormalization)
        {
            normalized = FeatRegex.Replace(normalized, " ");
        }

        // 4. Split by delimiters
        var tokens = DelimiterRegex.Split(normalized)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // 5. Filter out very short tokens that might be noise (optional, but good for "a", "the")
        // Keeping "a" and "the" might be important for strict matching, so we'll keep them for now.
        // Maybe strict matching implies *every* word.

        return tokens.ToArray();
    }
}
