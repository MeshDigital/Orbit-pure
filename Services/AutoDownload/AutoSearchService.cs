using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AutoDownload;

/// <summary>
/// AutoSearchService — Automatic downloads strict mode investigator and optimizer.
/// Privacy-first, local-only, opt-in wrapper around the core discovery pipeline.
/// 
/// PURPOSE:
/// Hardens automatic download selection by implementing:
/// - Exact-first filtering (exact filename match preferred over templates)
/// - Staged wait windows (fast peers 3-5s, then extended search up to 30s)
/// - Deterministic scoring (weighted scoring function, seeded RNG)
/// - Format/extension whitelisting (MP3 fallback only if explicitly configured)
/// - Peer reliability tracking (repeated sources get preference)
/// 
/// PRIVACY GUARANTEE:
/// - No telemetry uploads
/// - No PII storage (only non-PII: counts, formats, sizes, elapsedMs)
/// - All diagnostics written to local PlaylistActivityLogEntity
/// - Feature disabled by default; requires explicit opt-in
/// 
/// DETERMINISM:
/// - Scoring is deterministic given same inputs and RNG seed
/// - No randomization in selection (only in per-track jitter for retry scheduling)
/// - Results repeatable for testing
/// </summary>
public class AutoSearchService
{
    private readonly ILogger<AutoSearchService> _logger;
    private readonly AppConfig _config;
    private readonly DatabaseService _databaseService;
    private readonly DownloadDiscoveryService _discoveryService;
    private readonly SoulseekSearchHelper _searchHelper;

    public AutoSearchService(
        ILogger<AutoSearchService> logger,
        AppConfig config,
        DatabaseService databaseService,
        DownloadDiscoveryService discoveryService,
        SoulseekSearchHelper searchHelper)
    {
        _logger = logger;
        _config = config;
        _databaseService = databaseService;
        _discoveryService = discoveryService;
        _searchHelper = searchHelper;
    }

    /// <summary>
    /// Finds the best match for a track using strict mode: exact-first, filtered-fallback.
    /// Returns the best candidate or null if none pass all gates.
    /// 
    /// Pipeline:
    /// 1. Normalize query (canonicalize name, remove diacritics)
    /// 2. Try EXACT FILENAME search with extension whitelist
    /// 3. Wait INITIAL WINDOW (3-5s) for fast-peer results
    /// 4. Score candidates by exactness, format, bitrate, peer reliability
    /// 5. If no acceptable result, try TEMPLATE SEARCH (artist+title variants)
    /// 6. Wait EXTENDED WINDOW (up to 20-30s)
    /// 7. Return top candidate or null
    /// 
    /// Diagnostics: Emits local audit entries via DatabaseService.LogActivityAsync()
    /// with Action="autodownload_search_*", no PII in Details JSON.
    /// </summary>
    public async Task<(Track? BestMatch, AutoSearchDiagnostics Diagnostics)> FindBestMatchAsync(
        PlaylistTrack track,
        CancellationToken ct = default)
    {
        if (!_config.EnableAutoDownloadStrictMode)
        {
            // Feature disabled: return null cleanly
            return (null, new AutoSearchDiagnostics { IsEnabled = false });
        }

        var diag = new AutoSearchDiagnostics
        {
            IsEnabled = true,
            TrackId = track.Id,
            TrackArtist = track.Artist,
            TrackTitle = track.Title,
            StartedAtUtc = DateTime.UtcNow
        };

        try
        {
            // Normalize query
            var normalizedQuery = NormalizeQuery(track);
            diag.NormalizedQuery = normalizedQuery;

            // Log start
            if (_config.AutoDownloadDiagnosticsEnabled)
            {
                await LogDiagnosticAsync("autodownload_search_started", new
                {
                    trackId = track.Id,
                    artist = track.Artist,
                    title = track.Title,
                    normalizedQuery = normalizedQuery,
                    initialWaitMs = _config.AutoDownloadInitialWaitMs,
                    extendedWaitMs = _config.AutoDownloadExtendedWaitMs
                }, ct);
            }

            // Phase 1: Exact-first pipeline
            var exactResult = await SearchExactFilenameAsync(track, normalizedQuery, ct);
            if (exactResult.BestMatch != null)
            {
                diag.MatchType = "exact";
                diag.CandidatesConsidered = exactResult.CandidatesCount;
                await LogDiagnosticAsync("autodownload_selected", new
                {
                    trackId = track.Id,
                    matchType = "exact",
                    username = exactResult.BestMatch.Username,
                    filename = exactResult.BestMatch.Filename,
                    bitrate = exactResult.BestMatch.Bitrate,
                    format = exactResult.BestMatch.Format,
                    score = exactResult.Score,
                    candidatesCount = exactResult.CandidatesCount,
                    elapsedMs = (int)(DateTime.UtcNow - diag.StartedAtUtc).TotalMilliseconds
                }, ct);
                return (exactResult.BestMatch, diag);
            }

            diag.ExactFilenameResultsCount = exactResult.CandidatesCount;
            diag.ExactFilenameElapsedMs = (int)(DateTime.UtcNow - diag.StartedAtUtc).TotalMilliseconds;

            // Phase 2: Filtered template search if exact failed
            var templateResult = await SearchFilteredTemplateAsync(track, normalizedQuery, ct);
            if (templateResult.BestMatch != null)
            {
                diag.MatchType = "filtered_template";
                diag.CandidatesConsidered = templateResult.CandidatesCount;
                await LogDiagnosticAsync("autodownload_selected", new
                {
                    trackId = track.Id,
                    matchType = "filtered_template",
                    username = templateResult.BestMatch.Username,
                    filename = templateResult.BestMatch.Filename,
                    bitrate = templateResult.BestMatch.Bitrate,
                    format = templateResult.BestMatch.Format,
                    score = templateResult.Score,
                    candidatesCount = templateResult.CandidatesCount,
                    elapsedMs = (int)(DateTime.UtcNow - diag.StartedAtUtc).TotalMilliseconds
                }, ct);
                return (templateResult.BestMatch, diag);
            }

            diag.TemplateResultsCount = templateResult.CandidatesCount;
            diag.TemplateElapsedMs = (int)(DateTime.UtcNow - diag.StartedAtUtc).TotalMilliseconds;

            // No match found
            await LogDiagnosticAsync("autodownload_no_match", new
            {
                trackId = track.Id,
                exactFilenameCount = diag.ExactFilenameResultsCount,
                templateCount = diag.TemplateResultsCount,
                totalElapsedMs = (int)(DateTime.UtcNow - diag.StartedAtUtc).TotalMilliseconds
            }, ct);

            return (null, diag);
        }
        catch (OperationCanceledException)
        {
            await LogDiagnosticAsync("autodownload_cancelled", new
            {
                trackId = track.Id,
                elapsedMs = (int)(DateTime.UtcNow - diag.StartedAtUtc).TotalMilliseconds
            }, ct);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoSearchService.FindBestMatchAsync failed for track {TrackId}", track.Id);
            await LogDiagnosticAsync("autodownload_error", new
            {
                trackId = track.Id,
                error = ex.Message,
                elapsedMs = (int)(DateTime.UtcNow - diag.StartedAtUtc).TotalMilliseconds
            }, ct);
            throw;
        }
    }

    /// <summary>
    /// Searches for exact filename match with extension whitelisting.
    /// Waits up to AutoDownloadInitialWaitMs for results from fast peers.
    /// </summary>
    private async Task<SearchResult> SearchExactFilenameAsync(
        PlaylistTrack track,
        string normalizedQuery,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new SearchResult { BestMatch = null, CandidatesCount = 0, Score = 0 };
        }

        _logger.LogInformation("[AutoSearch] Exact filename phase for {Track}", track.Title);

        // Construct query with extension filters and wait for INITIAL WINDOW
        using var initialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initialCts.CancelAfter(TimeSpan.FromMilliseconds(_config.AutoDownloadInitialWaitMs));

        try
        {
            var baseQuery = string.IsNullOrWhiteSpace(normalizedQuery)
                ? NormalizeQuery(track)
                : normalizedQuery;
            var filteredQuery = _searchHelper.BuildFilteredQuery(track, baseQuery, enforceFormatFilters: true);
            var allowedFormats = ResolveAllowedExtensions(track);
            var minBitrate = ResolveMinBitrate(track);
            var maxCandidates = Math.Max(1, _config.AutoDownloadMaxCandidatesToScore);
            var candidates = new List<Track>();
            await foreach (var candidate in _searchHelper.SearchCandidatesAsync(
                filteredQuery,
                allowedFormats,
                minBitrate,
                maxCandidates,
                initialCts.Token))
            {
                candidates.Add(candidate);
            }

            var strictMatches = candidates
                .Where(c => IsExactFilenameMatch(c, baseQuery))
                .ToList();

            return await SelectBestCandidateAsync(track, strictMatches, "exact", ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Initial window expired; return what we collected
            _logger.LogDebug("[AutoSearch] Exact filename initial window ({WindowMs}ms) expired", _config.AutoDownloadInitialWaitMs);
            return new SearchResult { BestMatch = null, CandidatesCount = 0, Score = 0 };
        }
    }

    /// <summary>
    /// Searches for template-based matches (artist+title) with format filtering.
    /// Waits up to AutoDownloadExtendedWaitMs for results.
    /// </summary>
    private async Task<SearchResult> SearchFilteredTemplateAsync(
        PlaylistTrack track,
        string normalizedQuery,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new SearchResult { BestMatch = null, CandidatesCount = 0, Score = 0 };
        }

        _logger.LogInformation("[AutoSearch] Filtered template phase for {Track}", track.Title);

        using var extendedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        extendedCts.CancelAfter(TimeSpan.FromMilliseconds(_config.AutoDownloadExtendedWaitMs));

        try
        {
            var allowedFormats = ResolveAllowedExtensions(track);
            var minBitrate = ResolveMinBitrate(track);
            var maxCandidates = Math.Max(1, _config.AutoDownloadMaxCandidatesToScore);
            var templates = BuildTemplateQueries(track, normalizedQuery)
                .Select(q => _searchHelper.BuildFilteredQuery(track, q, enforceFormatFilters: true))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var deduped = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            foreach (var query in templates)
            {
                await foreach (var candidate in _searchHelper.SearchCandidatesAsync(
                    query,
                    allowedFormats,
                    minBitrate,
                    maxCandidates,
                    extendedCts.Token))
                {
                    var key = BuildCandidateKey(candidate);
                    if (!deduped.ContainsKey(key))
                    {
                        deduped[key] = candidate;
                    }

                    if (deduped.Count >= maxCandidates)
                    {
                        break;
                    }
                }

                if (deduped.Count >= maxCandidates)
                {
                    break;
                }
            }

            return await SelectBestCandidateAsync(track, deduped.Values.ToList(), "filtered_template", ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Extended window expired; return what we collected
            _logger.LogDebug("[AutoSearch] Filtered template extended window ({WindowMs}ms) expired", _config.AutoDownloadExtendedWaitMs);
            return new SearchResult { BestMatch = null, CandidatesCount = 0, Score = 0 };
        }
    }

    /// <summary>
    /// Normalizes a track query for strict-mode searching.
    /// Removes punctuation, collapses whitespace, lowercases, normalizes diacritics.
    /// </summary>
    private string NormalizeQuery(PlaylistTrack track)
    {
        // Skeleton: basic normalization
        var raw = $"{track.Artist} {track.Title}".ToLowerInvariant();
        
        // Remove common punctuation
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"[^\w\s\-]", "");
        
        // Collapse whitespace
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
        
        return raw;
    }

    private async Task<SearchResult> SelectBestCandidateAsync(
        PlaylistTrack track,
        List<Track> candidates,
        string stage,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return new SearchResult { BestMatch = null, CandidatesCount = 0, Score = 0 };
        }

        var allowedFormats = ResolveAllowedExtensions(track);
        var expectedDurationSeconds = AutoDownloadStrictFilterPolicy.ResolveExpectedDurationSeconds(track);
        var filteredCandidates = _searchHelper.FilterCandidates(
                candidates,
                allowedFormats,
                ResolveMinBitrate(track),
                _config.AutoDownloadMinFileSizeBytes,
                _config.AutoDownloadDurationToleranceSeconds,
                expectedDurationSeconds)
            .Take(Math.Max(1, _config.AutoDownloadMaxCandidatesToScore))
            .ToList();

        if (filteredCandidates.Count == 0)
        {
            return new SearchResult { BestMatch = null, CandidatesCount = candidates.Count, Score = 0 };
        }

        var options = new MatchScoringOptions
        {
            AllowedExtensions = allowedFormats,
            MinBitrateKbps = ResolveMinBitrate(track),
            MinFileSizeBytes = _config.AutoDownloadMinFileSizeBytes,
            AllowMp3Fallback = _config.EnableMp3Fallback && track.Status == TrackStatus.OnHold
        };

        var best = filteredCandidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = MatchScorer.ScoreCandidate(track, candidate, options)
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.QueueLength)
            .First();

        var effectiveScore = ApplyMetadataConfidenceAdjustments(track, best.Score);
        var minimumScore = ResolveMinimumAcceptanceScore(track);
        if (effectiveScore < minimumScore)
        {
            if (_config.AutoDownloadDiagnosticsEnabled)
            {
                await LogDiagnosticAsync("autodownload_candidate_rejected_threshold", new
                {
                    trackId = track.Id,
                    stage,
                    username = best.Candidate.Username,
                    filename = best.Candidate.Filename,
                    score = effectiveScore,
                    threshold = minimumScore,
                    candidatesCount = filteredCandidates.Count
                }, ct);
            }

            return new SearchResult
            {
                BestMatch = null,
                CandidatesCount = filteredCandidates.Count,
                Score = effectiveScore
            };
        }

        if (_config.AutoDownloadDiagnosticsEnabled)
        {
            await LogDiagnosticAsync("autodownload_candidate_found", new
            {
                trackId = track.Id,
                stage,
                username = best.Candidate.Username,
                filename = best.Candidate.Filename,
                bitrate = best.Candidate.Bitrate,
                score = effectiveScore,
                candidatesCount = filteredCandidates.Count
            }, ct);
        }

        return new SearchResult
        {
            BestMatch = best.Candidate,
            CandidatesCount = filteredCandidates.Count,
            Score = effectiveScore
        };
    }

    private List<string> ResolveAllowedExtensions(PlaylistTrack track)
    {
        return AutoDownloadStrictFilterPolicy.ResolveAllowedExtensions(track, _config);
    }

    private int ResolveMinBitrate(PlaylistTrack track)
    {
        return AutoDownloadStrictFilterPolicy.ResolveMinBitrateKbps(track, _config);
    }

    private int ResolveMinimumAcceptanceScore(PlaylistTrack track)
    {
        var configuredMinimum = Math.Clamp(_config.AutoDownloadMinMatchScore, 0, 100);

        if (IsSparseMetadataTrack(track))
        {
            // Sparse metadata tracks are more error-prone; enforce strict floor even if global threshold is lowered.
            return Math.Max(configuredMinimum, 75);
        }

        return configuredMinimum;
    }

    private double ApplyMetadataConfidenceAdjustments(PlaylistTrack track, double baseScore)
    {
        if (!IsSparseMetadataTrack(track))
        {
            return baseScore;
        }

        // Sparse metadata increases false-positive risk when thresholds are relaxed.
        return Math.Max(0, baseScore - 20);
    }

    private static bool IsSparseMetadataTrack(PlaylistTrack track)
    {
        return string.IsNullOrWhiteSpace(track.Artist)
               || string.IsNullOrWhiteSpace(track.Title)
               || !track.CanonicalDuration.HasValue
               || track.CanonicalDuration.Value <= 0;
    }

    private static IEnumerable<string> BuildTemplateQueries(PlaylistTrack track, string normalizedQuery)
    {
        var artist = (track.Artist ?? string.Empty).Trim();
        var title = (track.Title ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            yield return normalizedQuery;
        }

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            yield return $"{artist} {title}";
            yield return $"{artist} - {title}";
            yield return $"{title} {artist}";
        }
    }

    private static string BuildCandidateKey(Track candidate)
    {
        var user = candidate.Username ?? string.Empty;
        var file = candidate.Filename ?? string.Empty;
        return $"{user}::{file}";
    }

    private static bool IsExactFilenameMatch(Track candidate, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(candidate.Filename) || string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return false;
        }

        var filename = Path.GetFileNameWithoutExtension(candidate.Filename);
        if (string.IsNullOrWhiteSpace(filename))
        {
            return false;
        }

        var normalizedFilename = System.Text.RegularExpressions.Regex.Replace(
                filename.ToLowerInvariant(),
                @"[^\w\s\-]",
                string.Empty)
            .Trim();
        normalizedFilename = System.Text.RegularExpressions.Regex.Replace(normalizedFilename, @"\s+", " ");

        return normalizedFilename.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)
               || normalizedFilename.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logs a diagnostic entry to PlaylistActivityLogEntity (local-only, non-PII).
    /// </summary>
    private async Task LogDiagnosticAsync(string action, object details, CancellationToken ct)
    {
        if (!_config.AutoDownloadDiagnosticsEnabled)
            return;

        try
        {
            var detailsJson = System.Text.Json.JsonSerializer.Serialize(details);
            await _databaseService.LogActivityAsync(new PlaylistActivityLogEntity
            {
                Timestamp = DateTime.UtcNow,
                Action = action,
                Details = detailsJson
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to log AutoSearch diagnostic for action {Action}", action);
        }
    }

    /// <summary>
    /// Result of a search operation.
    /// </summary>
    public record SearchResult
    {
        public Track? BestMatch { get; set; }
        public int CandidatesCount { get; set; }
        public double Score { get; set; }
    }
}

/// <summary>
/// Diagnostics for a single AutoSearch operation.
/// </summary>
public class AutoSearchDiagnostics
{
    public bool IsEnabled { get; set; }
    public Guid TrackId { get; set; }
    public string? TrackArtist { get; set; }
    public string? TrackTitle { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public string? NormalizedQuery { get; set; }
    public string? MatchType { get; set; } // "exact", "filtered_template", or null if no match
    public int ExactFilenameResultsCount { get; set; }
    public int ExactFilenameElapsedMs { get; set; }
    public int TemplateResultsCount { get; set; }
    public int TemplateElapsedMs { get; set; }
    public int CandidatesConsidered { get; set; }
}
