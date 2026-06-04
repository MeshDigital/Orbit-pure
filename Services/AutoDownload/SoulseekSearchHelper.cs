using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AutoDownload;

/// <summary>
/// SoulseekSearchHelper — Wrapper for Soulseek.NET search with automatic filter application.
/// 
/// PURPOSE:
/// Encapsulates Soulseek filter tokens (format, minbitrate, minfilesize) and excluded phrases.
/// Applies filters uniformly across all search tiers (exact, template, fallback).
/// 
/// SOULSEEK FILTER CAPABILITIES (Protocol Reference):
/// - minbitrate:N — Minimum bitrate in kbps (e.g., "minbitrate:320")
/// - maxbitrate:N — Maximum bitrate in kbps
/// - mfs:N or minfilesize — Minimum file size in bytes
/// - mxs:N or maxfilesize — Maximum file size in bytes
/// - ext:EXT or format:EXT — File extension filter (e.g., "ext:flac")
/// 
/// EXCLUDED PHRASES:
/// - Server returns list of globally banned phrases (e.g., "fake", "ad", "promo")
/// - This helper respects that list and strips banned terms from queries
/// - Custom excluded phrases can be configured (e.g., "remix", "cover", "live")
/// 
/// PRIVACY:
/// - No PII storage
/// - No query logging beyond local diagnostics
/// - Filters are deterministic given same config
/// </summary>
public class SoulseekSearchHelper
{
    private readonly ILogger<SoulseekSearchHelper> _logger;
    private readonly AppConfig _config;
    private readonly ISoulseekAdapter _soulseekAdapter;
    private HashSet<string> _serverExcludedPhrases = new(StringComparer.OrdinalIgnoreCase);

    public SoulseekSearchHelper(
        ILogger<SoulseekSearchHelper> logger,
        AppConfig config,
        ISoulseekAdapter soulseekAdapter)
    {
        _logger = logger;
        _config = config;
        _soulseekAdapter = soulseekAdapter;
    }

    /// <summary>
    /// Registers excluded phrases returned by the Soulseek server.
    /// These phrases are globally banned on the network and should not appear in searches.
    /// </summary>
    public void RegisterServerExcludedPhrases(IEnumerable<string> phrases)
    {
        _serverExcludedPhrases = new HashSet<string>(phrases ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation("[SoulseekSearchHelper] Registered {Count} server-excluded phrases", _serverExcludedPhrases.Count);
    }

    /// <summary>
    /// Builds a filtered search query with Soulseek filter tokens appended.
    /// 
    /// Rules:
    /// 1. Start with base query
    /// 2. Remove server-excluded phrases
    /// 3. Append format/bitrate/size filters based on config
    /// 4. Respect per-track preferred formats if provided
    /// </summary>
    public string BuildFilteredQuery(
        PlaylistTrack track,
        string baseQuery,
        bool enforceFormatFilters = true)
    {
        var queryParts = new List<string> { baseQuery };

        // Strip server-excluded phrases
        var cleanedQuery = StripExcludedPhrases(baseQuery);
        queryParts.Clear();
        queryParts.Add(cleanedQuery);

        if (!enforceFormatFilters)
        {
            return string.Join(" ", queryParts).Trim();
        }

        // Keep query-time filters aligned with candidate-time strict gate resolution.
        var allowedFormats = AutoDownloadStrictFilterPolicy.ResolveAllowedExtensions(track, _config);
        var minBitrate = AutoDownloadStrictFilterPolicy.ResolveMinBitrateKbps(track, _config);

        // Append Soulseek filter tokens
        var filterTokens = BuildFilterTokens(allowedFormats, minBitrate);
        queryParts.AddRange(filterTokens);

        var finalQuery = string.Join(" ", queryParts).Trim();
        _logger.LogDebug("[SoulseekSearchHelper] Filtered query: {Query}", finalQuery);
        return finalQuery;
    }

    /// <summary>
    /// Streams search candidates for a prepared query and enforces strict candidate caps.
    /// </summary>
    public async IAsyncEnumerable<Track> SearchCandidatesAsync(
        string query,
        List<string> allowedFormats,
        int minBitrate,
        int maxCandidates,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedAllowedFormats = allowedFormats
            .Where(fmt => !string.IsNullOrWhiteSpace(fmt))
            .Select(NormalizeFormat)
            .Where(IsLikelyFormatToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var emitted = 0;
        await foreach (var candidate in _soulseekAdapter.StreamResultsAsync(
            query,
            normalizedAllowedFormats,
            (minBitrate > 0 ? minBitrate : null, null),
            DownloadMode.Normal,
            null,
            ct))
        {
            yield return candidate;
            emitted++;
            if (emitted >= Math.Max(1, maxCandidates))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Builds Soulseek filter tokens based on format and bitrate requirements.
    /// </summary>
    private List<string> BuildFilterTokens(List<string> formats, int minBitrate)
    {
        var tokens = new List<string>();

        // Bitrate filter (apply to all searches)
        if (minBitrate > 0)
        {
            tokens.Add($"minbitrate:{minBitrate}");
        }

        // File size filter (anti-stub)
        if (_config.AutoDownloadMinFileSizeBytes > 0)
        {
            tokens.Add($"mfs:{_config.AutoDownloadMinFileSizeBytes}");
        }

        // Format filter hygiene: only emit ext token when exactly one format is allowed.
        // Multi-format OR chains are not reliably interpreted by Soulseek queries.
        var normalizedFormats = formats
            .Where(fmt => !string.IsNullOrWhiteSpace(fmt))
            .Select(NormalizeFormat)
            .Where(IsLikelyFormatToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedFormats.Count == 1)
        {
            tokens.Add($"ext:{normalizedFormats[0]}");
        }

        _logger.LogDebug(
            "[SoulseekSearchHelper] BuildFilterTokens strategy: formats={FormatCount}, extTokenEmitted={ExtTokenEmitted}",
            normalizedFormats.Count,
            normalizedFormats.Count == 1);

        return tokens;
    }

    /// <summary>
    /// Removes server-excluded and custom-excluded phrases from a query.
    /// </summary>
    private string StripExcludedPhrases(string query)
    {
        var result = query;

        // Strip server-excluded phrases
        foreach (var phrase in _serverExcludedPhrases)
        {
            if (string.IsNullOrWhiteSpace(phrase)) continue;
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                System.Text.RegularExpressions.Regex.Escape(phrase),
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Strip custom excluded phrases (from config)
        if (!string.IsNullOrEmpty(_config.AutoDownloadExcludedPhrases))
        {
            var customExcluded = _config.AutoDownloadExcludedPhrases
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !string.IsNullOrWhiteSpace(p));

            foreach (var phrase in customExcluded)
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    System.Text.RegularExpressions.Regex.Escape(phrase.Trim()),
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }

        // Clean up extra whitespace
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
        return result;
    }

    /// <summary>
    /// Filters candidate results by extension and bitrate constraints.
    /// Returns only candidates that pass all gates.
    /// </summary>
    public IEnumerable<Track> FilterCandidates(
        IEnumerable<Track> candidates,
        List<string> allowedFormats,
        int minBitrate,
        long minFileSize,
        int durationToleranceSeconds = 0,
        int? expectedDurationSeconds = null)
    {
        var normalizedAllowedFormats = allowedFormats
            .Where(fmt => !string.IsNullOrWhiteSpace(fmt))
            .Select(NormalizeFormat)
            .Where(IsLikelyFormatToken)
            .Where(fmt => !string.IsNullOrWhiteSpace(fmt))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedAllowedFormats.Count == 0)
        {
            normalizedAllowedFormats =
            [
                "flac",
                "wav",
                "aiff",
                "aif",
                "ape",
                "alac"
            ];
        }

        foreach (var candidate in candidates)
        {
            var ext = NormalizeFormat(System.IO.Path.GetExtension(candidate.Filename ?? string.Empty));
            var format = NormalizeFormat(candidate.Format);
            if (!IsAllowedFormat(format, normalizedAllowedFormats))
            {
                format = ext;
            }

            // Format gate
            if (!IsAllowedFormat(format, normalizedAllowedFormats))
            {
                continue; // Fail fast on wrong format
            }

            // Bitrate gate
            if (candidate.Bitrate > 0 && candidate.Bitrate < minBitrate)
            {
                continue;
            }

            // File size gate
            if (candidate.Size.HasValue && candidate.Size.Value > 0)
            {
                if (candidate.Size.Value < minFileSize)
                {
                    continue;
                }
            }

            if (expectedDurationSeconds.HasValue && expectedDurationSeconds.Value > 0 && durationToleranceSeconds >= 0)
            {
                var candidateDuration = candidate.Length;
                if (candidateDuration.HasValue && Math.Abs(candidateDuration.Value - expectedDurationSeconds.Value) > durationToleranceSeconds)
                {
                    continue;
                }
            }

            yield return candidate;
        }
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

    private static bool IsAllowedFormat(string format, IReadOnlyCollection<string> normalizedAllowedFormats)
    {
        return !string.IsNullOrWhiteSpace(format)
               && normalizedAllowedFormats.Any(f => f.Equals(format, StringComparison.OrdinalIgnoreCase));
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
    /// Describes a candidate in compact form for logging (no PII except username).
    /// </summary>
    public string DescribeCandidate(Track candidate)
    {
        var ext = NormalizeFormat(System.IO.Path.GetExtension(candidate.Filename ?? string.Empty));
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = NormalizeFormat(candidate.Format);
        }

        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = "unknown";
        }

        var bitrate = candidate.Bitrate;
        var size = candidate.Size ?? 0;
        var queue = candidate.QueueLength;

        return $"{candidate.Username}/{ext}/{bitrate}kbps/{size}B/queue={queue}";
    }
}
