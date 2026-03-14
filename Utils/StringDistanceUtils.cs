using System;
using System.Linq;

namespace SLSKDONET.Utils;

/// <summary>
/// Utility class for string distance calculations and fuzzy matching.
/// Used for resolving missing file paths when tracks are moved or renamed.
/// </summary>
public static class StringDistanceUtils
{
    /// <summary>
    /// Computes the Levenshtein Distance between two strings.
    /// Used for basic fuzzy matching. A lower score is a better match.
    /// </summary>
    /// <param name="s">First string to compare</param>
    /// <param name="t">Second string to compare</param>
    /// <returns>The Levenshtein distance (minimum number of single-character edits)</returns>
    public static int ComputeLevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        // Phase 1.1: Apply filename noise stripping BEFORE normalization
        // This removes uploader tags, video quality markers, etc.
        s = FilenameNormalizer.Normalize(s);
        t = FilenameNormalizer.Normalize(t);

        // Normalize strings (case-insensitive, remove non-alphanumeric for robust matching)
        s = new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        t = new string(t.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        
        int n = s.Length;
        int m = t.Length;
        var d = new int[n + 1, m + 1];

        // Step 1: Initialize
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        // Step 2: Compute distance
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1,      // Deletion
                             d[i, j - 1] + 1),     // Insertion
                             d[i - 1, j - 1] + cost // Substitution
                );
            }
        }

        // Step 3: Result
        return d[n, m];
    }

    /// <summary>
    /// Calculates a matching score (0.0 to 1.0) based on Levenshtein distance.
    /// Higher score is better (1.0 = perfect match, 0.0 = completely different).
    /// </summary>
    /// <param name="s">First string to compare</param>
    /// <param name="t">Second string to compare</param>
    /// <returns>A normalized match score between 0.0 and 1.0</returns>
    public static double GetNormalizedMatchScore(string s, string t)
    {
        if (string.IsNullOrEmpty(s) && string.IsNullOrEmpty(t)) return 1.0;
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(t)) return 0.0;

        int maxLen = Math.Max(s.Length, t.Length);
        int distance = ComputeLevenshteinDistance(s, t);
        
        // Score = 1 - (distance / max length)
        return 1.0 - ((double)distance / maxLen);
    }

    /// <summary>
    /// Normalizes a string for comparison by removing special characters and converting to lowercase.
    /// </summary>
    /// <param name="input">String to normalize</param>
    /// <returns>Normalized string containing only letters and digits in lowercase</returns>
    public static string Normalize(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        
        // Phase 1.1: Use FilenameNormalizer for comprehensive noise stripping
        string cleaned = FilenameNormalizer.Normalize(input);
        return new string(cleaned.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
