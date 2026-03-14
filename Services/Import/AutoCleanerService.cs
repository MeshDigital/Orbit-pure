using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Import;

/// <summary>
/// Service for generating tiered search queries from raw input.
/// Follows the "Dirty -> Smart -> Aggressive" strategy for hit rate optimization.
/// </summary>
public class AutoCleanerService
{
    private readonly ILogger<AutoCleanerService> _logger;
    private readonly SearchNormalizationService _normalizationService;

    public AutoCleanerService(
        ILogger<AutoCleanerService> logger,
        SearchNormalizationService normalizationService)
    {
        _logger = logger;
        _normalizationService = normalizationService;
    }

    /// <summary>
    /// Generates tiered query variations for a given raw track string.
    /// </summary>
    public TieredQueryResult Clean(string rawInput)
    {
        var result = new TieredQueryResult
        {
            Dirty = rawInput.Trim()
        };

        // 1. Smart Clean: leverage improved CommentTracklistParser
        var parsedTracks = Utils.CommentTracklistParser.Parse(rawInput);
        if (parsedTracks.Any())
        {
            var first = parsedTracks.First();
            var (normalizedArtist, normalizedTitle) = _normalizationService.NormalizeForSoulseek(first.Artist ?? string.Empty, first.Title ?? string.Empty);
            result.Smart = string.IsNullOrEmpty(normalizedArtist) ? normalizedTitle : $"{normalizedArtist} - {normalizedTitle}";
        }
        else
        {
            // Fallback for single line or non-standard format
            var parts = rawInput.Split(new[] { " - ", " – ", " — ", " | ", " : " }, 2, StringSplitOptions.RemoveEmptyEntries);
            string artist = parts.Length > 0 ? parts[0] : "";
            string title = parts.Length > 1 ? parts[1] : "";

            if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist))
            {
                title = artist;
                artist = "";
            }

            var (normalizedArtist, normalizedTitle) = _normalizationService.NormalizeForSoulseek(artist, title);
            result.Smart = string.IsNullOrEmpty(normalizedArtist) ? normalizedTitle : $"{normalizedArtist} - {normalizedTitle}";
        }

        // 2. Aggressive Clean: Strip "feat.", "Remastered", "Extended Mix", etc.
        // This is where we remove Musical Identity to maximize hits at the cost of precision.
        result.Aggressive = StripMusicalIdentity(result.Smart);

        _logger.LogDebug("Auto-Clean tiers for '{Input}':\n  Dirty: {Dirty}\n  Smart: {Smart}\n  Aggressive: {Aggressive}", 
            rawInput, result.Dirty, result.Smart, result.Aggressive);

        return result;
    }

    private string StripMusicalIdentity(string query)
    {
        var cleaned = query;

        // Remove (feat. ...), (ft. ...), (with ...)
        cleaned = Regex.Replace(cleaned, @"\s*[\[\(](feat\.?|ft\.?|featuring|with)\s+.*?[\]\)]", "", RegexOptions.IgnoreCase);
        
        // Remove version info
        cleaned = Regex.Replace(cleaned, @"\s*[\[\(](Remastered|Original Mix|Extended Mix|Radio Edit|Instrumental|Acapella|Bootleg|Dub|Edit).*?[\]\)]", "", RegexOptions.IgnoreCase);
        
        // Remove trailing descriptors
        cleaned = Regex.Replace(cleaned, @"\s+(VIP|Remix|Edit|Rework|Mix)$", "", RegexOptions.IgnoreCase);

        // Remove any remaining brackets/parentheses content if it's not the core title
        cleaned = Regex.Replace(cleaned, @"\s*[\[\(].*?[\]\)]", "");

        return cleaned.Trim();
    }
}

public class TieredQueryResult
{
    public string Dirty { get; set; } = string.Empty;
    public string Smart { get; set; } = string.Empty;
    public string Aggressive { get; set; } = string.Empty;
}
