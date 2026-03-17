using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.IO;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Fuzzy matching service for Soulseek search results.
/// Uses Levenshtein Distance and duration tolerance to find the best matching track.
/// </summary>
public class SearchResultMatcher
{
    private static readonly string[] QualityBoosters =
    [
        "WEB", "CD", "VINYL", "LOSSLESS", "REMASTER", "REMASTERED", "24BIT", "24-BIT", "AUDIOPHILE", "DELUXE"
    ];

    private static readonly string[] SourceVerifiers =
    [
        "QOBUZ", "BANDCAMP", "DEEZER", "TIDAL", "ITUNES", "BEATPORT", "DISCOGS"
    ];

    private static readonly string[] JunkPenalties =
    [
        "DOWNLOADS", "DESKTOP", "TEMP", "RECYCLE", "NEW FOLDER", "INCOMING"
    ];

    private readonly ILogger<SearchResultMatcher> _logger;
    private readonly AppConfig _config;

    public SearchResultMatcher(ILogger<SearchResultMatcher> logger, AppConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public record MatchResult(double Score, string? ScoreBreakdown = null, string? RejectionReason = null, string? ShortReason = null);

    /// <summary>
    /// Finds the best matching track from a list of candidates.
    /// Returns null if no acceptable match is found.
    /// </summary>
    public Track? FindBestMatch(PlaylistTrack model, IEnumerable<Track> candidates)
    {
        return FindBestMatchWithDiagnostics(model, candidates).BestMatch;
    }

    public (Track? BestMatch, SearchAttemptLog Log) FindBestMatchWithDiagnostics(PlaylistTrack model, IEnumerable<Track> candidates)
    {
        var log = new SearchAttemptLog
        {
            QueryString = $"{model.Artist} - {model.Title}",
            ResultsCount = candidates.Count()
        };

        if (!candidates.Any()) return (null, log);

        var matches = new List<(Track Track, MatchResult Result)>();
        var rejections = new List<(Track Track, MatchResult Result)>();

        foreach (var candidate in candidates)
        {
            var result = CalculateMatchResult(model, candidate);
            if (result.Score >= 70) // Phase 1.1: Threshold is 70/100
            {
                matches.Add((candidate, result));
            }
            else
            {
                rejections.Add((candidate, result));
                
                if (result.ShortReason?.StartsWith("Duration") == true) log.RejectedByQuality++;
                else if (result.ShortReason?.Contains("Mismatch") == true) log.RejectedByQuality++;
                else if (result.ShortReason?.Contains("Format") == true) log.RejectedByFormat++;
                else if (result.ShortReason?.Contains("Blacklist") == true) log.RejectedByBlacklist++;
            }
        }

        // Capture top 3 rejections for diagnostics
        log.Top3RejectedResults = rejections
            .OrderByDescending(r => r.Result.Score)
            .Take(3)
            .Select((r, i) => new RejectedResult
            {
                Rank = i + 1,
                Username = r.Track.Username ?? "Unknown",
                Artist = r.Track.Artist ?? "Unknown Artist",
                Title = r.Track.Title ?? "Unknown Title",
                Bitrate = r.Track.Bitrate,
                Format = r.Track.Format ?? "Unknown",
                FileSize = r.Track.Size ?? 0,
                Filename = r.Track.Filename ?? "Unknown",
                SearchScore = r.Result.Score,
                ScoreBreakdown = r.Result.ScoreBreakdown,
                RejectionReason = r.Result.RejectionReason ?? "Unknown rejection",
                ShortReason = r.Result.ShortReason ?? "Rejected"
            })
            .ToList();

        if (!matches.Any())
        {
            _logger.LogWarning("No acceptable matches for {Artist} - {Title}. {Summary}", 
                model.Artist, model.Title, log.GetSummary());
            return (null, log);
        }

        var best = matches.OrderByDescending(m => m.Result.Score).First();
        return (best.Track, log);
    }

    /// <summary>
    /// Calculates a match score (0-1) for a single candidate against the requested track.
    /// Publicly exposed for Real-Time "Threshold Trigger" evaluation.
    /// </summary>
    public double CalculateScore(PlaylistTrack model, Track candidate)
    {
        return CalculateMatchResult(model, candidate).Score;
    }

    public MatchResult CalculateMatchResult(PlaylistTrack model, Track candidate)
    {
        double score = 0;
        var breakdown = new List<string>();

        var ext = Path.GetExtension(candidate.Filename ?? string.Empty).ToLowerInvariant();
        var format = (candidate.Format ?? string.Empty).ToLowerInvariant();

        // Purist hard reject: lossy formats are never valid autonomous winners.
        if (ext is ".mp3" or ".m4a" or ".mp4" or ".ogg" or ".wma" ||
            format is "mp3" or "m4a" or "mp4" or "ogg" or "wma")
        {
            return new MatchResult(
                0,
                "Format: Lossy rejected (0)",
                "Lossy format rejected by strict lossless profile.",
                "Format Rejected");
        }

        // 1. Duration Match (Max 40 pts)
        if (model.CanonicalDuration.HasValue && candidate.Length.HasValue)
        {
            var expectedSec = model.CanonicalDuration.Value / 1000;
            var actualSec = candidate.Length.Value;
            var diff = Math.Abs(expectedSec - actualSec);
            
            if (diff <= 2) 
            {
                score += 40;
                breakdown.Add("Duration: Exact/Close (+40)");
            }
            else if (diff <= 5) 
            {
                score += 20;
                breakdown.Add("Duration: Minor Mismatch (+20)");
            }
            else if (diff <= 15)
            {
                score += 5;
                breakdown.Add("Duration: Large Mismatch (+5)");
            }
            else
            {
                breakdown.Add("Duration: Unacceptable (0)");
            }
        }

        // 2. Artist Match (Max 30 pts)
        // Check both filename and path segments for artist tokens
        bool artistMatched = StrictArtistSatisfies(candidate.Filename ?? "", model.Artist);
        if (!artistMatched && candidate.PathSegments != null)
        {
            foreach (var segment in candidate.PathSegments)
            {
                if (StrictArtistSatisfies(segment, model.Artist))
                {
                    artistMatched = true;
                    break;
                }
            }
        }

        if (artistMatched)
        {
            score += 30;
            breakdown.Add("Artist: Matched (+30)");
        }
        else
        {
            breakdown.Add("Artist: Mismatch (0)");
        }

        // 3. Title Match (Max 20 pts)
        bool titleMatched = StrictTitleSatisfies(candidate.Filename ?? "", model.Title);
        if (!titleMatched && candidate.PathSegments != null)
        {
            foreach (var segment in candidate.PathSegments)
            {
                if (StrictTitleSatisfies(segment, model.Title))
                {
                    titleMatched = true;
                    break;
                }
            }
        }

        if (titleMatched)
        {
            score += 20;
            breakdown.Add("Title: Matched (+20)");
        }
        else
        {
            breakdown.Add("Title: Mismatch (0)");
        }

        // 4. Purist Format Priority (AIFF=100, FLAC=90, WAV=80 baseline profile)
        // We apply this as a bounded bonus so metadata fit still matters, but format preference is explicit.
        double formatBonus = 0;
        if (ext is ".aif" or ".aiff" || format is "aif" or "aiff")
        {
            formatBonus = 10;
            breakdown.Add("Format: AIFF priority (+10)");
        }
        else if (ext == ".flac" || format == "flac")
        {
            formatBonus = 9;
            breakdown.Add("Format: FLAC priority (+9)");
        }
        else if (ext == ".wav" || format == "wav")
        {
            formatBonus = 8;
            breakdown.Add("Format: WAV priority (+8)");
        }
        else
        {
            breakdown.Add("Format: Unsupported (0)");
        }

        score += formatBonus;

        // 5. Metadata confidence bonus (sample rate / bit depth / bitrate gate)
        if (candidate.Bitrate > 700)
        {
            score += 4;
            breakdown.Add("Bitrate: >700kbps (+4)");
        }
        else
        {
            breakdown.Add("Bitrate: <=700kbps (0)");
        }

        if (candidate.SampleRate.HasValue && candidate.SampleRate.Value >= 44100)
        {
            score += 3;
            breakdown.Add("SampleRate: >=44.1kHz (+3)");
        }

        if (candidate.BitDepth.HasValue && (candidate.BitDepth.Value == 16 || candidate.BitDepth.Value == 24))
        {
            score += 3;
            breakdown.Add("BitDepth: 16/24-bit (+3)");
        }

        // 6. Context anchors from folder structure / release naming.
        // This is a bounded tie-breaker: curated release folders should beat loose reshared files,
        // but never overpower a bad artist/title/duration fit.
        score += CalculateContextScore(candidate, breakdown);

        string breakdownStr = string.Join(", ", breakdown);
        string? rejection = null;
        if (score < 40) rejection = $"Low match score ({score:F1}). Breakdown: {breakdownStr}";

        return new MatchResult(score, breakdownStr, rejection, score < 40 ? "Low Score" : null);
    }

    private static double CalculateContextScore(Track candidate, List<string> breakdown)
    {
        var pathParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(candidate.Directory))
            pathParts.Add(candidate.Directory);

        if (candidate.PathSegments is { Count: > 0 })
            pathParts.AddRange(candidate.PathSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));

        if (!string.IsNullOrWhiteSpace(candidate.Filename))
            pathParts.Add(candidate.Filename);

        if (pathParts.Count == 0)
            return 0;

        var pathUpper = string.Join(" | ", pathParts).ToUpperInvariant();
        double contextScore = 0;

        foreach (var anchor in SourceVerifiers)
        {
            if (ContainsAnchor(pathUpper, anchor))
            {
                contextScore += 5;
                breakdown.Add($"Context: {anchor} source (+5)");
            }
        }

        foreach (var anchor in QualityBoosters)
        {
            if (ContainsAnchor(pathUpper, anchor))
            {
                contextScore += 3;
                breakdown.Add($"Context: {anchor} anchor (+3)");
            }
        }

        if (Regex.IsMatch(pathUpper, @"[\[\(](19|20)\d{2}[\]\)]"))
        {
            contextScore += 4;
            breakdown.Add("Context: Structured year (+4)");
        }

        if (Regex.IsMatch(pathUpper, @"[\[\(](WEB|FLAC|CD|VINYL|LOSSLESS|24BIT|24-BIT)[\]\)]"))
        {
            contextScore += 4;
            breakdown.Add("Context: Structured release tag (+4)");
        }

        foreach (var anchor in JunkPenalties)
        {
            if (ContainsAnchor(pathUpper, anchor))
            {
                contextScore -= 8;
                breakdown.Add($"Context: {anchor} junk (-8)");
            }
        }

        return Math.Clamp(contextScore, -16, 18);
    }

    private static bool ContainsAnchor(string haystack, string anchor)
    {
        var pattern = $@"(^|[^A-Z0-9]){Regex.Escape(anchor.ToUpperInvariant())}([^A-Z0-9]|$)";
        return Regex.IsMatch(haystack, pattern);
    }

    private List<string> Tokenize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return new List<string>();
        // Normalize handles most furniture. We just split by space and dash.
        return Regex.Split(NormalizeFuzzy(input), @"[\s\-]+")
                    .Where(s => s.Length > 1) // Ignore single chars/tokens
                    .Select(s => s.ToLowerInvariant())
                    .Distinct()
                    .ToList();
    }

    /// <summary>
    /// Phase 1.1: Normalizes an artist name for comparison.
    /// Strips common prefixes ("The", "DJ", "MC"), normalizes "and"/"&"/"vs"/"x" connectors,
    /// and removes possessives.
    /// </summary>
    private string NormalizeArtist(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var s = input.ToLowerInvariant().Trim();

        // Strip leading articles: "the beatles" -> "beatles"
        s = Regex.Replace(s, @"^(the|a)\s+", "", RegexOptions.IgnoreCase);

        // Normalize connectors: "Simon & Garfunkel" / "Simon and Garfunkel" -> "simon garfunkel"
        s = Regex.Replace(s, @"\b(and|&|vs\.?|x|\+)\b", " ", RegexOptions.IgnoreCase);

        // Strip common prefixes that are often omitted in filenames
        s = Regex.Replace(s, @"\b(dj|mc|lil|lil')\b", "", RegexOptions.IgnoreCase);

        // Remove possessives: "Avicii's" -> "Avicii"
        s = Regex.Replace(s, @"'s\b", "");

        // Collapse whitespace
        s = Regex.Replace(s, @"\s+", " ").Trim();

        return s;
    }

    /// <summary>
    /// Phase 1.1: Enhanced token matching with word-boundary checks and Levenshtein fuzzy fallback.
    /// Prevents false positives ("beat" won't match "beatles") while allowing typo tolerance.
    /// </summary>
    private double CalculateTokenMatchRatio(List<string> searchTokens, List<string> haystack)
    {
        if (!searchTokens.Any()) return 1.0;
        
        var haystackNormalized = NormalizeFuzzy(string.Join(" ", haystack));
        var haystackTokens = Regex.Split(haystackNormalized, @"[\s\-_]+")
                                  .Where(s => s.Length > 1)
                                  .Select(s => s.ToLowerInvariant())
                                  .Distinct()
                                  .ToList();

        double totalScore = 0;
        foreach (var token in searchTokens)
        {
            // 1. Exact word-boundary match (best)
            if (ContainsWithBoundary(haystackNormalized, token, ignoreCase: true))
            {
                totalScore += 1.0;
                continue;
            }

            // 2. Fuzzy token match: find best Levenshtein match among haystack tokens
            double bestSimilarity = 0;
            foreach (var ht in haystackTokens)
            {
                double sim = CalculateSimilarity(token, ht);
                if (sim > bestSimilarity) bestSimilarity = sim;
            }

            // Accept fuzzy match if > 85% similar (allows 1-char typos in ~7-char words)
            if (bestSimilarity >= 0.85)
            {
                totalScore += bestSimilarity;
            }
            // 3. Compound word check: "basstripper" contains "bass" as substring
            else if (token.Length >= 4 && haystackNormalized.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                totalScore += 0.7; // Partial credit for substring (no boundary)
            }
        }
        return totalScore / searchTokens.Count;
    }

    /// <summary>
    /// Simple parser to extract BPM from filename (e.g. "128bpm", "(128 BPM)").
    /// </summary>
    private int? ParseBpm(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        try 
        {
            // Simple regex for "128bpm" or "128 bpm"
            var match = System.Text.RegularExpressions.Regex.Match(filename, @"\b(\d{2,3})\s*bpm\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int bpm))
            {
                return bpm;
            }
        } 
        catch { }
        return null;
    }


    /// <summary>
    /// Checks if duration is within acceptable tolerance.
    /// </summary>
    private bool IsDurationAcceptable(int expectedSeconds, int actualSeconds, int toleranceSeconds)
    {
        var difference = Math.Abs(expectedSeconds - actualSeconds);
        var acceptable = difference <= toleranceSeconds;
        
        if (!acceptable)
        {
            _logger.LogDebug(
                "Duration mismatch: expected {Expected}s, actual {Actual}s, tolerance {Tolerance}s",
                expectedSeconds,
                actualSeconds,
                toleranceSeconds);
        }

        return acceptable;
    }

    /// <summary>
    /// Returns a bonus score (0-0.1) based on how close duration is.
    /// Closer duration = higher bonus.
    /// </summary>
    private double GetDurationBonus(int expectedSeconds, int actualSeconds)
    {
        var difference = Math.Abs(expectedSeconds - actualSeconds);
        
        // No bonus if difference > 5 seconds
        if (difference > 5)
            return 0.0;

        // Smooth bonus: 0.1 at 0 difference, 0 at 5+ difference
        return Math.Max(0.0, 0.1 * (1.0 - (difference / 5.0)));
    }

    /// <summary>
    /// Calculates string similarity using Levenshtein Distance.
    /// Returns a score from 0 (completely different) to 1 (identical).
    /// </summary>
    private double CalculateSimilarity(string expected, string actual)
    {
        if (string.IsNullOrEmpty(expected) && string.IsNullOrEmpty(actual))
            return 1.0;

        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
            return 0.0;

        // Normalize for case-insensitive comparison
        var exp = expected.ToLowerInvariant().Trim();
        var act = actual.ToLowerInvariant().Trim();

        if (_config.EnableFuzzyNormalization)
        {
            exp = NormalizeFuzzy(exp);
            act = NormalizeFuzzy(act);
        }

        // Exact match
        if (exp == act)
            return 1.0;

        // Calculate Levenshtein Distance
        var distance = LevenshteinDistance(exp, act);
        var maxLength = Math.Max(exp.Length, act.Length);

        // Convert distance to similarity score
        var similarity = 1.0 - (distance / (double)maxLength);
        return Math.Max(0.0, similarity);
    }

    /// <summary>
    /// Calculates Levenshtein Distance between two strings.
    /// Distance = minimum number of single-character edits (insert, delete, substitute).
    /// </summary>
    private int LevenshteinDistance(string s1, string s2)
    {
        if (s1.Length == 0)
            return s2.Length;

        if (s2.Length == 0)
            return s1.Length;

        var dp = new int[s1.Length + 1, s2.Length + 1];

        // Initialize first row and column
        for (int i = 0; i <= s1.Length; i++)
            dp[i, 0] = i;

        for (int j = 0; j <= s2.Length; j++)
            dp[0, j] = j;

        // Fill the matrix
        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;

                dp[i, j] = Math.Min(
                    Math.Min(
                        dp[i - 1, j] + 1,        // deletion
                        dp[i, j - 1] + 1),      // insertion
                    dp[i - 1, j - 1] + cost);   // substitution
            }
        }

        return dp[s1.Length, s2.Length];
    }

    /// <summary>
    /// Checks if filename contains the expected title with word boundaries.
    /// Based on slsk-batchdl's StrictTitle logic.
    /// </summary>
    private bool StrictTitleSatisfies(string filename, string expectedTitle)
    {
        if (string.IsNullOrEmpty(expectedTitle)) return true;

        // Get filename without extension and path
        var filenameOnly = System.IO.Path.GetFileNameWithoutExtension(filename);
        
        // Normalize both strings
        var normalizedFilename = NormalizeFuzzy(filenameOnly);
        var normalizedTitle = NormalizeFuzzy(expectedTitle);

        // Check if filename contains title with word boundaries
        return ContainsWithBoundary(normalizedFilename, normalizedTitle, ignoreCase: true);
    }

    /// <summary>
    /// Phase 1.1: Enhanced artist matching with multiple fallback strategies.
    /// Checks: (1) Direct boundary match, (2) Normalized/stripped match, (3) Multi-artist split,
    /// (4) Reverse containment, (5) Levenshtein fuzzy match.
    /// </summary>
    private bool StrictArtistSatisfies(string filename, string expectedArtist)
    {
        if (string.IsNullOrEmpty(expectedArtist)) return true;

        // Normalize both strings
        var normalizedFilename = NormalizeFuzzy(filename);
        var normalizedArtist = NormalizeFuzzy(expectedArtist);

        // 1. Standard Check: Filename contains Artist
        if (ContainsWithBoundary(normalizedFilename, normalizedArtist, ignoreCase: true))
            return true;

        // 2. Normalized Artist Check (strips "The", "DJ", "MC", connectors, possessives)
        string strippedArtist = NormalizeArtist(expectedArtist);
        if (!string.IsNullOrEmpty(strippedArtist) && strippedArtist.Length > 2 
            && ContainsWithBoundary(normalizedFilename, strippedArtist, ignoreCase: true))
            return true;

        // 3. Multi-Artist Handling (split by , & / feat and vs)
        var splitArtists = Regex.Split(normalizedArtist, @"[,&/]|\b(feat|ft|vs\.?)\b")
                                .Select(a => a?.Trim() ?? "")
                                .Where(a => a.Length > 2)
                                .ToList();

        if (splitArtists.Count > 1)
        {
            foreach (var subArtist in splitArtists)
            {
                if (ContainsWithBoundary(normalizedFilename, subArtist, ignoreCase: true))
                    return true;

                // Also check sub-artist with prefix leniency
                string strippedSub = NormalizeArtist(subArtist);
                if (!string.IsNullOrEmpty(strippedSub) && strippedSub.Length > 2
                    && ContainsWithBoundary(normalizedFilename, strippedSub, ignoreCase: true))
                    return true;
            }
        }

        // 4. Reverse Containment: Does the filename's artist-like segment contain OUR artist?
        // Handles: Query="Tiësto" but file path has "Tiesto" (accent normalization already handles this)
        // or Query="Deadmau5" but file says "deadmaus" — caught by fuzzy below.

        // 5. Levenshtein Fuzzy Fallback: If the whole-string similarity is > 85%, accept it.
        // This catches typos and minor variations like "Basstripper" vs "Bass Tripper"
        if (strippedArtist.Length > 3)
        {
            double similarity = CalculateSimilarity(strippedArtist, NormalizeArtist(normalizedFilename));
            // Only use this for short filenames (artist-only folder names)
            // For full paths, token matching in CalculateTokenMatchRatio handles it.
            // Here we check if the normalized filename starts with something similar.
            var filenameTokens = Regex.Split(normalizedFilename, @"[\s\-_]+")
                                      .Where(t => t.Length > 2)
                                      .ToList();

            // Build candidate artist strings from first N tokens of filename
            for (int len = 1; len <= Math.Min(3, filenameTokens.Count); len++)
            {
                var candidateArtist = string.Join(" ", filenameTokens.Take(len));
                double sim = CalculateSimilarity(strippedArtist, NormalizeArtist(candidateArtist));
                if (sim >= 0.85)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if haystack contains needle with word boundaries.
    /// Prevents "love" from matching "glove".
    /// </summary>
    private bool ContainsWithBoundary(string haystack, string needle, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(needle)) return true;
        if (string.IsNullOrEmpty(haystack)) return false;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        
        // Find all occurrences
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, comparison)) != -1)
        {
            // Check if this occurrence has word boundaries
            bool leftBoundary = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            bool rightBoundary = (index + needle.Length >= haystack.Length) || !char.IsLetterOrDigit(haystack[index + needle.Length]);

            if (leftBoundary && rightBoundary)
                return true;

            index++;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a string for fuzzy matching by removing special characters,
    /// smart quotes, en-dashes, and normalizing "feat." variants.
    /// </summary>
    private string NormalizeFuzzy(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        // 0. Lowercase immediately to ensure regex [a-z] works and we don't strip uppercase chars
        input = input.ToLowerInvariant();

        // 1. Normalize "feat." variants
        var featNormal = System.Text.RegularExpressions.Regex.Replace(input, @"\b(feat\.?|ft\.?|featuring)\b", "feat", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 2. Normalize dashes (smart quotes, long dashes)
        var dashNormal = featNormal
            .Replace('—', '-') // Em-dash
            .Replace('–', '-') // En-dash
            .Replace('′', '\'') // Smart single quote
            .Replace('‘', '\'') // Smart single quote
            .Replace('’', '\'') // Smart single quote
            .Replace('″', '\"') // Smart double quote
            .Replace('“', '\"') // Smart double quote
            .Replace('”', '\"'); // Smart double quote

        // 3. Remove other non-alphanumeric frictional characters (except space, quote)
        var frictionalNormal = System.Text.RegularExpressions.Regex.Replace(dashNormal, @"[^a-z0-9\s\']", " ");

        // 4. Collapse whitespace
        return System.Text.RegularExpressions.Regex.Replace(frictionalNormal, @"\s+", " ").Trim();
    }
}
