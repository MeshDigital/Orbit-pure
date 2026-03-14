using System.Text.RegularExpressions;

namespace SLSKDONET.Utils;

/// <summary>
/// Filename normalization utility for cleaning Soulseek search results.
/// Implements idempotent regex pipeline: Specific → General → Cleanup
/// Based on proven patterns from slsk-batchdl.
/// </summary>
public static class FilenameNormalizer
{
    // ===== Specific Patterns (Highest Priority) =====
    
    /// <summary>
    /// Uploader-specific tags (e.g., [user-tag], {uploader})
    /// </summary>
    private static readonly Regex UploaderTags = new(@"\[[\w\-\.]+\]|\{[\w\-\.]+\}", RegexOptions.Compiled);
    
    /// <summary>
    /// Camelot key notation (e.g., 8A, 12B) - PRESERVE for extraction
    /// </summary>
    private static readonly Regex CamelotKey = new(@"\b([1-9]|1[0-2])[AB]\b", RegexOptions.Compiled);
    
    /// <summary>
    /// BPM notation (e.g., 128bpm, 124 BPM) - PRESERVE for extraction
    /// </summary>
    private static readonly Regex BpmNotation = new(@"\b(\d{2,3})\s*(?:bpm|BPM)\b", RegexOptions.Compiled);
    
    // ===== General Noise Patterns =====
    
    /// <summary>
    /// Common video/quality tags
    /// </summary>
    private static readonly Regex VideoQualityTags = new(
        @"\[(Official\s+)?Video\]|\[HD\]|\[HQ\]|\[4K\]|\[1080p\]|\[720p\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    /// <summary>
    /// Remaster/edition tags
    /// </summary>
    private static readonly Regex EditionTags = new(
        @"\(Remaster(?:ed)?\)|\(Deluxe\s+Edition\)|\(Anniversary\s+Edition\)|\(Expanded\s+Edition\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    /// <summary>
    /// Year tags (e.g., (2024), [2023])
    /// </summary>
    private static readonly Regex YearTags = new(@"[\[\(]\d{4}[\]\)]", RegexOptions.Compiled);
    
    /// <summary>
    /// Explicit content tags
    /// </summary>
    private static readonly Regex ExplicitTags = new(
        @"\[Explicit\]|\(Explicit\)|\[Clean\]|\(Clean\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    /// <summary>
    /// File format tags (redundant, already in extension)
    /// </summary>
    private static readonly Regex FormatTags = new(
        @"\[FLAC\]|\[MP3\]|\[320\]|\[V0\]|\[AAC\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // ===== Cleanup Patterns =====
    
    /// <summary>
    /// Multiple spaces
    /// </summary>
    private static readonly Regex MultipleSpaces = new(@"\s{2,}", RegexOptions.Compiled);
    
    /// <summary>
    /// Underscores to spaces
    /// </summary>
    private static readonly Regex Underscores = new(@"_", RegexOptions.Compiled);
    
    /// <summary>
    /// Leading/trailing delimiters
    /// </summary>
    private static readonly Regex LeadingTrailingDelimiters = new(@"^[\s\-\.]+|[\s\-\.]+$", RegexOptions.Compiled);
    
    /// <summary>
    /// Normalizes a filename for string matching by removing noise.
    /// Preserves BPM and Key tags for extraction.
    /// </summary>
    /// <param name="filename">Raw filename from Soulseek</param>
    /// <returns>Cleaned filename suitable for Levenshtein matching</returns>
    public static string Normalize(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return string.Empty;
        
        string normalized = filename;
        
        // Step 1: Remove uploader-specific tags (highest priority)
        normalized = UploaderTags.Replace(normalized, " ");
        
        // Step 2: Remove general noise (video, quality, edition tags)
        normalized = VideoQualityTags.Replace(normalized, " ");
        normalized = EditionTags.Replace(normalized, " ");
        normalized = YearTags.Replace(normalized, " ");
        normalized = ExplicitTags.Replace(normalized, " ");
        normalized = FormatTags.Replace(normalized, " ");
        
        // Step 3: Cleanup delimiters
        normalized = Underscores.Replace(normalized, " ");
        normalized = MultipleSpaces.Replace(normalized, " ");
        normalized = LeadingTrailingDelimiters.Replace(normalized, "");
        
        return normalized.Trim();
    }
    
    /// <summary>
    /// Normalizes a filename while preserving musical metadata (BPM, Key).
    /// Use this variant when you need to extract BPM/Key AFTER normalization.
    /// </summary>
    /// <param name="filename">Raw filename</param>
    /// <param name="preservedBpm">Extracted BPM if found</param>
    /// <param name="preservedKey">Extracted Camelot key if found</param>
    /// <returns>Normalized filename</returns>
    public static string NormalizeWithExtraction(string filename, out string? preservedBpm, out string? preservedKey)
    {
        preservedBpm = null;
        preservedKey = null;
        
        if (string.IsNullOrWhiteSpace(filename))
            return string.Empty;
        
        // Extract before normalization
        var bpmMatch = BpmNotation.Match(filename);
        if (bpmMatch.Success)
            preservedBpm = bpmMatch.Groups[1].Value;
        
        var keyMatch = CamelotKey.Match(filename);
        if (keyMatch.Success)
            preservedKey = keyMatch.Value;
        
        // Then normalize
        return Normalize(filename);
    }
    
    /// <summary>
    /// Extracts BPM from filename or directory path with decay factor.
    /// Searches parent directories with decreasing confidence.
    /// </summary>
    /// <param name="fullPath">Complete file path including directories</param>
    /// <param name="confidence">Confidence score (1.0 = filename, 0.9 = parent, 0.7 = grandparent)</param>
    /// <returns>BPM value if found</returns>
    public static double? ExtractBpmFromPath(string fullPath, out double confidence)
    {
        confidence = 0.0;
        
        if (string.IsNullOrWhiteSpace(fullPath))
            return null;
        
        // Split path into components
        var parts = fullPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        
        // Search from filename backwards (highest confidence first)
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var match = BpmNotation.Match(parts[i]);
            if (match.Success && double.TryParse(match.Groups[1].Value, out double bpm))
            {
                // Calculate confidence based on depth
                int depth = parts.Length - 1 - i;
                confidence = depth == 0 ? 1.0 : (depth == 1 ? 0.9 : 0.7);
                return bpm;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Extracts Camelot key from filename or path.
    /// </summary>
    /// <param name="fullPath">Complete file path</param>
    /// <returns>Camelot key (e.g., "8A") if found</returns>
    public static string? ExtractKeyFromPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return null;
        
        var match = CamelotKey.Match(fullPath);
        return match.Success ? match.Value : null;
    }
}
