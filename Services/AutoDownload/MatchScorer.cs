using System;
using System.Collections.Generic;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AutoDownload;

/// <summary>
/// MatchScorer — Deterministic scoring function for automatic download candidates.
/// 
/// PURPOSE:
/// Implements exact-match and fallback scoring with weighted components.
/// Ensures deterministic behavior (given same inputs and seed, same score).
/// 
/// SCORING MODEL (100-point scale):
/// - Exactness (50%): filename match ratio, extension match, name distance
/// - Format/Extension (20%): preferred format bonus, no MP3 penalty if lossless-only
/// - Bitrate/Size (15%): bitrate premium for lossless, size reasonability check
/// - Peer Reliability (10%): repeated source preference, queue length penalty
/// - Response Time (5%): prefer faster peers (lower latency bonus)
/// 
/// All weights are tunable via scoring constants.
/// </summary>
public class MatchScorer
{
    /// <summary>
    /// Scoring configuration constants.
    /// </summary>
    public static class ScoringWeights
    {
        public const double ExactnessWeight = 0.50;
        public const double FormatWeight = 0.20;
        public const double BitrateWeight = 0.15;
        public const double ReliabilityWeight = 0.10;
        public const double ResponseTimeWeight = 0.05;

        // Sub-weights for exactness
        public const double ExactFilenameBonus = 40; // points
        public const double ExactExtensionBonus = 10; // points
        public const double ArtistTitleMatchBonus = 30; // points

        // Penalties
        public const double WrongFormatPenalty = -30; // points
        public const double LowBitratePenalty = -15; // points for <320kbps on lossless
        public const double LargeQueuePenalty = -10; // points for queue > 10
    }

    /// <summary>
    /// Scores a single candidate for a track.
    /// Returns score 0-100 (100 = perfect match).
    /// </summary>
    public static double ScoreCandidate(
        PlaylistTrack targetTrack,
        Track candidate,
        MatchScoringOptions? options = null)
    {
        options ??= new MatchScoringOptions();

        double score = 0;

        // 1. EXACTNESS (50 pts max)
        var exactnessScore = ScoreExactness(targetTrack, candidate);
        score += exactnessScore * ScoringWeights.ExactnessWeight * 100;

        // 2. FORMAT/EXTENSION (20 pts max)
        var formatScore = ScoreFormat(targetTrack, candidate, options);
        score += formatScore * ScoringWeights.FormatWeight * 100;

        // 3. BITRATE/SIZE (15 pts max)
        var bitrateScore = ScoreBitrate(candidate, options);
        score += bitrateScore * ScoringWeights.BitrateWeight * 100;

        // 4. PEER RELIABILITY (10 pts max)
        var reliabilityScore = ScoreReliability(candidate, options);
        score += reliabilityScore * ScoringWeights.ReliabilityWeight * 100;

        // 5. RESPONSE TIME (5 pts max)
        var responseScore = ScoreResponseTime(candidate);
        score += responseScore * ScoringWeights.ResponseTimeWeight * 100;

        return Math.Max(0, Math.Min(100, score)); // Clamp to 0-100
    }

    /// <summary>
    /// Scores exactness of filename and metadata match (0.0-1.0).
    /// </summary>
    private static double ScoreExactness(PlaylistTrack targetTrack, Track candidate)
    {
        var targetNorm = NormalizeForComparison($"{targetTrack.Artist} {targetTrack.Title}");
        var candidateNorm = NormalizeForComparison(Path.GetFileNameWithoutExtension(candidate.Filename ?? ""));

        // Exact filename match
        if (targetNorm.Equals(candidateNorm, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0; // Perfect score
        }

        // Partial matches: calculate Levenshtein-like similarity
        var similarity = CalculateSimilarity(targetNorm, candidateNorm);
        return Math.Clamp(similarity, 0.0, 1.0);
    }

    /// <summary>
    /// Scores format/extension match (0.0-1.0).
    /// </summary>
    private static double ScoreFormat(
        PlaylistTrack targetTrack,
        Track candidate,
        MatchScoringOptions? options)
    {
        var candidateExt = NormalizeFormat(Path.GetExtension(candidate.Filename ?? string.Empty));
        var candidateFormat = NormalizeFormat(candidate.Format);
        var allowedExtensions = (options?.AllowedExtensions ?? new List<string> { "flac", "wav", "aiff", "aif" })
            .Where(ext => !string.IsNullOrWhiteSpace(ext))
            .Select(NormalizeFormat)
            .Where(IsLikelyFormatToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allowedExtensions.Count == 0)
        {
            allowedExtensions = new List<string> { "flac", "wav", "aiff", "aif", "ape", "alac" };
        }

        if (!IsAllowedFormat(candidateFormat, allowedExtensions))
        {
            candidateFormat = candidateExt;
        }

        var isAllowedFormat = IsAllowedFormat(candidateFormat, allowedExtensions);

        if (!isAllowedFormat)
        {
            // Optional fallback path: MP3 can be considered, but remains intentionally lower-confidence.
            if (options?.AllowMp3Fallback == true && candidateFormat == "mp3")
            {
                return 0.45;
            }

            return 0.0;
        }

        // Preferred format bonus
        if (candidateFormat == "flac" || candidateFormat == "wav")
        {
            return 1.0; // Full points for lossless
        }

        return 0.8; // Reduced for other formats
    }

    /// <summary>
    /// Scores bitrate and file size reasonableness (0.0-1.0).
    /// </summary>
    private static double ScoreBitrate(Track candidate, MatchScoringOptions? options)
    {
        var minBitrate = options?.MinBitrateKbps ?? 320;
        var candidateBitrate = candidate.Bitrate;

        // Check for suspiciously low bitrate on FLAC
        var format = NormalizeFormat(candidate.Format);
        if (string.IsNullOrWhiteSpace(format))
        {
            format = NormalizeFormat(Path.GetExtension(candidate.Filename ?? string.Empty));
        }

        if (format == "flac" && candidateBitrate > 0 && candidateBitrate < 400)
        {
            return 0.0; // Likely fake FLAC (transcode)
        }

        // Standard bitrate check
        if (candidateBitrate < minBitrate)
        {
            return 0.0;
        }

        // File size check
        var minFileSize = options?.MinFileSizeBytes ?? (500 * 1024); // 500 KB default
        if (candidate.Size.HasValue && candidate.Size.Value < minFileSize)
        {
            return 0.0; // Too small — likely corrupt or stub
        }

        return 1.0; // Good bitrate and size
    }

    /// <summary>
    /// Scores peer reliability (0.0-1.0).
    /// </summary>
    private static double ScoreReliability(Track candidate, MatchScoringOptions? options)
    {
        // Check if peer is in reliable list (repeated source)
        var candidateUsername = (candidate.Username ?? string.Empty).Trim();
        var isRepeatedSource = !string.IsNullOrWhiteSpace(candidateUsername)
            && (options?.RepeatedSources?.Any(source =>
                !string.IsNullOrWhiteSpace(source)
                && source.Trim().Equals(candidateUsername, StringComparison.OrdinalIgnoreCase)) ?? false);
        if (isRepeatedSource)
        {
            return 1.0; // Full bonus for known good peer
        }

        // Check queue length
        var queueLength = Math.Max(0, candidate.QueueLength);
        if (queueLength > 50)
        {
            return 0.1; // Very long queue — likely slow
        }
        if (queueLength > 20)
        {
            return 0.5; // Moderate queue
        }

        return 1.0; // Short or no queue
    }

    /// <summary>
    /// Scores response time (0.0-1.0).
    /// Prefer faster peers (lower latency).
    /// </summary>
    private static double ScoreResponseTime(Track candidate)
    {
        // Skeleton: we don't currently track response time per candidate
        // In a full implementation, would use candidate.ResponseTimeMs or similar
        return 1.0; // Neutral score
    }

    private static string NormalizeFormat(string? format)
    {
        var normalized = (format ?? string.Empty).Trim().ToLowerInvariant();
        normalized = normalized.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '{', '}');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var semicolonSeparator = normalized.IndexOf(';');
        var commaSeparator = normalized.IndexOf(',');
        var metadataSeparator = semicolonSeparator >= 0 && commaSeparator >= 0
            ? Math.Min(semicolonSeparator, commaSeparator)
            : Math.Max(semicolonSeparator, commaSeparator);
        if (metadataSeparator >= 0)
        {
            normalized = normalized[..metadataSeparator].Trim();
            normalized = normalized.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '{', '}');
        }

        var whitespaceSeparator = normalized.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (whitespaceSeparator >= 0)
        {
            normalized = normalized[..whitespaceSeparator].Trim();
            normalized = normalized.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '{', '}');
        }

        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            normalized = normalized[(slashIndex + 1)..].Trim();
            normalized = normalized.Trim('"', '\'', '[', ']', '(', ')', '<', '>', '{', '}');
        }

        normalized = normalized.TrimStart('.');
        if (normalized.StartsWith("x-", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized switch
        {
            "mpeg" => "mp3",
            "mpg" => "mp3",
            "wave" => "wav",
            "vnd.wave" => "wav",
            "mp4" => "m4a",
            _ => normalized
        };
    }

    private static bool IsAllowedFormat(string format, IReadOnlyCollection<string> allowedFormats)
    {
        return !string.IsNullOrWhiteSpace(format)
               && allowedFormats.Any(ext => ext.Equals(format, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyFormatToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' or '+');
    }

    /// <summary>
    /// Normalizes a string for comparison (lowercase, remove punctuation).
    /// </summary>
    private static string NormalizeForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value.ToLowerInvariant();
        // Remove non-alphanumeric except spaces
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^\w\s\-]", "");
        // Collapse whitespace
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    /// <summary>
    /// Calculates similarity ratio between two strings (0.0-1.0).
    /// Uses simple character overlap metric (skeleton).
    /// Full implementation would use Levenshtein or similar.
    /// </summary>
    private static double CalculateSimilarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        // Simple metric: overlap of characters
        var aChars = new HashSet<char>(a);
        var bChars = new HashSet<char>(b);

        var overlap = aChars.Count(c => bChars.Contains(c));
        var total = Math.Max(aChars.Count, bChars.Count);

        return (double)overlap / total;
    }
}

/// <summary>
/// Options for match scoring.
/// </summary>
public class MatchScoringOptions
{
    /// <summary>
    /// Allowed file extensions (e.g., ["flac", "wav"]).
    /// </summary>
    public List<string>? AllowedExtensions { get; set; } = new() { "flac", "wav", "aiff", "aif", "ape", "alac" };

    /// <summary>
    /// Minimum bitrate in kbps (default 320).
    /// </summary>
    public int? MinBitrateKbps { get; set; } = 320;

    /// <summary>
    /// Minimum file size in bytes (default 500 KB).
    /// </summary>
    public long? MinFileSizeBytes { get; set; } = 500 * 1024;

    /// <summary>
    /// Allow MP3 fallback if no lossless available (default false).
    /// </summary>
    public bool AllowMp3Fallback { get; set; } = false;

    /// <summary>
    /// Usernames of repeated/trusted sources (optional).
    /// </summary>
    public HashSet<string>? RepeatedSources { get; set; }

    /// <summary>
    /// Maximum acceptable queue length for fast-lane peers (default 10).
    /// </summary>
    public int FastLaneMaxQueueLength { get; set; } = 10;
}
